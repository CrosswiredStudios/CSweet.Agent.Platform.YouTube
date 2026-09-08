using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task IncompleteConversationalUploadIntakeStillCannotStartWork()
    {
        var f = new Fixture("Upload my video", Route("Action", "video-upload", "Starting"));
        Assert.False((await f.Converse()).Succeeded);
        Assert.Empty(f.Todos); Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task LongProcessingEscalatesTheExistingVideoWithoutUploadingAgain()
    {
        var u = new UploadFixture(); await u.Process(); u.Complete("uploaded"); await u.Process();
        u.Clock.Now = u.Clock.Now.AddDays(3); await u.Process(); await u.Process();
        Assert.Equal("ReviewRequired", u.State.UploadWork!.Status);
        Assert.Single(u.F.Sent, x => x.Content.Contains("after two days"));
        Assert.Single(u.Actions); Assert.Equal(1, u.Requests);
    }

    [Fact]
    public async Task DuplicateWorkForUncertainVideoCannotCreateAnotherUpload()
    {
        var u = new UploadFixture(); await u.Process(); u.SetStatus("Indeterminate"); await u.Process();
        var messageId = Guid.NewGuid(); var key = $"youtube.turn:{messageId:N}";
        var todo = u.F.Todos[0] with { Id = Guid.NewGuid(), SourceMessageId = messageId, CorrelationId = key };
        var old = u.State; var work = old.UploadWork!;
        var state = old with { Input = old.Input with { MessageId = messageId }, TodoId = todo.Id,
            UploadWork = new(work.Media, work.Request with { IdempotencyKey = $"youtube-upload:{messageId:N}" }, work.ChannelId) };
        u.F.States[key] = u.F.States[u.Key] with { StateKey = key, Payload = JsonSerializer.SerializeToElement(state, UploadFixture.Json) };
        await new YouTubeManagerAgent(u.F.Model).HandlePersonalTodoAsync(todo, u.F.Context(), default);
        Assert.Single(u.Actions); Assert.Equal(1, u.Requests);
    }

    [Fact]
    public async Task PreparedUploadWaitsForExactApprovalAndReportsOnlyReconciledProcessingResult()
    {
        var u = new UploadFixture(); await u.Process(); await u.Process();
        Assert.Single(u.Actions); Assert.Equal("AwaitingApproval", u.State.UploadWork!.Status);
        Assert.DoesNotContain(u.F.Sent, x => x.Content.Contains("are confirmed"));
        u.Complete("uploaded"); await u.Process(); await u.Process();
        Assert.Equal("Processing", u.State.UploadWork.Status);
        Assert.Single(u.F.Sent, x => x.Content.Contains("still processing"));
        u.VideoStatus = "processed"; u.F.FailDelivery = true; await u.Process();
        Assert.Equal("Processed", u.State.UploadWork.Status); Assert.False(u.State.UploadWork.NoticeSent);
        u.F.FailDelivery = false; await u.Process(); await u.Process();
        Assert.Single(u.F.Sent, x => x.Content.Contains("are confirmed"));
        Assert.Equal(1, u.Requests); Assert.True(u.State.UploadWork.NoticeSent);
    }

    [Fact]
    public async Task LostActionCheckpointReusesTheSameUploadAndMediaSource()
    {
        var u = new UploadFixture(); var fail = true;
        u.F.FailStateWrite = r =>
        {
            if (!fail || !r.Payload.TryGetProperty("uploadWork", out var work) || work.ValueKind != JsonValueKind.Object ||
                work.GetProperty("actionId").ValueKind != JsonValueKind.String) return false;
            fail = false; return true;
        };
        await u.Process(); Assert.Null(u.State.UploadWork!.ActionId); Assert.Single(u.Actions);
        await u.Process(); Assert.Equal(2, u.Requests); Assert.Single(u.Actions);
        Assert.Single(u.F.Sent, x => x.Content.Contains("prepared for approval"));
        Assert.Equal(u.Media.Source, u.LastRequest!.MediaSource);
    }

    [Theory]
    [InlineData("Indeterminate")]
    [InlineData("Unavailable")]
    [InlineData("RevisionRequested")]
    public async Task UncertainOrUnapprovedOutcomesNeverCauseAReplacementUpload(string status)
    {
        var u = new UploadFixture(); await u.Process(); u.SetStatus(status);
        await u.Process(); await u.Process();
        Assert.Equal(1, u.Requests); Assert.Single(u.Actions);
        Assert.DoesNotContain(u.F.Sent, x => x.Content.Contains("are confirmed"));
        Assert.True(u.State.UploadWork!.NoticeSent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PauseCancelsOnlyUnstartedUploadAndNeverRestoresIt(bool executing)
    {
        var u = new UploadFixture(); await u.Process();
        if (executing) u.SetStatus("Executing");
        u.Pause(true); await u.Process();
        Assert.Equal(executing ? 0 : 1, u.Cancels);
        Assert.Equal(executing ? "Executing" : "Cancelled", u.State.UploadWork!.Status);
        u.Pause(false); await u.Process(); Assert.Single(u.Actions); Assert.Equal(1, u.Requests);
    }

    [Fact]
    public async Task AlteredSourceOrChannelCannotReachApproval()
    {
        var u = new UploadFixture(); u.ChannelId = "another-channel"; await u.Process();
        Assert.Empty(u.Actions);
        u.ChannelId = "channel";
        u.Source = u.Source with { Attachments = [u.Source.Attachments[0] with { MediaAssetId = Guid.NewGuid() }] };
        await u.Process(); Assert.Empty(u.Actions);
    }

    [Fact]
    public async Task EventHintWakesOnlyTheSavedUploadAndDoesNotProveCompletion()
    {
        var u = new UploadFixture(); await u.Process();
        u.F.Todos[0] = u.F.Todos[0] with { Status = "Running" };
        AgentEventEnvelope Event(Guid id) => new(Guid.NewGuid(), Guid.NewGuid(), ConnectorActionEvents.Changed,
            JsonSerializer.SerializeToElement(new ConnectorActionChanged(id, YouTubeCapabilities.UploadVideo, "Completed"), UploadFixture.Json), DateTimeOffset.UtcNow);
        var agent = new YouTubeManagerAgent(u.F.Model);
        await agent.HandleEventAsync(Event(Guid.NewGuid()), u.F.Context(), default); Assert.Empty(u.F.Requeued);
        await agent.HandleEventAsync(Event(u.Current.ActionId), u.F.Context(), default); Assert.Single(u.F.Requeued);
        await u.Process(); Assert.Equal("AwaitingApproval", u.State.UploadWork!.Status);
    }

    [Theory]
    [InlineData(false, "22")]
    [InlineData(true, "27")]
    public async Task ChangedCategoryCannotReachUploadApproval(bool assignable, string returnedId)
    {
        var u = new UploadFixture();
        u.F.Runtime.RegisterCapability<ListVideoCategoriesRequest, YouTubePage<VideoCategory>>(YouTubeCapabilities.ListVideoCategories,
            (_, _) => Task.FromResult(new YouTubePage<VideoCategory>([new(returnedId, new("People & Blogs", assignable))])));
        await u.Process(); Assert.Empty(u.Actions); Assert.Empty(u.F.Sent);
    }

    [Fact]
    public async Task UploadPreviewShowsProviderCategoryNameNotTechnicalCategoryId()
    {
        var u = new UploadFixture(); await u.Process();
        var preview = Assert.Single(u.F.Sent).Content;
        Assert.Contains("People & Blogs", preview);
        Assert.Contains("Tags:", preview);
        Assert.DoesNotContain("Category: 22", preview);
    }

    private sealed class UploadFixture
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public Fixture F { get; }
        public CommunicationMessage Source;
        public PublicationMedia Media { get; }
        public string Key { get; }
        public string ChannelId = "channel", VideoStatus = "processed";
        public UploadClock Clock { get; } = new();
        public Dictionary<Guid, ConnectorAction> Actions { get; } = [];
        private readonly Dictionary<string, Guid> keys = [];
        public int Requests, Cancels;
        public RequestConnectorAction? LastRequest;
        public TurnState State => F.States[Key].Payload.Deserialize<TurnState>(Json)!;
        public ConnectorAction Current => Actions.Values.Single();
        public UploadFixture(string? revisionOutput = null)
        {
            F = new("unused", revisionOutput ?? "unused");
            var todo = F.SeedPausedDraft(); Key = todo.CorrelationId!;
            var state = F.States[Key].Payload.Deserialize<TurnState>(Json)!;
            Source = PublicationMediaTests.Message(state.Input.MessageId, Guid.Parse(state.Input.ConversationId), F.Sender, state.Input.Prompt);
            Media = PublicationMedia.Capture(Source).Single();
            var request = new UploadVideoRequest(Media.AssetId, "Launch", "Description", "22", "private", false, false, false,
                $"youtube-upload:{state.Input.MessageId:N}");
            F.States[Key] = F.States[Key] with { Payload = JsonSerializer.SerializeToElement(state with
                { Intake = new("Action", "video-upload", "Prepared"), Paused = false,
                    UploadWork = new(Media, request, "channel", Category: new("US", "en_US", "People & Blogs")) }, Json) };
            F.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead,
                (_, _) => Task.FromResult(new CommunicationMessages([Source])));
            F.Runtime.RegisterCapability<RequestConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRequest, (r, _) =>
            {
                Requests++; LastRequest = r; Assert.Equal(YouTubeCapabilities.UploadVideo, r.Capability);
                Assert.Equal(Media.Source, r.MediaSource);
                if (keys.TryGetValue(r.IdempotencyKey, out var id)) return Task.FromResult(Actions[id]);
                var action = new ConnectorAction(Guid.NewGuid(), r.Capability, "AwaitingApproval", DateTimeOffset.UtcNow);
                keys.Add(r.IdempotencyKey, action.ActionId); Actions.Add(action.ActionId, action); return Task.FromResult(action);
            });
            F.Runtime.RegisterCapability<ReadConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRead,
                (r, _) => Task.FromResult(Actions[r.ActionId]));
            F.Runtime.RegisterCapability<CancelConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionCancel, (r, _) =>
            { Cancels++; var action = Actions[r.ActionId] with { Status = "Cancelled" }; Actions[r.ActionId] = action; return Task.FromResult(action); });
            F.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel,
                (_, _) => Task.FromResult(new YouTubePage<Channel>([new(ChannelId, new("Company"))])));
            F.Runtime.RegisterCapability<ReadVideoRequest, YouTubePage<Video>>(YouTubeCapabilities.ReadVideo,
                (_, _) => Task.FromResult(new YouTubePage<Video>([Video()])));
            F.Runtime.RegisterCapability<ListVideoCategoriesRequest, YouTubePage<VideoCategory>>(YouTubeCapabilities.ListVideoCategories,
                (_, _) => Task.FromResult(new YouTubePage<VideoCategory>([new("22", new("People & Blogs", true))])));
        }
        private Video Video() => new("abcdefghijk", new(Title: "Launch", Description: "Description", ChannelId: ChannelId, CategoryId: "22"),
            Status: new(PrivacyStatus: "private", UploadStatus: VideoStatus, SelfDeclaredMadeForKids: false, ContainsSyntheticMedia: false));
        public void Complete(string status) { VideoStatus = status; Actions[Current.ActionId] = Current with { Status = "Completed", Result = JsonSerializer.SerializeToElement(Video(), Json) }; }
        public void SetStatus(string status) => Actions[Current.ActionId] = Current with { Status = status };
        public void Pause(bool paused) => F.States["youtube.preferences"] = F.States[Key] with { StateKey = "youtube.preferences",
            Payload = JsonSerializer.SerializeToElement(new Preferences(new Dictionary<string, string> { ["paused"] = paused ? "true" : "false" }), Json) };
        public Task<PersonalTodoResult> Process() => new YouTubeManagerAgent(F.Model, Clock).HandlePersonalTodoAsync(F.Todos[0], F.Context(), default);
    }

    private sealed class UploadClock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
