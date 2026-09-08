using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task VideoEditSavesDraftRequestsExactApprovalAndDeliversOnceAcrossRestarts()
    {
        var e = new EditFixture(); Assert.True((await e.F.Converse()).Succeeded); await e.Process(); await e.Process();
        Assert.Single(e.Actions); Assert.Single(e.F.Todos); Assert.Equal(2, e.F.Model.Calls);
        Assert.Equal("AwaitingApproval", e.State.VideoEdit!.Status); Assert.DoesNotContain("original-version", e.F.Model.LastData);
        Assert.DoesNotContain(e.F.Sent, x => x.Content.Contains("edit is confirmed"));
        e.Complete(); e.F.FailDelivery = true; await e.Process();
        Assert.Equal("Verified", e.State.VideoEdit!.Status); Assert.False(e.State.VideoEdit.NoticeSent);
        e.F.FailDelivery = false; await e.Process(); await e.Process();
        Assert.Single(e.F.Sent, x => x.Content.Contains("edit is confirmed")); Assert.True(e.State.VideoEdit.NoticeSent);
        Assert.Equal(1, e.RequestCalls); Assert.Equal(2, e.F.Model.Calls);
    }

    [Fact]
    public async Task LostEditActionCheckpointReusesTheFrozenKeyAndDraft()
    {
        var e = new EditFixture(); await e.F.Converse(); var failed = false;
        e.F.FailStateWrite = request => !failed && request.Payload.TryGetProperty("phase", out var phase) && phase.GetString() == "EditActionSaved" && (failed = true);
        await e.Process(); Assert.Single(e.Actions); Assert.Null(e.State.VideoEdit!.ActionId);
        await e.Process(); Assert.Single(e.Actions); Assert.Equal(2, e.RequestCalls); Assert.Equal(2, e.F.Model.Calls);
    }

    [Theory]
    [InlineData("Blocked", "resource_changed", "Conflict")]
    [InlineData("Indeterminate", "reconciliation_required", "ReviewRequired")]
    [InlineData("Unavailable", "authority_changed", "Unavailable")]
    public async Task ConflictAndUncertainOutcomesNeverSilentlyResend(string status, string condition, string expected)
    {
        var e = new EditFixture(); await e.F.Converse(); await e.Process();
        e.Actions[e.Current.ActionId] = e.Current with { Status = status, ConditionCode = condition };
        await e.Process(); await e.Process();
        Assert.Equal(expected, e.State.VideoEdit!.Status); Assert.Single(e.Actions); Assert.Equal(1, e.RequestCalls);
        Assert.DoesNotContain(e.F.Sent, x => x.Content.Contains("edit is confirmed"));
    }

    [Fact]
    public async Task RevisionUsesFreshSnapshotAndRequiresAnotherExactDecision()
    {
        var e = new EditFixture(); await e.F.Converse(); await e.Process(); var first = e.Current.ActionId;
        e.Actions[first] = e.Current with { Status = "RevisionRequested", Decision = new("RequestRevision", "Use a shorter title", DateTimeOffset.UtcNow) };
        e.Video = e.Video with { Etag = "new-snapshot", Snippet = e.Video.Snippet! with { Description = "A human changed this description" } };
        e.F.Model.Enqueue(JsonSerializer.Serialize(new VideoEditFields(Title: "Short title"), EditFixture.Json));
        await e.Process(); Assert.Null(e.State.VideoEdit!.ActionId); Assert.Equal(1, e.State.VideoEdit.Revision);
        await e.Process(); Assert.Equal(2, e.Actions.Count); Assert.NotEqual(first, e.Current.ActionId);
        Assert.Equal("\"new-snapshot\"", e.State.VideoEdit.Request!.ExpectedETag);
        Assert.Equal("A human changed this description", e.State.VideoEdit.Request.Description);
        Assert.Equal("AwaitingApproval", e.Current.Status);
    }

    [Fact]
    public async Task RevisionDecisionIsRecheckedAfterDraftPersistence()
    {
        var e = new EditFixture(); await e.F.Converse(); await e.Process(); var id = e.Current.ActionId;
        e.Actions[id] = e.Current with { Status = "RevisionRequested", Decision = new("RequestRevision", "Shorter", DateTimeOffset.UtcNow) };
        await e.Process(); e.F.Model.Enqueue(JsonSerializer.Serialize(new VideoEditFields(Title: "Short"), EditFixture.Json));
        e.F.FailStateWrite = r => {
            if (r.Payload.TryGetProperty("phase", out var phase) && phase.GetString() == "EditDraftSaved")
                e.Actions[id] = e.Actions[id] with { Status = "Rejected" };
            return false;
        };
        await e.Process(); Assert.Single(e.Actions); Assert.Null(e.State.VideoEdit!.ActionId);
        Assert.Single(e.F.Sent, x => x.Content.Contains("couldn't safely verify"));
    }

    [Fact]
    public async Task RepeatingRequestCannotCreateASecondPendingVideoEdit()
    {
        var e = new EditFixture(); await e.F.Converse(); await e.Process();
        var next = e.F.Input with { MessageId = Guid.NewGuid(), ChatTurnId = Guid.NewGuid() };
        e.F.Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(
            new CommunicationMessages([new(next.MessageId, 2, Guid.Parse(next.ConversationId), e.F.Sender, "Owner", "Human", next.Prompt, DateTimeOffset.UtcNow)])));
        e.F.Model.Enqueue(EditFixture.Route);
        Assert.True((await e.F.Converse(next)).Succeeded); Assert.Single(e.F.Todos); Assert.Single(e.Actions);
    }

    [Fact]
    public async Task PauseBeforeDraftDoesNoMutationAndPauseAfterApprovalCancelsOnlyPendingWork()
    {
        var e = new EditFixture(); await e.F.Converse(); e.Pause(true); await e.Process();
        Assert.Empty(e.Actions); Assert.Equal(1, e.F.Model.Calls); Assert.True(e.State.Paused);
        e.Pause(false); await e.Process(); e.Pause(true); await e.Process();
        Assert.Equal("Cancelled", e.State.VideoEdit!.Status); Assert.Equal(1, e.CancelCalls);
        e.Pause(false); await e.Process(); Assert.Single(e.Actions);
    }

    [Fact]
    public async Task PauseDoesNotDescribeExecutingEditsAsCancelled()
    {
        var e = new EditFixture(); await e.F.Converse(); await e.Process();
        e.Actions[e.Current.ActionId] = e.Current with { Status = "Executing" };
        e.Pause(true); await e.Process(); Assert.Equal(0, e.CancelCalls);
        Assert.DoesNotContain(e.F.Sent, x => x.Content.Contains("cancelled"));
        e.Complete(); await e.Process(); Assert.Equal("Verified", e.State.VideoEdit!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VideoTitleLookupRequiresOneAuthenticatedMatchBeforeCreatingWork(bool ambiguous)
    {
        var route = JsonSerializer.Serialize(new Intake("Action", "video-edit", "Preparing", ResourceQuery: "Launch"), EditFixture.Json);
        var e = new EditFixture(prompt: "Rename the video Launch to New title", route: route);
        e.F.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel,
            (_, _) => Task.FromResult(new YouTubePage<Channel>([new("channel", new("Company"), ContentDetails: new(new("uploads")))])));
        e.F.Runtime.RegisterCapability<ListPlaylistItemsRequest, YouTubePage<PlaylistItem>>(YouTubeCapabilities.ListPlaylistItems,
            (_, _) => Task.FromResult(new YouTubePage<PlaylistItem>(ambiguous
                ? [new("one", new(Title: "Launch"), new("abcdefghijk")), new("two", new(Title: "Launch"), new("abcdefghijl"))]
                : [new("one", new(Title: "Launch"), new("abcdefghijk"))])));
        Assert.True((await e.F.Converse()).Succeeded);
        Assert.Equal(ambiguous ? 0 : 1, e.F.Todos.Count); Assert.Empty(e.Actions);
        if (!ambiguous) Assert.Equal("abcdefghijk", e.State.ResolvedResourceId);
    }

    [Fact]
    public async Task StaleNoOpSnapshotCannotClaimTheCurrentMetadataAlreadyMatches()
    {
        var e = new EditFixture(new(Title: "Old title")); await e.F.Converse();
        e.Video = e.Video with { Etag = "changed", Snippet = e.Video.Snippet! with { Title = "A later change" } };
        await e.Process(); Assert.Empty(e.Actions);
        Assert.DoesNotContain(e.F.Sent, x => x.Content.Contains("already present"));
    }

    [Fact]
    public async Task EditWakeValidatesExactWorkAndDoesNotTrustEventStatus()
    {
        var e = new EditFixture(); await e.F.Converse(); await e.Process();
        e.F.Todos[0] = e.F.Todos[0] with { Status = "Running" };
        AgentEventEnvelope Event(Guid id) => new(Guid.NewGuid(), Guid.NewGuid(), ConnectorActionEvents.Changed,
            JsonSerializer.SerializeToElement(new ConnectorActionChanged(id, YouTubeCapabilities.UpdateVideoMetadata, "Completed"), EditFixture.Json), DateTimeOffset.UtcNow);
        var agent = new YouTubeManagerAgent(e.F.Model);
        await agent.HandleEventAsync(Event(Guid.NewGuid()), e.F.Context(), default); Assert.Empty(e.F.Requeued);
        await agent.HandleEventAsync(Event(e.Current.ActionId), e.F.Context(), default); Assert.Single(e.F.Requeued);
        await e.Process(); Assert.Equal("AwaitingApproval", e.State.VideoEdit!.Status);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("source")]
    public async Task ForgedEditWorkCannotRequestAnAction(string problem)
    {
        var e = new EditFixture(); await e.F.Converse(); var item = e.F.Todos[0];
        item = problem == "owner" ? item with { CreatedByOrganizationUserId = Guid.NewGuid() } : item with { SourceMessageId = Guid.NewGuid() };
        await new YouTubeManagerAgent(e.F.Model).HandlePersonalTodoAsync(item, e.F.Context(), default);
        Assert.Empty(e.Actions); Assert.Equal(1, e.F.Model.Calls);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"title\":\"a\",\"title\":\"b\"}")]
    [InlineData("{\"title\":null,\"description\":null,\"tags\":null,\"categoryName\":null,\"categoryRegion\":null,\"defaultLanguage\":null,\"clearDefaultLanguage\":false,\"question\":null,\"privacyStatus\":\"public\"}")]
    public void EditDraftRejectsMissingDuplicateOrUndeclaredFields(string raw) => Assert.Throws<InvalidOperationException>(() => YouTubeManagerAgent.ParseVideoEditFields(raw));

    [Fact]
    public async Task ClarificationAndNoOpDoNotRequestApproval()
    {
        var e = new EditFixture(new(Title: "Old title")); await e.F.Converse(); await e.Process();
        Assert.Empty(e.Actions); Assert.Equal("NoChange", e.State.VideoEdit!.Status);
        var question = new EditFixture(new(Question: "Which category and country should I use?"));
        await question.F.Converse(); await question.Process(); await question.Process();
        Assert.Empty(question.Actions); Assert.Equal("NeedsDetails", question.State.VideoEdit!.Status);
        Assert.Single(question.F.Sent);
    }

    private sealed class EditFixture
    {
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public static string Route => JsonSerializer.Serialize(new Intake("Action", "video-edit", "Preparing", ResourceId: "abcdefghijk"), Json);
        public Fixture F { get; }
        public Dictionary<Guid, ConnectorAction> Actions { get; } = [];
        private readonly Dictionary<string, Guid> keys = [];
        public Video Video { get; set; } = new("abcdefghijk", new("channel", "Old title", "Keep description", CategoryId: "22", Tags: ["keep"], DefaultLanguage: "en"),
            new("private"), Etag: "original-version");
        public int RequestCalls { get; private set; }
        public int CancelCalls { get; private set; }
        public string Key => $"youtube.turn:{F.Input.MessageId:N}";
        public TurnState State => F.States[Key].Payload.Deserialize<TurnState>(Json)!;
        public ConnectorAction Current => Actions[State.VideoEdit!.ActionId!.Value];
        public EditFixture(VideoEditFields? fields = null, string? prompt = null, string? route = null)
        {
            F = new(prompt ?? "Rename https://www.youtube.com/watch?v=abcdefghijk to New title", route ?? Route, JsonSerializer.Serialize(fields ?? new(Title: "New title"), Json));
            F.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel,
                (_, _) => Task.FromResult(new YouTubePage<Channel>([new("channel", new("Company"))])));
            F.Runtime.RegisterCapability<ReadVideoRequest, YouTubePage<Video>>(YouTubeCapabilities.ReadVideo, (r, _) => {
                Assert.Equal(Video.Id, r.VideoId); return Task.FromResult(new YouTubePage<Video>([Video])); });
            F.Runtime.RegisterCapability<RequestConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRequest, (r, _) => {
                RequestCalls++; Assert.Equal(YouTubeCapabilities.UpdateVideoMetadata, r.Capability);
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(State.VideoEdit!.Request, Json), r.Input));
                Assert.True(State.VideoEdit.PreviewSent); Assert.Equal(State.VideoEdit.Request!.IdempotencyKey, r.IdempotencyKey);
                if (keys.TryGetValue(r.IdempotencyKey, out var old)) return Task.FromResult(Actions[old]);
                var action = new ConnectorAction(Guid.NewGuid(), r.Capability, "AwaitingApproval", DateTimeOffset.UtcNow);
                Actions[action.ActionId] = action; keys[r.IdempotencyKey] = action.ActionId; return Task.FromResult(action); });
            F.Runtime.RegisterCapability<ReadConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionRead, (r, _) => Task.FromResult(Actions[r.ActionId]));
            F.Runtime.RegisterCapability<CancelConnectorAction, ConnectorAction>(PlatformCapabilities.ConnectorActionCancel, (r, _) => {
                CancelCalls++; Assert.Contains(Actions[r.ActionId].Status, new[] { "AwaitingApproval", "Approved", "Cancelled" });
                Actions[r.ActionId] = Actions[r.ActionId] with { Status = "Cancelled" }; return Task.FromResult(Actions[r.ActionId]); });
        }
        public Task<PersonalTodoResult> Process() => new YouTubeManagerAgent(F.Model).HandlePersonalTodoAsync(F.Todos[0], F.Context(), default);
        public void Complete()
        {
            var request = State.VideoEdit!.Request!;
            Video = Video with { Snippet = new("channel", request.Title, request.Description, CategoryId: request.CategoryId, Tags: request.Tags, DefaultLanguage: request.DefaultLanguage), Etag = "updated" };
            Actions[Current.ActionId] = Current with { Status = "Completed", Result = JsonSerializer.SerializeToElement(Video, Json) };
        }
        public void Pause(bool paused) => F.States["youtube.preferences"] = F.States[Key] with { StateKey = "youtube.preferences", Revision = 1,
            Payload = JsonSerializer.SerializeToElement(new Preferences(new Dictionary<string, string> { ["paused"] = paused ? "true" : "false" }), Json) };
    }
}
