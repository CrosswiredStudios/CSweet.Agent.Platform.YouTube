using System.Runtime.CompilerServices;
using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task NamedVideoIsResolvedOnceAndRetriedAgainstTheSavedTarget()
    {
        var route = JsonSerializer.Serialize(new Intake("Read", "video", "Checking", ResourceQuery: "Product Launch"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var f = new Fixture("How is Product Launch doing?", route, "The video has 12 views.");
        var listings = 0; var reads = 0;
        f.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, _) =>
            Task.FromResult(new YouTubePage<Channel>([new("channel-a", new("Company"), ContentDetails: new(new("uploads-a")))])));
        f.Runtime.RegisterCapability<ListPlaylistItemsRequest, YouTubePage<PlaylistItem>>(YouTubeCapabilities.ListPlaylistItems, (_, _) =>
        {
            listings++;
            return Task.FromResult(new YouTubePage<PlaylistItem>([new("item-a", new(Title: "Product Launch", ResourceId: new("video-a")))]));
        });
        f.Runtime.RegisterCapability<ReadVideoRequest, YouTubePage<Video>>(YouTubeCapabilities.ReadVideo, (r, _) =>
        {
            Assert.Equal("video-a", r.VideoId);
            if (++reads == 1) throw new PlatformCapabilityException(YouTubeCapabilities.ReadVideo, PlatformCapabilityErrorCode.Denied, "Temporary loss of access");
            return Task.FromResult(new YouTubePage<Video>([new("video-a", Statistics: new(ViewCount: "12"))]));
        });
        var deferred = await f.Converse();
        Assert.True(deferred.Succeeded);
        Assert.Contains("current channel access", deferred.Value!.Value.GetProperty("response").GetString());
        Assert.Equal(1, f.SetupActions);
        Assert.Equal("video-a", f.States.Values.Single().Payload.GetProperty("resolvedResourceId").GetString());
        Assert.True((await f.Converse()).Succeeded);
        Assert.True((await f.Converse()).Succeeded);
        Assert.Equal(1, listings); Assert.Equal(2, reads); Assert.Equal(2, f.Model.Calls); Assert.Empty(f.Todos);
    }

    [Fact]
    public async Task AmbiguousTitleRespondsWithChoicesWithoutDetailReadOrPhantomWork()
    {
        var route = JsonSerializer.Serialize(new Intake("Read", "playlist-items", "Checking", ResourceQuery: "Launch"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var f = new Fixture("Show the Launch playlist", route);
        f.Runtime.RegisterCapability<ListPageRequest, YouTubePage<Playlist>>(YouTubeCapabilities.ListPlaylists, (_, _) =>
            Task.FromResult(new YouTubePage<Playlist>([new("playlist-a", new(Title: "Launch")), new("playlist-b", new(Title: "Launch"))])));
        var result = await f.Converse();
        Assert.True(result.Succeeded); Assert.Contains("Which one", result.Value!.Value.GetProperty("response").GetString());
        Assert.True((await f.Converse()).Succeeded);
        Assert.Equal(1, f.Model.Calls); Assert.Empty(f.Todos);
    }

    [Theory]
    [InlineData(PlatformCapabilityErrorCode.Denied, "current channel access", 1)]
    [InlineData(PlatformCapabilityErrorCode.NotFound, "couldn't find", 0)]
    [InlineData(PlatformCapabilityErrorCode.BudgetExceeded, "usage limit", 0)]
    [InlineData(PlatformCapabilityErrorCode.Unavailable, "couldn't confirm", 0)]
    public async Task ReadFailuresGiveSafeRecoveryGuidanceWithoutLeakingProviderDiagnostics(PlatformCapabilityErrorCode code, string message, int setupActions)
    {
        var f = new Fixture("How is my channel doing?", Route("Read", "channel", "Checking"));
        var calls = 0;
        f.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, _) =>
        {
            calls++;
            throw new PlatformCapabilityException(YouTubeCapabilities.ReadChannel, code, "secret-provider-diagnostic");
        });
        var result = await f.Converse();
        Assert.True(result.Succeeded);
        var response = result.Value!.Value.GetProperty("response").GetString();
        Assert.Contains(message, response); Assert.DoesNotContain("secret-provider-diagnostic", response);
        Assert.Equal(setupActions, f.SetupActions); Assert.Equal(1, calls); Assert.Equal(1, f.Model.Calls); Assert.Empty(f.Todos);
        var state = Assert.Single(f.States).Value.Payload;
        Assert.Equal("ReadDeferred", state.GetProperty("phase").GetString());
        Assert.Equal(JsonValueKind.Null, state.GetProperty("response").ValueKind);
    }

    [Theory]
    [InlineData("invented-id", null)]
    [InlineData(null, "Unmentioned title")]
    public async Task UngroundedModelResourceCannotStartProviderReads(string? id, string? query)
    {
        var route = JsonSerializer.Serialize(new Intake("Read", "video", "Checking", ResourceId: id, ResourceQuery: query), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var f = new Fixture("How is Product Launch doing?", route);
        Assert.False((await f.Converse()).Succeeded); Assert.Equal(0, f.ProviderReads); Assert.Empty(f.Todos);
    }

    [Fact]
    public async Task ManagerResumeRequeuesExistingPausedWorkWithoutNewCardsOrGeneration()
    {
        var f = new Fixture("Resume paused work", Route("Preference", "none", "Resuming", preference: "paused", value: "false"));
        var paused = f.SeedPausedDraft();
        Assert.True((await f.Converse()).Succeeded);
        Assert.Equal(new[] { paused.Id }, f.Requeued);
        Assert.Single(f.Todos);
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(f.Todos[0], f.Context(), default);
        Assert.Equal(1, f.Model.Calls);
        Assert.Equal("Previously generated draft", Assert.Single(f.Sent).Content);
    }

    [Fact]
    public async Task NonManagerResumeCannotWakePausedWork()
    {
        var f = new Fixture("Resume paused work", Route("Preference", "none", "Resuming", preference: "paused", value: "false"));
        f.SeedPausedDraft(); f.Manager = Guid.NewGuid();
        Assert.True((await f.Converse()).Succeeded);
        Assert.Empty(f.Requeued);
        Assert.Equal("Running", Assert.Single(f.Todos).Status);
    }

    [Fact]
    public async Task PausePreservesGeneratedDraftAndDoesNotCallModelOrProvider()
    {
        var f = new Fixture("Pause work", Route("Preference", "none", "Paused", preference: "paused", value: "true"));
        var paused = f.SeedPausedDraft();
        Assert.True((await f.Converse()).Succeeded);
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(paused, f.Context(), default);
        Assert.Equal(1, f.Model.Calls); Assert.Empty(f.Sent); Assert.Equal(0, f.ProviderReads);
        Assert.Equal("Generated", f.States[paused.CorrelationId!].Payload.GetProperty("phase").GetString());
        Assert.True(f.States[paused.CorrelationId!].Payload.GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task QueuedDraftRecoversWhenQueueIdCheckpointWasLost()
    {
        var f = new Fixture("Draft a plan", Route("Deliverable", "publication-plan", "Preparing"), "Draft: review the plan.");
        Assert.True((await f.Converse()).Succeeded);
        var key = $"youtube.turn:{f.Input.MessageId:N}";
        var saved = f.States[key];
        var payload = saved.Payload.Deserialize<TurnState>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        f.States[key] = saved with { Payload = JsonSerializer.SerializeToElement(payload with { TodoId = null, Phase = "Routed", Response = null },
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var todo = Assert.Single(f.Todos);
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo, f.Context(), default);
        Assert.Equal(todo.Id, f.States[key].Payload.GetProperty("todoId").GetGuid());
        Assert.Equal("Delivered", f.States[key].Payload.GetProperty("phase").GetString());
        Assert.Single(f.Sent);
    }

    [Fact]
    public async Task ForgedPersonalWorkCannotConsumeSavedDirection()
    {
        var f = new Fixture("Draft a plan", Route("Deliverable", "publication-plan", "Preparing"));
        Assert.True((await f.Converse()).Succeeded);
        var todo = Assert.Single(f.Todos);
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo with { CreatedByOrganizationUserId = Guid.NewGuid() }, f.Context(), default);
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo with { SourceMessageId = Guid.NewGuid() }, f.Context(), default);
        Assert.Equal(1, f.Model.Calls);
        Assert.Empty(f.Sent);
    }

    [Fact]
    public async Task ReplyReadIsBoundedAndDoesNotCreatePublicWork()
    {
        var route = JsonSerializer.Serialize(new Intake("Read", "replies", "Checking", ResourceId: "parent-a"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var f = new Fixture("Show replies to parent-a", route, "This page contains one reply; more replies are available.");
        f.Runtime.RegisterCapability<ListCommentRepliesRequest, YouTubePage<YouTubeComment>>(YouTubeCapabilities.ListCommentReplies, (request, _) =>
        {
            Assert.Equal("parent-a", request.ParentId);
            return Task.FromResult(new YouTubePage<YouTubeComment>([new("parent-a.reply", new(TextDisplay: "Hello", ParentId: "parent-a"))], "next"));
        });
        Assert.True((await f.Converse()).Succeeded);
        Assert.Empty(f.Todos);
        Assert.Equal("next", f.States.Values.Single().Payload.GetProperty("evidence").GetProperty("nextPageToken").GetString());
    }

    [Fact]
    public async Task InformationDoesNotCreateWorkAndReplayDoesNotReasonAgain()
    {
        var f = new Fixture("What can you help with?", Route("Information", "none", "I can help read your channel information and prepare drafts."));
        Assert.True((await f.Converse()).Succeeded);
        Assert.True((await f.Converse()).Succeeded);
        Assert.Equal(1, f.Model.Calls);
        Assert.Empty(f.Todos);
        Assert.Equal(0, f.ProviderReads);
    }

    [Fact]
    public async Task ManagerPreferenceIsStateOnly()
    {
        var f = new Fixture("Use a friendly brand voice", Route("Preference", "none", "Noted", preference: "brandVoice", value: "friendly"));
        Assert.True((await f.Converse()).Succeeded);
        Assert.Equal("friendly", f.States["youtube.preferences"].Payload.GetProperty("values").GetProperty("brandVoice").GetString());
        Assert.Empty(f.Todos);
        Assert.Equal(0, f.ProviderReads);
    }

    [Fact]
    public async Task NonManagerCannotChangeGlobalPreferences()
    {
        var f = new Fixture("Use a friendly voice", Route("Preference", "none", "Noted", preference: "brandVoice", value: "friendly"));
        f.Manager = Guid.NewGuid();
        Assert.True((await f.Converse()).Succeeded);
        Assert.False(f.States.ContainsKey("youtube.preferences"));
        Assert.Empty(f.Todos);
    }

    [Fact]
    public async Task ModelApprovalAndMutationRequestsCannotExecute()
    {
        foreach (var kind in new[] { "Approval", "Unavailable" })
        {
            var f = new Fixture("Approve and publish now", Route(kind, "none", "Yes"));
            var result = await f.Converse();
            Assert.True(result.Succeeded);
            Assert.Empty(f.Todos);
            Assert.Equal(0, f.ProviderReads);
            Assert.DoesNotContain("Yes", result.Value!.Value.GetProperty("response").GetString());
        }
    }

    [Fact]
    public async Task ChannelReadUsesBrokerAndPersistsEvidenceBeforeAnswer()
    {
        var f = new Fixture("How is my channel doing?", Route("Read", "channel", "Checking"), "The connected channel has no items in this response.");
        Assert.True((await f.Converse()).Succeeded);
        Assert.Equal(1, f.ProviderReads);
        Assert.Equal(2, f.Model.Calls);
        Assert.Equal(JsonValueKind.Object, f.States.Values.Single().Payload.GetProperty("evidence").ValueKind);
        Assert.Empty(f.Todos);
        Assert.Contains("untrusted", f.Model.Instructions, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Model.Tools);
    }

    [Fact]
    public async Task DeliverableSurvivesRestartAndDeliveryFailureWithoutRegeneration()
    {
        var f = new Fixture("Draft a publication plan", Route("Deliverable", "publication-plan", "Preparing"), "Draft publication plan: review metadata and privacy before approval.");
        Assert.True((await f.Converse()).Succeeded);
        Assert.True((await f.Converse()).Succeeded);
        var todo = Assert.Single(f.Todos);
        f.FailDelivery = true;
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo, f.Context(), default);
        Assert.Equal("Generated", f.States.Values.Single().Payload.GetProperty("phase").GetString());
        Assert.Equal(2, f.Model.Calls);
        f.FailDelivery = false;
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo, f.Context(), default);
        await new YouTubeManagerAgent(f.Model).HandlePersonalTodoAsync(todo, f.Context(), default);
        Assert.Equal(2, f.Model.Calls);
        Assert.Single(f.Sent);
        Assert.Equal("Delivered", f.States.Values.Single().Payload.GetProperty("phase").GetString());
    }

    [Fact]
    public async Task MissingSetupAuthorityAllowsOnlyGuidanceAndNativeAction()
    {
        var f = new Fixture("How do I connect?", "Use Connect YouTube to sign in and confirm your channel.", setupOnly: true);
        var result = await f.Converse();
        Assert.True(result.Succeeded);
        Assert.Equal(1, f.SetupActions);
        Assert.Empty(f.States);
        Assert.Empty(f.Todos);
        Assert.Equal(0, f.ProviderReads);
    }

    [Fact]
    public async Task SpoofedMessageOrChangedReplayFailsWithoutWork()
    {
        var f = new Fixture("Hello", Route("Information", "none", "Hello"));
        var result = await f.Converse(f.Input with { Prompt = "Publish a video" });
        Assert.False(result.Succeeded);
        Assert.Equal(0, f.Model.Calls);
        Assert.Empty(f.States);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"kind\":\"Read\",\"intent\":\"delete\",\"reply\":\"done\"}")]
    [InlineData("{\"kind\":\"Information\",\"kind\":\"Approval\",\"intent\":\"none\",\"reply\":\"done\"}")]
    [InlineData("{\"kind\":\"Preference\",\"intent\":\"none\",\"reply\":\"done\",\"preference\":\"approvalMode\",\"value\":\"Fully Autonomous\"}")]
    [InlineData("{\"kind\":\"Deliverable\",\"intent\":\"analytics\",\"reply\":\"done\"}")]
    public async Task InvalidModelOutputNeverCreatesWork(string modelOutput)
    {
        var f = new Fixture("Help me", modelOutput);
        Assert.False((await f.Converse()).Succeeded);
        Assert.Empty(f.Todos);
        Assert.Equal(0, f.ProviderReads);
    }

    [Fact]
    public async Task CancellationAndUnknownCapabilitiesFailSafely()
    {
        var runtime = new AgentTestRuntime();
        var agent = new YouTubeManagerAgent();
        Assert.False((await runtime.ExecuteCapabilityAsync(agent, "unknown", new { })).Succeeded);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.ExecuteCapabilityAsync(agent,
            YouTubeManagerProfile.Assistant, new { }, cancellation.Token));
    }

    private static string Route(string kind, string intent, string reply, string? preference = null, string? value = null) =>
        JsonSerializer.Serialize(new Intake(kind, intent, reply, Preference: preference, Value: value), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class Fixture
    {
        public AgentTestRuntime Runtime { get; } = new();
        public FakeModel Model { get; }
        public Guid Sender { get; } = Guid.NewGuid();
        public Guid Manager { get; set; }
        public Guid Employee { get; } = Guid.NewGuid();
        public AssistantRequest Input { get; }
        public Dictionary<string, AgentOperatingStateResponse> States { get; } = [];
        public List<PersonalTodoItem> Todos { get; } = [];
        public List<CommunicationMessage> Sent { get; } = [];
        public List<Guid> Requeued { get; } = [];
        public int ProviderReads { get; private set; }
        public int SetupActions { get; private set; }
        public bool FailDelivery { get; set; }
        public bool FailNextActionCheckpoint { get; set; }
        public Func<AgentOperatingStateWriteRequest, bool>? FailStateWrite { get; set; }
        private readonly Dictionary<string, CommunicationMessage> sentKeys = [];
        private readonly Dictionary<string, PersonalTodoItem> todoKeys = [];
        public Fixture(string prompt, string output, string? secondOutput = null, bool setupOnly = false)
        {
            Manager = Sender;
            Input = new(Guid.NewGuid(), Guid.NewGuid().ToString("D"), prompt, MessageId: Guid.NewGuid(), ChatTurnId: Guid.NewGuid());
            Model = new(output, secondOutput);
            Runtime.RegisterCapability<JsonElement, CommunicationMessages>(CommunicationCapabilities.ChatRead, (_, _) => Task.FromResult(
                new CommunicationMessages([new(Input.MessageId, 1, Guid.Parse(Input.ConversationId), Sender, "Owner", "Human", prompt, DateTimeOffset.UtcNow)])));
            if (!setupOnly)
            {
                Runtime.RegisterCapability<AgentOperatingStateReadRequest, AgentOperatingStateReadResponse>(PlatformCapabilities.AgentOperatingStateRead,
                    (r, _) => Task.FromResult(new AgentOperatingStateReadResponse(States.GetValueOrDefault(r.StateKey))));
                Runtime.RegisterCapability<AgentOperatingStateWriteRequest, AgentOperatingStateResponse>(PlatformCapabilities.AgentOperatingStateWrite, (r, _) =>
                {
                    Assert.True(r.Payload.GetRawText().Length <= 65536, "State payload exceeds the host bound.");
                    if (FailStateWrite?.Invoke(r) == true)
                        throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateWrite, PlatformCapabilityErrorCode.Unavailable, "Simulated state failure");
                    if (FailNextActionCheckpoint && r.Payload.TryGetProperty("replyWork", out var reply) && reply.ValueKind == JsonValueKind.Object &&
                        reply.TryGetProperty("actionId", out var action) && action.ValueKind == JsonValueKind.String)
                    {
                        FailNextActionCheckpoint = false;
                        throw new PlatformCapabilityException(PlatformCapabilities.AgentOperatingStateWrite, PlatformCapabilityErrorCode.Unavailable, "Simulated checkpoint failure");
                    }
                    var old = States.GetValueOrDefault(r.StateKey);
                    Assert.Equal(old?.Revision, r.ExpectedRevision);
                    var state = new AgentOperatingStateResponse(old?.Id ?? Guid.NewGuid(), r.StateKey, r.SchemaId, r.SchemaVersion,
                        r.Status, r.SourceRevisions, r.ConditionCodes, r.DecisionFingerprint, r.OpenCommitmentCorrelations,
                        r.AttentionReviewId, r.Payload.Clone(), (old?.Revision ?? 0) + 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
                    States[r.StateKey] = state; return Task.FromResult(state);
                });
            }
            Runtime.RegisterCapability<AddPersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Add, (r, _) =>
            {
                if (todoKeys.TryGetValue(r.IdempotencyKey, out var existing)) return Task.FromResult(existing);
                var item = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), Employee, Employee, "YouTube Manager", r.Title,
                    r.Description ?? "", "Ready", r.Priority, 0, 1, r.DueDate, r.SourceConversationId, r.SourceMessageId,
                    [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) { CorrelationId = r.CorrelationId };
                Todos.Add(item); todoKeys[r.IdempotencyKey] = item; return Task.FromResult(item);
            });
            Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, _) =>
            { ProviderReads++; return Task.FromResult(new YouTubePage<Channel>([])); });
            Runtime.RegisterCapability<JsonElement, PersonalTodoDirectory>(PersonalTodoCapabilities.Read, (_, _) => Task.FromResult(
                new PersonalTodoDirectory([new(Guid.NewGuid(), Employee, "YouTube Manager", Manager, "Manager", 1, Todos.ToArray())], Employee)));
            Runtime.RegisterCapability<RequeuePersonalTodoItemRequest, PersonalTodoItem>(PersonalTodoCapabilities.Requeue, (r, _) =>
            {
                var index = Todos.FindIndex(x => x.Id == r.ItemId); Assert.True(index >= 0);
                Assert.Equal(Todos[index].Revision, r.ExpectedRevision); Requeued.Add(r.ItemId);
                Todos[index] = Todos[index] with { Status = "Ready", Revision = Todos[index].Revision + 1 };
                return Task.FromResult(Todos[index]);
            });
            Runtime.RegisterCapability<JsonElement, CommunicationMessage>(CommunicationCapabilities.MessageSend, (r, _) =>
            {
                Assert.InRange(r.GetProperty("content").GetString()!.Length, 1, 32768);
                if (FailDelivery) throw new PlatformCapabilityException(CommunicationCapabilities.MessageSend, PlatformCapabilityErrorCode.Denied, "Simulated denied delivery");
                var key = r.GetProperty("idempotencyKey").GetString()!;
                if (sentKeys.TryGetValue(key, out var prior)) return Task.FromResult(prior);
                var message = new CommunicationMessage(Guid.NewGuid(), 2, r.GetProperty("chatId").GetGuid(), Employee,
                    "YouTube Manager", "Agent", r.GetProperty("content").GetString()!, DateTimeOffset.UtcNow);
                Sent.Add(message); sentKeys[key] = message; return Task.FromResult(message);
            });
            Runtime.RegisterCapability<SuggestUserActionRequest, SuggestedUserActionResponse>(PlatformCapabilities.UserActionSuggest, (r, _) =>
            {
                SetupActions++; Assert.Equal(UserActionWorkflows.PluginSetupOpen, r.WorkflowType);
                Assert.Equal("{}", r.Parameters.GetRawText());
                return Task.FromResult(new SuggestedUserActionResponse(Guid.NewGuid(), r.WorkflowType, r.Label, r.Description, "/setup", "Available", DateTimeOffset.UtcNow));
            });
        }
        public AgentRuntimeContext Context() => Runtime.CreateContext(identity: new(Employee.ToString("D"), "YouTube Manager",
            null, null, null, [], "IndividualContributor", Manager.ToString("D"), "Manager"));
        public PersonalTodoItem SeedPausedDraft()
        {
            var sourceId = Guid.NewGuid(); var key = $"youtube.turn:{sourceId:N}";
            var item = new PersonalTodoItem(Guid.NewGuid(), Guid.NewGuid(), Employee, Employee, "YouTube Manager", "Saved draft", "Draft only",
                "Running", "Normal", 0, 1, null, Guid.Parse(Input.ConversationId), sourceId, [], null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                { CorrelationId = key, Wait = new(DateTimeOffset.UtcNow.AddMinutes(15), "Paused") };
            Todos.Add(item);
            var state = new TurnState("saved", Input with { MessageId = sourceId, Prompt = "Prepare a publication plan" }, "Generated",
                new("Deliverable", "publication-plan", "Preparing"), Response: "Previously generated draft", TodoId: item.Id, Paused: true);
            States[key] = new(Guid.NewGuid(), key, "youtube.manager.state.v1", 1, "Generated", new Dictionary<string, string>(), [], "saved", [], Guid.Empty,
                JsonSerializer.SerializeToElement(state, new JsonSerializerOptions(JsonSerializerDefaults.Web)), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            return item;
        }
        public Task<AgentWorkResult> Converse(AssistantRequest? input = null) => new YouTubeManagerAgent(Model).ExecuteCapabilityAsync(
            new(Guid.NewGuid(), YouTubeManagerProfile.Assistant, JsonSerializer.SerializeToElement(input ?? Input,
                new JsonSerializerOptions(JsonSerializerDefaults.Web))), Context(), default);
    }

    private sealed class FakeModel(string first, string? second) : IAgentLlmClientFactory, IChatClient
    {
        private readonly Queue<string> outputs = new(second is null ? [first] : [first, second]);
        public void Enqueue(string output) => outputs.Enqueue(output);
        public int Calls { get; private set; }
        public string Instructions { get; private set; } = "";
        public string LastData { get; private set; } = "";
        public IReadOnlyList<AITool> Tools { get; private set; } = [];
        public Task<IChatClient> CreateChatClientAsync(AgentLlmSelection selection, CancellationToken cancellationToken = default) => Task.FromResult<IChatClient>(this);
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++;
            Assert.Equal(3000, options!.MaxOutputTokens); Assert.Equal(0.2f, options.Temperature);
            Instructions = messages.First().Text; LastData = messages.Last().Text; Tools = options.Tools?.ToArray() ?? [];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, outputs.Dequeue())));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { var response = await GetResponseAsync(messages, options, cancellationToken); yield return new(ChatRole.Assistant, response.Text); }
        public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;
        public void Dispose() { }
    }
}
