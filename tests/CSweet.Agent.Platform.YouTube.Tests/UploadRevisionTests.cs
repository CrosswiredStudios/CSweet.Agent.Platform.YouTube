using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task ReplayedRevisionClarificationCannotChangeANewerReview()
    {
        var u = new IntakeFixture(CompleteUploadFields);
        Assert.True((await u.F.Converse()).Succeeded);
        var actions = new Dictionary<Guid, ConnectorAction>();
        u.F.Runtime.RegisterCapability<RequestConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRequest, (r, _) =>
        {
            var created = new ConnectorAction(Guid.NewGuid(), r.Capability, "AwaitingApproval", DateTimeOffset.UtcNow);
            actions.Add(created.ActionId, created); return Task.FromResult(created);
        });
        u.F.Runtime.RegisterCapability<ReadConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRead,
            (r, _) => Task.FromResult(actions[r.ActionId]));
        var todo = Assert.Single(u.F.Todos);
        async Task Process() => await new YouTubeManagerAgent(u.F.Model).HandlePersonalTodoAsync(todo, u.F.Context(), default);
        await Process();
        var oldId = u.Root.UploadWork!.ActionId!.Value;
        actions[oldId] = actions[oldId] with { Status = "RevisionRequested", Decision = new("RequestRevision", "Please improve the title", DateTimeOffset.UtcNow) };
        u.F.Model.Enqueue("{}"); await Process();
        Assert.Contains("What should I change", u.Root.UploadWork.Notice);
        var reply = u.Reply("Use the title Updated launch", new(Title: "Updated launch"));
        u.F.FailStateWrite = r => r.StateKey == $"youtube.turn:{reply.MessageId:N}" && r.Payload.GetProperty("response").ValueKind == JsonValueKind.String;
        Assert.False((await u.F.Converse(reply)).Succeeded);
        Assert.Equal(1, u.Root.UploadWork.Revision);
        Assert.Null(u.Root.UploadWork.ActionId);
        Assert.Equal("Updated launch", u.Root.UploadWork.Request.Title);
        u.F.FailStateWrite = null;
        await Process();
        var newId = u.Root.UploadWork.ActionId!.Value;
        Assert.NotEqual(oldId, newId);
        actions[newId] = actions[newId] with { Status = "RevisionRequested", Decision = new("RequestRevision", "A different revision", DateTimeOffset.UtcNow) };
        var calls = u.F.Model.Calls;
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.Equal(calls, u.F.Model.Calls);
        Assert.Equal(1, u.Root.UploadWork.Revision);
        Assert.Equal(2, actions.Count);
    }

    private static UploadFixture RevisionFixture(UploadFields? patch = null) =>
        new(JsonSerializer.Serialize(patch ?? new UploadFields(Title: "Revised launch"), UploadFixture.Json));
    private static Guid RequestUploadRevision(UploadFixture u, string feedback = "Change the title to Revised launch")
    {
        var action = u.Current;
        u.Actions[action.ActionId] = action with { Status = "RevisionRequested",
            Decision = new("RequestRevision", feedback, DateTimeOffset.UtcNow) };
        return action.ActionId;
    }

    [Fact]
    public async Task UploadRevisionRequiresAnotherExactApprovalAndKeepsTheSameSource()
    {
        var u = RevisionFixture(); await u.Process();
        var original = RequestUploadRevision(u);
        var firstKey = u.LastRequest!.IdempotencyKey;
        await u.Process();
        Assert.Equal(1, u.State.UploadWork!.Revision);
        Assert.Equal(original, u.State.UploadWork.PreviousActionId);
        Assert.Null(u.State.UploadWork.ActionId);
        Assert.Equal("Revised launch", u.State.UploadWork.Request.Title);
        Assert.Equal(u.Media, u.State.UploadWork.Media);
        Assert.Single(u.Actions);
        Assert.True(u.F.States.ContainsKey($"youtube.upload-history:{original:N}"));
        await u.Process(); await u.Process();
        Assert.Equal(2, u.Actions.Count);
        Assert.Equal(2, u.Requests);
        Assert.Equal("AwaitingApproval", u.State.UploadWork.Status);
        Assert.NotEqual(firstKey, u.LastRequest!.IdempotencyKey);
        Assert.Equal("RevisionRequested", u.Actions[original].Status);
        Assert.Equal(2, u.F.Sent.Count(x => x.Content.Contains("prepared for approval")));
        Assert.DoesNotContain(u.F.Sent, x => x.Content.Contains("are confirmed"));
        Assert.Equal(1, u.F.Model.Calls);
        var revisedAction = u.State.UploadWork.ActionId!.Value;
        var video = new Video("abcdefghijk", new(Title: "Revised launch", Description: "Description", ChannelId: "channel", CategoryId: "22"),
            Status: new(PrivacyStatus: "private", UploadStatus: "processed", SelfDeclaredMadeForKids: false, ContainsSyntheticMedia: false));
        u.Actions[revisedAction] = u.Actions[revisedAction] with { Status = "Completed", Result = JsonSerializer.SerializeToElement(video, UploadFixture.Json) };
        u.F.Runtime.RegisterCapability<ReadVideoRequest, YouTubePage<Video>>(YouTubeCapabilities.ReadVideo,
            (_, _) => Task.FromResult(new YouTubePage<Video>([video])));
        await u.Process(); await u.Process();
        Assert.Equal("Processed", u.State.UploadWork.Status);
        Assert.Single(u.F.Sent, x => x.Content.Contains("are confirmed"));
        Assert.Equal(2, u.Requests);
    }

    [Fact]
    public async Task UploadRevisionReusesTheSavedModelCandidateAfterALostCheckpoint()
    {
        var u = RevisionFixture(); await u.Process(); RequestUploadRevision(u);
        u.F.FailStateWrite = r => r.StateKey == u.Key && r.Payload.GetProperty("phase").GetString() == "UploadRevisionPrepared";
        await u.Process();
        Assert.Equal(0, u.State.UploadWork!.Revision);
        Assert.Equal(1, u.F.Model.Calls);
        u.F.FailStateWrite = null;
        await u.Process(); await u.Process();
        Assert.Equal(1, u.State.UploadWork.Revision);
        Assert.Equal(1, u.F.Model.Calls);
        Assert.Equal(2, u.Requests);
        Assert.Equal(2, u.Actions.Count);
    }

    [Theory]
    [InlineData("Executing")]
    [InlineData("Completed")]
    [InlineData("Indeterminate")]
    [InlineData("Approved")]
    public async Task ChangedPreviousDecisionBlocksRevisedUploadPreparation(string status)
    {
        var u = RevisionFixture(); await u.Process(); var original = RequestUploadRevision(u);
        await u.Process();
        u.Actions[original] = u.Actions[original] with { Status = status };
        await u.Process();
        Assert.Single(u.Actions); Assert.Equal(1, u.Requests);
        Assert.Null(u.State.UploadWork!.ActionId);
    }

    [Fact]
    public async Task PausedUploadDoesNotGenerateOrSubmitRevisions()
    {
        var u = RevisionFixture(); await u.Process(); RequestUploadRevision(u); u.Pause(true);
        await u.Process();
        Assert.Equal(0, u.F.Model.Calls);
        Assert.Single(u.Actions);
        u.Pause(false); await u.Process();
        Assert.Equal(1, u.F.Model.Calls);
        Assert.Equal(1, u.State.UploadWork!.Revision);
    }

    [Theory]
    [InlineData("unchanged", "What should I change")]
    [InlineData("file", "Changing the video file")]
    [InlineData("schedule", "Scheduled publishing")]
    public async Task UnsupportedOrUnchangedRevisionsSeekDirectionWithoutAnotherUpload(string mode, string notice)
    {
        var patch = mode switch { "file" => new UploadFields(FileName: "another.mp4"), "schedule" => new UploadFields(UploadNow: false), _ => new UploadFields() };
        var u = RevisionFixture(patch); await u.Process(); RequestUploadRevision(u);
        await u.Process(); await u.Process();
        Assert.Single(u.Actions); Assert.Equal(1, u.Requests);
        Assert.Equal(0, u.State.UploadWork!.Revision);
        Assert.Contains(notice, u.State.UploadWork.Notice);
        Assert.Equal(1, u.F.Model.Calls);
        Assert.Single(u.F.Sent, x => x.Content.Contains(notice));
    }
}
