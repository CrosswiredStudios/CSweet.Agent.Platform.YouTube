using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task CategoryAnswerResumesTheSameEditWithoutRepeatingAVideoReference()
    {
        var f = new EditFollowupFixture(); await f.Start();
        var reply = f.Add("Use Entertainment in the US");
        Assert.True((await f.Submit(reply)).Succeeded); Assert.Single(f.E.F.Todos); Assert.Single(f.E.F.Requeued);
        Assert.Single(f.E.State.VideoEdit!.Clarifications!); Assert.Equal(1, f.E.State.VideoEdit.Revision);
        f.E.F.Runtime.RegisterCapability<ListVideoCategoriesRequest, YouTubePage<VideoCategory>>(YouTubeCapabilities.ListVideoCategories,
            (request, _) => { Assert.Equal("US", request.RegionCode); return Task.FromResult(new YouTubePage<VideoCategory>([new("24", new("Entertainment", true))])); });
        f.E.F.Model.Enqueue(JsonSerializer.Serialize(new VideoEditFields(Title: "New title", CategoryName: "Entertainment", CategoryRegion: "us"), EditFixture.Json));
        await f.E.Process(); Assert.Single(f.E.Actions);
        Assert.Equal("24", f.E.State.VideoEdit.Request!.CategoryId); Assert.Equal("AwaitingApproval", f.E.Current.Status);
        Assert.Contains("Use Entertainment in the US", f.E.F.Model.LastData);
        Assert.DoesNotContain("original-version", f.E.F.Model.LastData);
    }

    [Fact]
    public async Task ConflictFollowupRefreshesEvidenceButStillRequiresANewDecision()
    {
        var f = new EditFollowupFixture(question: false); await f.Start(); var old = f.E.Current.ActionId;
        f.E.Actions[old] = f.E.Current with { Status = "Blocked", ConditionCode = "resource_changed" };
        var blocked = await f.E.Process(); f.Block(); Assert.Equal("Conflict", f.E.State.VideoEdit!.Status);
        Assert.Equal(PersonalTodoResult.Blocked("The saved edit needs further review; it will not resend automatically."), blocked);
        f.E.Video = f.E.Video with { Etag = "newer-version", Snippet = f.E.Video.Snippet! with { Description = "Preserve this newer human change" } };
        Assert.True((await f.Submit(f.Add("Review the latest version"))).Succeeded);
        f.E.F.Model.Enqueue(JsonSerializer.Serialize(new VideoEditFields(Title: "New title"), EditFixture.Json));
        await f.E.Process(); Assert.Equal(2, f.E.Actions.Count); Assert.Single(f.E.F.Todos);
        Assert.NotEqual(old, f.E.Current.ActionId); Assert.Equal("AwaitingApproval", f.E.Current.Status);
        Assert.Equal("\"newer-version\"", f.E.State.VideoEdit!.Request!.ExpectedETag);
        Assert.Equal("Preserve this newer human change", f.E.State.VideoEdit.Request.Description);
        Assert.Equal(old, f.E.State.VideoEdit.ConflictActionId);
    }

    [Fact]
    public async Task ClarificationDuringAnAuthoritativeRevisionKeepsTheDecisionBinding()
    {
        var f = new EditFollowupFixture(question: false); await f.Start(); var previous = f.E.Current.ActionId;
        f.E.Actions[previous] = f.E.Current with { Status = "RevisionRequested", Decision = new("RequestRevision", "Also change the category", DateTimeOffset.UtcNow) };
        await f.E.Process();
        f.E.F.Model.Enqueue(JsonSerializer.Serialize(new VideoEditFields(Question: "Which category and country should I use?"), EditFixture.Json));
        await f.E.Process(); f.Block(); Assert.Equal("NeedsDetails", f.E.State.VideoEdit!.Status);
        Assert.True((await f.Submit(f.Add("Use Entertainment in the US"))).Succeeded);
        f.E.F.Runtime.RegisterCapability<ListVideoCategoriesRequest, YouTubePage<VideoCategory>>(YouTubeCapabilities.ListVideoCategories,
            (_, _) => Task.FromResult(new YouTubePage<VideoCategory>([new("24", new("Entertainment", true))])));
        f.E.F.Model.Enqueue(JsonSerializer.Serialize(new VideoEditFields(Title: "New title", CategoryName: "Entertainment", CategoryRegion: "US"), EditFixture.Json));
        await f.E.Process(); Assert.Equal(2, f.E.Actions.Count);
        Assert.Equal(previous, f.E.State.VideoEdit.PreviousActionId); Assert.Equal("AwaitingApproval", f.E.Current.Status);
        Assert.Single(f.E.F.Todos);
    }

    [Fact]
    public async Task LostFollowupAnswerCheckpointDoesNotReapplyOrRequeueAgain()
    {
        var f = new EditFollowupFixture(); await f.Start(); var reply = f.Add("Make the title shorter"); var failed = false;
        f.E.F.FailStateWrite = r => !failed && r.Payload.TryGetProperty("input", out var input) && input.GetProperty("messageId").GetGuid() == reply.MessageId &&
            r.Payload.GetProperty("phase").GetString() == "Answered" && (failed = true);
        Assert.False((await f.Submit(reply)).Succeeded); Assert.Single(f.E.F.Requeued);
        Assert.Equal(1, f.E.State.VideoEdit!.Revision);
        Assert.True((await f.E.F.Converse(reply)).Succeeded);
        Assert.Single(f.E.F.Requeued); Assert.Single(f.E.State.VideoEdit.Clarifications!); Assert.Equal(1, f.E.State.VideoEdit.Revision);
        Assert.Equal(3, f.E.F.Model.Calls); Assert.Single(f.E.F.Todos);
    }

    [Fact]
    public async Task LostRootCheckpointRetriesTheSameCorrelatedClarification()
    {
        var f = new EditFollowupFixture(); await f.Start(); var reply = f.Add("Use a warmer title"); var failed = false;
        f.E.F.FailStateWrite = r => !failed && r.Payload.GetProperty("phase").GetString() == "EditFollowupSaved" && (failed = true);
        Assert.False((await f.Submit(reply)).Succeeded); Assert.Empty(f.E.F.Requeued); Assert.Null(f.E.State.VideoEdit!.Clarifications);
        Assert.True((await f.E.F.Converse(reply)).Succeeded); Assert.Single(f.E.F.Requeued);
        Assert.Equal(reply.MessageId, Assert.Single(f.E.State.VideoEdit.Clarifications!).MessageId);
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("conversation")]
    public async Task FollowupCannotInheritAnotherPersonsOrConversationsEdit(string mismatch)
    {
        var f = new EditFollowupFixture(); await f.Start();
        var reply = f.Add("Use a warmer title", mismatch == "requester" ? Guid.NewGuid() : null,
            mismatch == "conversation" ? Guid.NewGuid() : null);
        Assert.True((await f.Submit(reply)).Succeeded);
        Assert.Null(f.E.State.VideoEdit!.Clarifications); Assert.Empty(f.E.F.Requeued); Assert.Single(f.E.F.Todos); Assert.Empty(f.E.Actions);
    }

    [Fact]
    public async Task OlderRoutedReplyCannotReplaceANewerAcceptedReply()
    {
        var f = new EditFollowupFixture(); await f.Start(); var first = f.Add("Make it formal"); var failed = false;
        f.E.F.FailStateWrite = r => !failed && r.Payload.GetProperty("phase").GetString() == "EditFollowupSaved" && (failed = true);
        Assert.False((await f.Submit(first)).Succeeded);
        var second = f.Add("Actually, make it friendly"); Assert.True((await f.Submit(second)).Succeeded);
        var revision = f.E.State.VideoEdit!.Revision;
        var result = await f.E.F.Converse(first); Assert.True(result.Succeeded);
        Assert.Contains("moved on", result.Value!.Value.GetProperty("response").GetString());
        Assert.Equal(revision, f.E.State.VideoEdit.Revision);
        Assert.Equal(second.MessageId, Assert.Single(f.E.State.VideoEdit.Clarifications!).MessageId);
    }

    [Fact]
    public async Task ChangedClarificationSourceCannotDriveADraftOrAction()
    {
        var f = new EditFollowupFixture(); await f.Start(); var reply = f.Add("Use a friendly title");
        await f.Submit(reply);
        var index = f.History.FindIndex(x => x.Id == reply.MessageId);
        f.History[index] = f.History[index] with { Content = "Changed source content" };
        await f.E.Process(); Assert.Empty(f.E.Actions); Assert.Equal(3, f.E.F.Model.Calls);
        Assert.Contains(f.E.F.Sent, x => x.Content.Contains("couldn't safely verify"));
    }

    [Fact]
    public async Task UncertainOutcomeCannotBeReclassifiedAsAConflictByChat()
    {
        var f = new EditFollowupFixture(question: false); await f.Start();
        f.E.Actions[f.E.Current.ActionId] = f.E.Current with { Status = "Indeterminate", ConditionCode = "reconciliation_required" };
        await f.E.Process(); f.Block();
        Assert.True((await f.Submit(f.Add("Review the latest version and try again"))).Succeeded);
        Assert.Equal("ReviewRequired", f.E.State.VideoEdit!.Status); Assert.Null(f.E.State.VideoEdit.Clarifications);
        Assert.Single(f.E.Actions); Assert.Empty(f.E.F.Requeued);
    }

    [Fact]
    public async Task ChangedAuthoritativeConflictReceiptBlocksFollowupBeforeStateReplacement()
    {
        var f = new EditFollowupFixture(question: false); await f.Start(); var id = f.E.Current.ActionId;
        f.E.Actions[id] = f.E.Current with { Status = "Blocked", ConditionCode = "resource_changed" }; await f.E.Process(); f.Block();
        f.E.Actions[id] = f.E.Actions[id] with { Status = "Indeterminate", ConditionCode = "reconciliation_required" };
        Assert.False((await f.Submit(f.Add("Review the latest version"))).Succeeded);
        Assert.Equal("Conflict", f.E.State.VideoEdit!.Status); Assert.Null(f.E.State.VideoEdit.Clarifications); Assert.Empty(f.E.F.Requeued);
    }

    [Theory]
    [InlineData("Information", "none", "What's happening?")]
    [InlineData("Approval", "none", "I approve")]
    public async Task InformationalAndApprovalTurnsDoNotBecomeClarifications(string kind, string intent, string prompt)
    {
        var f = new EditFollowupFixture(); await f.Start(); var reply = f.Add(prompt);
        f.E.F.Model.Enqueue(JsonSerializer.Serialize(new Intake(kind, intent, "Please review the saved request."), EditFixture.Json));
        Assert.True((await f.E.F.Converse(reply)).Succeeded);
        Assert.Null(f.E.State.VideoEdit!.Clarifications); Assert.Empty(f.E.F.Requeued); Assert.Single(f.E.F.Todos);
    }

    private sealed class EditFollowupFixture
    {
        public EditFixture E { get; }
        public List<CommunicationMessage> History { get; } = [];
        public EditFollowupFixture(bool question = true)
        {
            E = new(question ? new(Question: "Which category and country should I use?") : null);
            History.Add(new(E.F.Input.MessageId, 1, Guid.Parse(E.F.Input.ConversationId), E.F.Sender, "Owner", "Human", E.F.Input.Prompt, DateTimeOffset.UtcNow));
            E.F.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(new CommunicationMessages(History.ToArray())));
        }
        public async Task Start() { Assert.True((await E.F.Converse()).Succeeded); await E.Process(); Block(); }
        public void Block() => E.F.Todos[0] = E.F.Todos[0] with { Status = "Blocked" };
        public AssistantRequest Add(string prompt, Guid? requester = null, Guid? conversation = null)
        {
            var input = E.F.Input with { MessageId = Guid.NewGuid(), ChatTurnId = Guid.NewGuid(), Prompt = prompt,
                ConversationId = (conversation ?? Guid.Parse(E.F.Input.ConversationId)).ToString("D") };
            History.Add(new(input.MessageId, History.Count + 1, Guid.Parse(input.ConversationId), requester ?? E.F.Sender, "Participant", "Human", prompt, DateTimeOffset.UtcNow));
            return input;
        }
        public Task<AgentWorkResult> Submit(AssistantRequest input)
        {
            E.F.Model.Enqueue(JsonSerializer.Serialize(new Intake("VideoEditFollowup", "video-edit", "Continuing the saved edit"), EditFixture.Json));
            return E.F.Converse(input);
        }
    }
}
