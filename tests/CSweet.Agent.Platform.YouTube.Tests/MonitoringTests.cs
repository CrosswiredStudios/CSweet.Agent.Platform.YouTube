using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Fact]
    public async Task OnboardingVerifiesChannelCreatesBothDutiesAndAcknowledgesTheStableEventAfterPersistence()
    {
        var m = new MonitoringFixture(); await m.Start(); await m.Start();
        Assert.Equal(2, m.F.Todos.Count); Assert.Single(m.F.Sent); Assert.Single(m.Acknowledged);
        Assert.Equal(m.Event.EventId, m.Acknowledged.Single());
        Assert.NotEqual(m.Event.WorkId, m.Acknowledged.Single());
        Assert.Equal(m.F.Todos[0].Id, m.State.TodoId);
        Assert.Equal(m.F.Sent[0].Id, m.State.IntroductionId); Assert.Equal(0, m.F.Model.Calls);
    }

    [Theory]
    [InlineData("introductionId")]
    [InlineData("todoId")]
    public async Task LostOnboardingCheckpointsReuseIntroductionAndTaskBeforeAcknowledging(string field)
    {
        var m = new MonitoringFixture(); var failed = false;
        m.F.FailStateWrite = r => !failed && r.StateKey == "youtube.monitor" &&
            r.Payload.GetProperty(field).ValueKind == JsonValueKind.String && (failed = true);
        await Assert.ThrowsAsync<PlatformCapabilityException>(() => m.Start()); Assert.Empty(m.Acknowledged);
        m.F.FailStateWrite = null; await m.Start();
        Assert.Equal(2, m.F.Todos.Count); Assert.Single(m.F.Sent); Assert.Single(m.Acknowledged);
    }

    [Fact]
    public async Task ForeignLifecycleAndUnavailableChannelCannotActivateMonitoring()
    {
        var m = new MonitoringFixture();
        var data = m.Event.Data.Deserialize<AgentOnboardedEvent>(JsonOptions)!;
        await m.Agent().HandleEventAsync(m.Event with { Data = JsonSerializer.SerializeToElement(data with { OrganizationId = Guid.NewGuid() }, JsonOptions) }, m.Context(), default);
        await m.Agent().HandleEventAsync(m.Event with { Data = JsonSerializer.SerializeToElement(data with { AgentOrganizationUserId = Guid.NewGuid() }, JsonOptions) }, m.Context(), default);
        Assert.Equal(0, m.ChannelReads); Assert.Empty(m.F.States);
        m.Failure = PlatformCapabilityErrorCode.Denied;
        await Assert.ThrowsAsync<PlatformCapabilityException>(() => m.Start());
        Assert.Empty(m.F.Todos); Assert.Empty(m.F.Sent); Assert.Empty(m.Acknowledged);
    }

    [Fact]
    public async Task MonitoringWaitsFifteenMinutesAndSuppressesUnchangedDailyNotifications()
    {
        var m = new MonitoringFixture(); await m.Start(); await m.Process();
        Assert.True(m.State.Initialized); Assert.Equal(1, m.State.Cycle);
        Assert.Equal(m.Clock.GetUtcNow().AddMinutes(15), m.State.NextRunAt);
        var reads = m.ChannelReads; await m.Process(); Assert.Equal(reads, m.ChannelReads);
        m.Clock.Advance(TimeSpan.FromMinutes(15)); await m.Process();
        Assert.Equal(2, m.State.Cycle); Assert.Equal(0, m.State.PendingNew); Assert.Equal(0, m.State.PendingChanged);
        m.Clock.Advance(TimeSpan.FromDays(1)); await m.Process();
        Assert.Equal(2, m.F.Sent.Count); Assert.Equal(0, m.F.Model.Calls); Assert.Equal(2, m.F.Todos.Count);
    }

    [Fact]
    public async Task DailyDigestCountsNewAndEditedContentButNotLikesAndSurvivesDeliveryCheckpointLoss()
    {
        var m = new MonitoringFixture(); await m.Start(); await m.Process();
        m.Threads = [Thread("one", 0, "Updated text"), Thread("two", 0)];
        m.Clock.Advance(TimeSpan.FromMinutes(15)); await m.Process();
        Assert.Equal(1, m.State.PendingNew); Assert.Equal(1, m.State.PendingChanged); Assert.Equal(2, m.F.Sent.Count);
        var first = m.Threads[0];
        m.Threads = [first with { Snippet = first.Snippet! with { TopLevelComment = first.Snippet!.TopLevelComment! with
            { Snippet = first.Snippet.TopLevelComment!.Snippet! with { LikeCount = 77 } } } }, m.Threads[1]];
        m.Clock.Advance(TimeSpan.FromDays(1)); var failed = false;
        m.F.FailStateWrite = r => !failed && r.StateKey == "youtube.monitor" && r.Payload.GetProperty("cycle").GetInt32() == 3 && (failed = true);
        await m.Process(); Assert.True(failed); Assert.Equal(3, m.F.Sent.Count);
        m.F.FailStateWrite = null; await m.Process(); await m.Process();
        Assert.Equal(3, m.F.Sent.Count); Assert.Equal(3, m.State.Cycle);
        Assert.Contains("1 newly observed", m.F.Sent[^1].Content); Assert.Contains("1 observed content", m.F.Sent[^1].Content);
        Assert.Equal(0, m.State.PendingNew); Assert.Equal(0, m.State.PendingChanged);
    }

    [Fact]
    public async Task PauseStopsProviderWorkAndCadencePreferenceControlsTheNextCycle()
    {
        var m = new MonitoringFixture(); await m.Start();
        m.Preferences(new() { ["paused"] = "true" }); var reads = m.ChannelReads;
        await m.Process(); Assert.Equal(reads, m.ChannelReads); Assert.Equal(0, m.State.Cycle);
        m.Preferences(new() { ["paused"] = "false", ["commentCadenceMinutes"] = "30" });
        await m.Process(); Assert.Equal(m.Clock.GetUtcNow().AddMinutes(30), m.State.NextRunAt);
        await m.Agent().HandlePersonalTodoAsync(m.F.Todos[0] with { SourceMessageId = Guid.NewGuid() }, m.Context(), default);
        Assert.Equal(1, m.State.Cycle);
    }

    [Fact]
    public async Task LateCompletionRecoverySchedulesAnImmediateFutureCheckWithoutReplayingItsScan()
    {
        var m = new MonitoringFixture(); await m.Start(); m.F.FailDelivery = true;
        await m.Process(); Assert.Equal(1, m.ThreadReads); Assert.Equal(0, m.State.Cycle);
        m.Clock.Advance(TimeSpan.FromDays(2)); m.F.FailDelivery = false;
        var recovered = await m.Process();
        Assert.Equal(1, m.ThreadReads); Assert.Equal(1, m.State.Cycle);
        Assert.True(m.State.NextRunAt < m.Clock.GetUtcNow());
        Assert.Equal(PersonalTodoResult.WaitingUntil(m.Clock.GetUtcNow().AddSeconds(5),
            "YouTube monitoring is saved and scheduled for its next check."), recovered);
        Assert.Equal(2, m.F.Sent.Count);
    }

    [Fact]
    public async Task DigestUsesTheConfiguredLocalDayRatherThanUtcMidnight()
    {
        var m = new MonitoringFixture(); await m.Start(); m.Preferences(new() { ["timezone"] = "America/Los_Angeles" });
        await m.Process(); m.Threads = [Thread("one", 0), Thread("two", 0)];
        m.Clock.Advance(TimeSpan.FromHours(12)); await m.Process();
        Assert.Equal(1, m.State.PendingNew); Assert.Equal(2, m.F.Sent.Count);
        m.Clock.Advance(TimeSpan.FromHours(8)); await m.Process();
        Assert.Equal(0, m.State.PendingNew); Assert.Equal(3, m.F.Sent.Count);
        Assert.Contains("1 newly observed", m.F.Sent[^1].Content);
    }

    [Fact]
    public async Task MultiPageMonitoringContinuesTheSameCycleAndDefersBetweenPages()
    {
        var m = new MonitoringFixture(); await m.Start(); var tokens = new List<string?>();
        m.F.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (r, _) =>
        {
            tokens.Add(r.PageToken);
            return Task.FromResult(r.PageToken is null ? new YouTubePage<CommentThread>([Thread("one", 0)], "next") : new YouTubePage<CommentThread>([]));
        });
        await m.Process(); Assert.Equal(0, m.State.Cycle); Assert.Single(m.F.Sent);
        await m.Process(); Assert.Single(tokens);
        m.Clock.Advance(TimeSpan.FromMinutes(1)); await m.Process();
        Assert.Equal(new string?[] { null, "next" }, tokens); Assert.Equal(1, m.State.Cycle); Assert.Equal(2, m.F.Sent.Count);
    }

    [Fact]
    public async Task ARequestedDraftScanCannotConsumeTheMonitorsNewCommentNotification()
    {
        var m = new MonitoringFixture(); await m.Start(); await m.Process();
        m.Threads = [Thread("one", 0), Thread("two", 0)];
        var request = new AgentCapabilityRequest(Guid.NewGuid(), YouTubeManagerProfile.Assistant,
            JsonSerializer.SerializeToElement(m.F.Input, JsonOptions));
        Assert.True((await m.Agent().ExecuteCapabilityAsync(request, m.Context(), default)).Succeeded);
        var requested = m.F.Todos.Single(x => x.CorrelationId?.StartsWith("youtube.turn:", StringComparison.Ordinal) == true);
        await m.Agent().HandlePersonalTodoAsync(requested, m.Context(), default);
        Assert.Equal("Delivered", m.F.States[requested.CorrelationId!].Payload.GetProperty("phase").GetString());
        m.Clock.Advance(TimeSpan.FromDays(1)); await m.Process();
        Assert.Contains("1 newly observed", m.F.Sent[^1].Content);
    }

    [Fact]
    public async Task LostAccessNotifiesOnceOffersReconnectAndResumesWithoutRestoringPublicAuthority()
    {
        var m = new MonitoringFixture(); await m.Start(); await m.Process();
        m.Failure = PlatformCapabilityErrorCode.Denied; m.Clock.Advance(TimeSpan.FromMinutes(15)); await m.Process();
        Assert.Equal("ConnectionRequired", m.State.Condition); Assert.Equal(1, m.F.SetupActions);
        var notices = m.F.Sent.Count;
        m.Clock.Advance(TimeSpan.FromHours(1)); await m.Process(); Assert.Equal(notices, m.F.Sent.Count);
        m.Failure = null; m.Clock.Advance(TimeSpan.FromHours(1)); await m.Process();
        Assert.Null(m.State.Condition); Assert.Contains("resumed for the same", m.F.Sent[^1].Content);
        Assert.Equal(2, m.F.Todos.Count); Assert.Equal(0, m.F.Model.Calls);
    }

    [Fact]
    public async Task QuotaBackoffAndChannelSwitchDoNotResetOrRebindTheSavedDuty()
    {
        var m = new MonitoringFixture(); await m.Start();
        m.Failure = PlatformCapabilityErrorCode.BudgetExceeded; await m.Process();
        Assert.Equal(m.Clock.GetUtcNow().AddHours(1), m.State.NextRunAt); Assert.Equal(0, m.State.Cycle);
        Assert.Single(m.F.Sent); m.Failure = null; m.Channel = "different";
        m.Clock.Advance(TimeSpan.FromHours(1)); await m.Process();
        Assert.Equal("channel", m.State.ChannelId); Assert.Equal("ConnectionRequired", m.State.Condition);
        Assert.Equal(0, m.ThreadReads); Assert.DoesNotContain(m.F.States.Keys, x => x.StartsWith("youtube.engagement-inbox:"));
    }

    private sealed class MonitoringFixture
    {
        public Fixture F { get; } = EngagementFixture();
        public Guid Organization { get; } = Guid.NewGuid();
        public TestMonitoringClock Clock { get; } = new();
        public AgentEventEnvelope Event { get; }
        public HashSet<Guid> Acknowledged { get; } = [];
        public IReadOnlyList<CommentThread> Threads { get; set; } = [Thread("one", 0)];
        public PlatformCapabilityErrorCode? Failure { get; set; }
        public string Channel { get; set; } = "channel";
        public int ChannelReads { get; private set; }
        public int ThreadReads { get; private set; }
        public YouTubeMonitoringState State => F.States["youtube.monitor"].Payload.Deserialize<YouTubeMonitoringState>(JsonOptions)!;
        public MonitoringFixture()
        {
            Event = new(Guid.NewGuid(), Guid.NewGuid(), AgentLifecycleEvents.Onboarded,
                JsonSerializer.SerializeToElement(new AgentOnboardedEvent(Organization, F.Employee, F.Sender,
                    Guid.Parse(F.Input.ConversationId), Clock.GetUtcNow()), JsonOptions), Clock.GetUtcNow());
            F.Runtime.RegisterCapability<ReadChannelRequest, YouTubePage<Channel>>(YouTubeCapabilities.ReadChannel, (_, _) =>
            {
                ChannelReads++;
                if (Failure is { } failure) throw new PlatformCapabilityException(YouTubeCapabilities.ReadChannel, failure, "Sensitive provider diagnostic");
                return Task.FromResult(new YouTubePage<Channel>([new(Channel, new("Company"))]));
            });
            F.Runtime.RegisterCapability<ListPageRequest, YouTubePage<CommentThread>>(YouTubeCapabilities.ListCommentThreads, (_, _) =>
            { ThreadReads++; return Task.FromResult(new YouTubePage<CommentThread>(Threads)); });
            F.Runtime.RegisterCapability<CompleteAgentOnboardingRequest, CompleteAgentOnboardingResponse>(AgentLifecycleCapabilities.CompleteOnboarding, (r, _) =>
            {
                Assert.Equal(Event.EventId, r.EventId); Assert.NotNull(State.IntroductionId);
                Assert.Equal(F.Todos.Single(x => x.CorrelationId == "youtube.monitor").Id, State.TodoId);
                Assert.Equal(F.Todos.Single(x => x.CorrelationId == "youtube.reporting").Id,
                    F.States["youtube.reporting"].Payload.Deserialize<YouTubeReportingState>(JsonOptions)!.TodoId);
                Acknowledged.Add(r.EventId);
                return Task.FromResult(new CompleteAgentOnboardingResponse(true, Clock.GetUtcNow()));
            });
        }
        public YouTubeManagerAgent Agent() => new(F.Model, Clock);
        public AgentRuntimeContext Context() => F.Runtime.CreateContext(Organization.ToString("D"), identity: F.Context().Identity);
        public Task Start() => Agent().HandleEventAsync(Event, Context(), default);
        public Task<PersonalTodoResult> Process() => Agent().HandlePersonalTodoAsync(F.Todos.Single(x => x.CorrelationId == "youtube.monitor"), Context(), default);
        public void Preferences(Dictionary<string, string> values) => F.States["youtube.preferences"] = F.States["youtube.monitor"] with
        { StateKey = "youtube.preferences", Payload = JsonSerializer.SerializeToElement(new Preferences(values), JsonOptions) };
    }

    private sealed class TestMonitoringClock : TimeProvider
    {
        // SDK wait validation uses the real clock, so anchor the otherwise controlled test clock in its future.
        private DateTimeOffset now = new(DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(2).AddHours(12), TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
