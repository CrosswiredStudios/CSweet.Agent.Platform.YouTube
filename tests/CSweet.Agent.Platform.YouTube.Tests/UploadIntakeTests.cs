using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    private static readonly JsonSerializerOptions UploadJson = new(JsonSerializerDefaults.Web);
    private static UploadFields CompleteUploadFields => new(Title: "Launch", Description: "Our launch", RegionCode: "US",
        CategoryName: "Education", Privacy: "private", MadeForKids: false, ContainsSyntheticMedia: false,
        NotifySubscribers: false, UploadNow: true);

    [Fact]
    public async Task ConversationHandsOneExactUploadToApprovalAndReportsVerifiedCompletionOnce()
    {
        var u = new IntakeFixture(CompleteUploadFields);
        Assert.True((await u.F.Converse()).Succeeded);
        ConnectorAction? action = null;
        var requests = 0;
        u.F.Runtime.RegisterCapability<RequestConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRequest, (r, _) =>
        {
            requests++;
            Assert.Equal(YouTubeCapabilities.UploadVideo, r.Capability);
            Assert.Equal(u.Root.UploadWork!.Media.Source, r.MediaSource);
            Assert.Equal($"youtube-upload:{u.F.Input.MessageId:N}", r.IdempotencyKey);
            action = new(Guid.NewGuid(), r.Capability, "AwaitingApproval", DateTimeOffset.UtcNow);
            return Task.FromResult(action);
        });
        u.F.Runtime.RegisterCapability<ReadConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRead,
            (r, _) => { Assert.Equal(action!.ActionId, r.ActionId); return Task.FromResult(action); });
        var todo = Assert.Single(u.F.Todos);
        await new YouTubeManagerAgent(u.F.Model).HandlePersonalTodoAsync(todo, u.F.Context(), default);
        Assert.Equal(1, requests);
        Assert.Equal("AwaitingApproval", u.Root.UploadWork!.Status);
        Assert.Single(u.F.Sent);
        var approveInChat = u.Reply("Approved", null, kind: "Approval", intent: "none");
        Assert.True((await u.F.Converse(approveInChat)).Succeeded);
        Assert.Equal("AwaitingApproval", action!.Status);
        Assert.Equal(1, requests);
        var video = new Video("abcdefghijk", new(Title: "Launch", Description: "Our launch", ChannelId: "channel", CategoryId: "27"),
            Status: new(PrivacyStatus: "private", UploadStatus: "processed", SelfDeclaredMadeForKids: false, ContainsSyntheticMedia: false));
        action = action with { Status = "Completed", Result = JsonSerializer.SerializeToElement(video, UploadJson) };
        u.F.Runtime.RegisterCapability<ReadVideoRequest, YouTubePage<Video>>(YouTubeCapabilities.ReadVideo,
            (_, _) => Task.FromResult(new YouTubePage<Video>([video])));
        await new YouTubeManagerAgent(u.F.Model).HandlePersonalTodoAsync(todo, u.F.Context(), default);
        await new YouTubeManagerAgent(u.F.Model).HandlePersonalTodoAsync(todo, u.F.Context(), default);
        Assert.Equal("Processed", u.Root.UploadWork.Status);
        Assert.Equal(2, u.F.Sent.Count);
        Assert.Contains("abcdefghijk", u.F.Sent[1].Content);
        Assert.Equal(1, requests);
        Assert.Single(u.F.Todos);
    }

    [Fact]
    public async Task ConversationalUploadSavesExactPreparedWorkWithoutExecutingIt()
    {
        var u = new IntakeFixture(CompleteUploadFields);
        Assert.True((await u.F.Converse()).Succeeded);
        var work = Assert.IsType<UploadWork>(u.Root.UploadWork);
        Assert.Equal(u.Messages[0].Attachments[0].MediaAssetId, work.Request.MediaAssetId);
        Assert.Equal("27", work.Request.CategoryId);
        Assert.Equal("private", work.Request.PrivacyStatus);
        Assert.False(work.Request.MadeForKids);
        Assert.False(work.Request.ContainsSyntheticMedia);
        Assert.Null(work.ActionId);
        Assert.Single(u.F.Todos);
        Assert.Contains("Nothing has been uploaded", u.Root.Response);
        Assert.True((await u.F.Converse()).Succeeded);
        Assert.Equal(2, u.F.Model.Calls);
        Assert.Single(u.F.Todos);
        Assert.Empty(u.F.Sent);
    }

    [Theory]
    [InlineData("audience", "children")]
    [InlineData("disclosure", "AI-generated")]
    [InlineData("notification", "notify")]
    [InlineData("privacy", "private, unlisted, or public")]
    [InlineData("timing", "upload now")]
    public async Task UploadNeverDefaultsMissingPublicationChoices(string missing, string question)
    {
        var fields = missing switch
        {
            "audience" => CompleteUploadFields with { MadeForKids = null },
            "disclosure" => CompleteUploadFields with { ContainsSyntheticMedia = null },
            "notification" => CompleteUploadFields with { NotifySubscribers = null },
            "privacy" => CompleteUploadFields with { Privacy = null },
            _ => CompleteUploadFields with { UploadNow = null }
        };
        var u = new IntakeFixture(fields);
        Assert.True((await u.F.Converse()).Succeeded);
        Assert.Contains(question, u.Root.UploadIntake!.Question);
        Assert.Null(u.Root.UploadWork);
        Assert.Empty(u.F.Sent);
        Assert.Single(u.F.Todos);
    }

    [Fact]
    public async Task UploadAnswersSurviveNewAgentInstancesAndReuseOneObligation()
    {
        var u = new IntakeFixture(CompleteUploadFields with { MadeForKids = null });
        Assert.True((await u.F.Converse()).Succeeded);
        Assert.Contains("children", u.Root.UploadIntake!.Question);
        Assert.Null(u.Root.UploadWork);
        var originalAnswer = u.Root.Response;
        var reply = u.Reply("No, it is not made for children", new(MadeForKids: false));
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.NotNull(u.Root.UploadWork);
        Assert.False(u.Root.UploadWork.Request.MadeForKids);
        Assert.Equal(originalAnswer, u.Root.Response);
        Assert.Single(u.F.Todos);
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.Equal(4, u.F.Model.Calls);
    }

    [Fact]
    public async Task UploadMayReceiveTheVideoInALaterMessage()
    {
        var u = new IntakeFixture(CompleteUploadFields, attached: false);
        Assert.True((await u.F.Converse()).Succeeded);
        Assert.Contains("attach", u.Root.Response);
        var reply = u.Reply("Here is the video", new(), attach: true);
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.Equal(reply.MessageId, u.Root.UploadWork!.Media.Source.MessageId);
        Assert.Single(u.F.Todos);
    }

    [Fact]
    public async Task LostUploadMergeCheckpointReusesPersistedExtraction()
    {
        var u = new IntakeFixture(CompleteUploadFields with { MadeForKids = null });
        Assert.True((await u.F.Converse()).Succeeded);
        var reply = u.Reply("No", new(MadeForKids: false));
        u.F.FailStateWrite = r => r.StateKey == u.Key && r.Payload.TryGetProperty("uploadIntake", out var intake) &&
            intake.GetProperty("lastMessageId").GetGuid() == reply.MessageId;
        await u.F.Converse(reply);
        Assert.Null(u.Root.UploadWork);
        Assert.Equal(JsonValueKind.Object, u.F.States[$"youtube.turn:{reply.MessageId:N}"].Payload.GetProperty("uploadPatch").ValueKind);
        // A subsequent request can change the latest pointer while this source turn is recovering.
        // Recovery must still apply its saved answer to the original obligation.
        var pointerKey = $"youtube.upload-conversation:{Guid.Parse(u.F.Input.ConversationId):N}:{u.F.Sender:N}";
        u.F.States[pointerKey] = u.F.States[pointerKey] with
            { Payload = JsonSerializer.SerializeToElement(new UploadConversationPointer(Guid.NewGuid(), 10), UploadJson) };
        u.F.FailStateWrite = null;
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.Equal(4, u.F.Model.Calls);
        Assert.NotNull(u.Root.UploadWork);
        Assert.Single(u.F.Todos);
    }

    [Fact]
    public async Task AnotherRequesterCannotAnswerTheOwnersUploadQuestion()
    {
        var u = new IntakeFixture(CompleteUploadFields with { MadeForKids = null });
        Assert.True((await u.F.Converse()).Succeeded);
        var reply = u.Reply("No", new(MadeForKids: false), sender: Guid.NewGuid());
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.Null(u.Root.UploadWork);
        Assert.Null(u.Root.UploadIntake!.Fields.MadeForKids);
        Assert.Single(u.F.Todos);
        Assert.Equal(3, u.F.Model.Calls);
    }

    [Fact]
    public async Task LateUploadAnswerCannotOverwriteNewerDetails()
    {
        var u = new IntakeFixture(CompleteUploadFields with { MadeForKids = null, NotifySubscribers = null });
        Assert.True((await u.F.Converse()).Succeeded);
        var newer = u.Reply("No", new(MadeForKids: false), sequence: 4);
        Assert.True((await u.F.Converse(newer)).Succeeded);
        var older = u.Reply("Yes", new(MadeForKids: true), sequence: 2);
        Assert.True((await u.F.Converse(older)).Succeeded);
        Assert.False(u.Root.UploadIntake!.Fields.MadeForKids);
        Assert.Equal(5, u.F.Model.Calls);
        Assert.Null(u.Root.UploadWork);
    }

    [Theory]
    [InlineData("Information", "none")]
    [InlineData("Approval", "none")]
    public async Task UploadContextDoesNotTurnInformationalOrApprovalTurnsIntoMoreWork(string kind, string intent)
    {
        var u = new IntakeFixture(CompleteUploadFields with { MadeForKids = null });
        Assert.True((await u.F.Converse()).Succeeded);
        var reply = u.Reply("What is the status?", null, kind: kind, intent: intent);
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.Single(u.F.Todos);
        Assert.Null(u.Root.UploadWork);
        Assert.Equal(3, u.F.Model.Calls);
    }

    [Fact]
    public async Task UploadConsentFailureKeepsDetailsAndResumesTheSameRequest()
    {
        var u = new IntakeFixture(CompleteUploadFields);
        u.DenyCategories = true;
        Assert.True((await u.F.Converse()).Succeeded);
        Assert.Equal(1, u.F.SetupActions);
        Assert.Null(u.Root.UploadWork);
        u.DenyCategories = false;
        var reply = u.Reply("Continue the upload", new());
        Assert.True((await u.F.Converse(reply)).Succeeded);
        Assert.NotNull(u.Root.UploadWork);
        Assert.Single(u.F.Todos);
    }

    [Theory]
    [InlineData(PlatformCapabilityErrorCode.BudgetExceeded, "usage limit")]
    [InlineData(PlatformCapabilityErrorCode.Unavailable, "couldn't verify")]
    public async Task UploadProviderFailuresDoNotMasqueradeAsMissingConsent(PlatformCapabilityErrorCode code, string expected)
    {
        var u = new IntakeFixture(CompleteUploadFields);
        u.F.Runtime.RegisterCapability<ListVideoCategoriesRequest, YouTubePage<VideoCategory>>(YouTubeCapabilities.ListVideoCategories,
            (_, _) => throw new PlatformCapabilityException(YouTubeCapabilities.ListVideoCategories, code, "secret-provider-diagnostic"));
        Assert.True((await u.F.Converse()).Succeeded);
        Assert.Contains(expected, u.Root.Response);
        Assert.DoesNotContain("secret-provider-diagnostic", u.Root.Response);
        Assert.Equal(0, u.F.SetupActions);
        Assert.Null(u.Root.UploadWork);
        Assert.Single(u.F.Todos);
    }

    private sealed class IntakeFixture
    {
        public Fixture F { get; }
        public List<CommunicationMessage> Messages { get; } = [];
        public bool DenyCategories { get; set; }
        public string Key => $"youtube.turn:{F.Input.MessageId:N}";
        public TurnState Root => F.States[Key].Payload.Deserialize<TurnState>(UploadJson)!;
        public IntakeFixture(UploadFields fields, bool attached = true)
        {
            F = new("Upload this video now", Route("Upload", "video-upload", "Preparing"), JsonSerializer.Serialize(fields, UploadJson));
            var source = PublicationMediaTests.Message(F.Input.MessageId, Guid.Parse(F.Input.ConversationId), F.Sender, F.Input.Prompt);
            Messages.Add(attached ? source : source with { Attachments = [] });
            F.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (r, _) =>
                Task.FromResult(new CommunicationMessages(Messages.ToArray())));
            F.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, _) =>
                Task.FromResult(new YouTubePage<Channel>([new("channel", new("Company"))])));
            F.Runtime.RegisterCapability<ListVideoCategoriesRequest, YouTubePage<VideoCategory>>(YouTubeCapabilities.ListVideoCategories, (_, _) =>
            {
                if (DenyCategories) throw new PlatformCapabilityException(YouTubeCapabilities.ListVideoCategories, PlatformCapabilityErrorCode.Denied, "secret-provider-diagnostic");
                return Task.FromResult(new YouTubePage<VideoCategory>([new("27", new("Education", true))]));
            });
        }
        public AssistantRequest Reply(string prompt, UploadFields? fields, bool attach = false, Guid? sender = null,
            long? sequence = null, string kind = "UploadFollowup", string intent = "video-upload")
        {
            var input = F.Input with { MessageId = Guid.NewGuid(), ChatTurnId = Guid.NewGuid(), Prompt = prompt };
            var source = PublicationMediaTests.Message(input.MessageId, Guid.Parse(input.ConversationId), sender ?? F.Sender, prompt)
                with { Sequence = sequence ?? Messages.Count + 1 };
            Messages.Add(attach ? source : source with { Attachments = [] });
            F.Model.Enqueue(Route(kind, intent, "Checking"));
            if (fields is not null) F.Model.Enqueue(JsonSerializer.Serialize(fields, UploadJson));
            return input;
        }
    }
}
