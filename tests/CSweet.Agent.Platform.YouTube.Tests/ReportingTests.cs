using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube.Tests;

public sealed partial class ConversationTests
{
    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task OnboardingWaitsForTheReportingDutyCheckpointAndRecoversWithoutDuplicates(bool afterTodo)
    {
        var r = new ReportingFixture(); var failed = false;
        r.M.F.FailStateWrite = write => !failed && write.StateKey == "youtube.reporting" &&
            (!afterTodo || write.Payload.GetProperty("todoId").ValueKind == JsonValueKind.String) && (failed = true);
        await Assert.ThrowsAsync<PlatformCapabilityException>(() => r.Start()); Assert.Empty(r.M.Acknowledged);
        r.M.F.FailStateWrite = null; await r.Start(); await r.Start();
        Assert.Equal(2, r.M.F.Todos.Count); Assert.Single(r.M.F.Sent); Assert.Single(r.M.Acknowledged);
        Assert.Equal(r.Todo.Id, r.State.TodoId);
    }

    [Fact]
    public async Task WeeklyDutyWaitsThenSavesOfficialEvidenceNarrativeAndOneDelivery()
    {
        var r = new ReportingFixture(); await r.Start();
        await r.Process(); Assert.Equal(0, r.Reads); Assert.Equal(0, r.Model.Calls);
        r.M.Clock.Advance(TimeSpan.FromDays(7)); await r.Process(); await r.Process();
        Assert.Equal(1, r.State.Cycle); Assert.Equal(1, r.Reads); Assert.Equal(1, r.Model.Calls);
        Assert.Equal(2, r.M.F.Todos.Count); Assert.Equal(2, r.M.F.Sent.Count);
        Assert.Contains("Views: 1", r.M.F.Sent[^1].Content); Assert.Contains("Subscribers gained: 8", r.M.F.Sent[^1].Content);
        Assert.Contains("not proof of complete period coverage", r.M.F.Sent[^1].Content);
        Assert.Contains("No channel changes", r.M.F.Sent[^1].Content); Assert.Empty(r.Model.Tools);
        Assert.DoesNotContain("channel", r.Model.LastData); // Internal account identity is not needed for narrative reasoning.
        var cycle = r.Cycle(0); Assert.NotNull(cycle.Evidence); Assert.NotNull(cycle.Narrative); Assert.NotNull(cycle.MessageId);
        Assert.Equal(cycle.StartDate, r.Requests.Single().StartDate); Assert.Equal(cycle.EndDate, r.Requests.Single().EndDate);
        Assert.Equal(r.M.Clock.GetUtcNow().AddDays(7), r.State.NextRunAt);
    }

    [Theory]
    [InlineData("delivery")][InlineData("message-checkpoint")][InlineData("cursor-checkpoint")]
    public async Task RestartDoesNotRegenerateOrDuplicateASavedReport(string failure)
    {
        var r = new ReportingFixture(); await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7));
        var failed = false;
        r.M.F.FailDelivery = failure == "delivery";
        r.M.F.FailStateWrite = write => !failed && (failure == "message-checkpoint" && write.StateKey.StartsWith("youtube.report-cycle:") &&
            write.Payload.GetProperty("messageId").ValueKind == JsonValueKind.String || failure == "cursor-checkpoint" &&
            write.StateKey == "youtube.reporting" && write.Payload.GetProperty("cycle").GetInt32() == 1) && (failed = true);
        await r.Process(); Assert.Equal(1, r.Model.Calls); Assert.NotNull(r.Cycle(0).Narrative);
        r.M.F.FailDelivery = false; r.M.F.FailStateWrite = null; r.M.Clock.Advance(TimeSpan.FromHours(1));
        await r.Process(); await r.Process();
        Assert.Equal(1, r.State.Cycle); Assert.Equal(1, r.Reads); Assert.Equal(1, r.Model.Calls);
        Assert.Equal(2, r.M.F.Sent.Count); Assert.NotNull(r.Cycle(0).MessageId);
    }

    [Fact]
    public async Task PauseAndForgedWorkDoNotReadOrGenerateReports()
    {
        var r = new ReportingFixture(); await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7));
        r.M.Preferences(new() { ["paused"] = "true" }); await r.Process();
        Assert.Equal(0, r.Reads); Assert.Equal(0, r.Model.Calls);
        r.M.Preferences(new() { ["paused"] = "false" });
        await r.Process(r.Todo with { SourceMessageId = Guid.NewGuid() });
        await r.Process(r.Todo with { SourceConversationId = Guid.NewGuid() });
        await r.Process(r.Todo with { CreatedByOrganizationUserId = Guid.NewGuid() });
        Assert.Equal(0, r.Reads); Assert.Equal(0, r.Model.Calls);
        await r.Process(); Assert.Equal(1, r.State.Cycle);
    }

    [Fact]
    public async Task QuotaBackoffPreservesTheOriginallyScheduledDateRange()
    {
        var r = new ReportingFixture(); await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7));
        r.Failure = PlatformCapabilityErrorCode.BudgetExceeded; await r.Process(); var original = r.Cycle(0);
        Assert.Equal(0, r.Model.Calls); Assert.Single(r.M.F.Sent);
        await r.Process(); Assert.Equal(1, r.Reads);
        r.M.Clock.Advance(TimeSpan.FromDays(2)); r.Failure = null; await r.Process();
        Assert.All(r.Requests, request => { Assert.Equal(original.StartDate, request.StartDate); Assert.Equal(original.EndDate, request.EndDate); });
        Assert.Equal(1, r.State.Cycle); Assert.Equal(1, r.Model.Calls);
    }

    [Fact]
    public async Task ChangedChannelNotifiesOnceAndNeverReceivesThePreviousChannelsReport()
    {
        var r = new ReportingFixture(); await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7)); r.M.Channel = "different";
        await r.Process(); Assert.Equal(0, r.Reads); Assert.Equal("ConnectionRequired", r.State.Condition);
        r.M.Clock.Advance(TimeSpan.FromHours(1)); await r.Process(); Assert.Equal(2, r.M.F.Sent.Count); Assert.Equal(1, r.M.F.SetupActions);
        r.M.Channel = "channel"; r.M.Clock.Advance(TimeSpan.FromHours(1)); await r.Process();
        Assert.Equal(1, r.State.Cycle); Assert.Equal(1, r.Reads); Assert.Equal(3, r.M.F.Sent.Count);
    }

    [Fact]
    public async Task MissingReasoningConfigurationRetainsEvidenceAndExplainsRecoveryOnce()
    {
        var r = new ReportingFixture { Configured = false }; await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7));
        await r.Process(); Assert.Equal("ReasoningRequired", r.State.Condition); Assert.NotNull(r.Cycle(0).Evidence);
        r.M.Clock.Advance(TimeSpan.FromHours(1)); await r.Process(); Assert.Equal(2, r.M.F.Sent.Count);
        Assert.Equal(0, r.Model.Calls); Assert.Equal(1, r.Reads);
        r.Configured = true; r.M.Clock.Advance(TimeSpan.FromHours(1)); await r.Process();
        Assert.Equal(1, r.Model.Calls); Assert.Equal(1, r.State.Cycle); Assert.Equal(1, r.Reads);
    }

    [Fact]
    public async Task EmptyAnalyticsDoesNotInventZeroMetricsOrRequestInterpretation()
    {
        var r = new ReportingFixture(); r.Analytics = r.Analytics with { Rows = [] };
        await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7)); await r.Process();
        Assert.Equal(0, r.Model.Calls); Assert.Equal(1, r.State.Cycle);
        Assert.Contains("unavailable, not zero", r.M.F.Sent[^1].Content); Assert.DoesNotContain("Views: 0", r.M.F.Sent[^1].Content);
    }

    [Fact]
    public async Task MalformedAnalyticsNeverBecomesReportEvidenceOrAModelPrompt()
    {
        var r = new ReportingFixture(); r.Analytics = r.Analytics with { ColumnHeaders = [] };
        await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7)); await r.Process();
        Assert.Equal(0, r.State.Cycle); Assert.Null(r.Cycle(0).Evidence); Assert.Equal(0, r.Model.Calls); Assert.Single(r.M.F.Sent);
    }

    [Fact]
    public async Task MonthlyPreferenceUsesThePreviousPacificCalendarMonthAndDoesNotCreateAnotherDuty()
    {
        var r = new ReportingFixture(); await r.Start(); r.M.Preferences(new() { ["reportCadence"] = "monthly" });
        r.M.Clock.Advance(TimeSpan.FromDays(7)); await r.Process();
        var cycle = r.Cycle(0); Assert.Equal("monthly", cycle.Cadence); Assert.Equal(1, cycle.StartDate.Day);
        Assert.Equal(cycle.StartDate.AddMonths(1).AddDays(-1), cycle.EndDate);
        Assert.Equal(r.M.Clock.GetUtcNow().AddMonths(1), r.State.NextRunAt); Assert.Equal(2, r.M.F.Todos.Count);
    }

    [Theory]
    [InlineData("2026-03-09T06:30:00Z", "weekly", "2026-03-01", "2026-03-07")]
    [InlineData("2026-03-09T08:30:00Z", "weekly", "2026-03-02", "2026-03-08")]
    [InlineData("2026-03-01T07:30:00Z", "monthly", "2026-01-01", "2026-01-31")]
    [InlineData("2026-03-01T09:30:00Z", "monthly", "2026-02-01", "2026-02-28")]
    public void ReportPeriodsFollowPacificBoundariesNotMachineOrCompanyMidnight(string scheduled, string cadence, string start, string end)
    {
        var range = YouTubeManagerAgent.ReportingPeriod(DateTimeOffset.Parse(scheduled), cadence);
        Assert.Equal(DateOnly.Parse(start), range.StartDate); Assert.Equal(DateOnly.Parse(end), range.EndDate);
    }

    [Fact]
    public async Task CancellationDoesNotConsumeTheReportOrStartReasoning()
    {
        var r = new ReportingFixture(); await r.Start(); r.M.Clock.Advance(TimeSpan.FromDays(7));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => r.Process(ct: cancellation.Token));
        Assert.Equal(0, r.State.Cycle); Assert.Equal(0, r.Reads); Assert.Equal(0, r.Model.Calls);
    }

    private sealed class ReportingFixture
    {
        public MonitoringFixture M { get; } = new();
        public FakeModel Model { get; } = new("Consider reviewing audience retention before choosing the next video's structure.", null);
        public bool Configured { get; set; } = true;
        public int Reads { get; private set; }
        public PlatformCapabilityErrorCode? Failure { get; set; }
        public List<AnalyticsSummaryRequest> Requests { get; } = [];
        public OfficialAnalytics Analytics { get; set; } = new(new[] { "views", "estimatedMinutesWatched", "averageViewDuration", "averageViewPercentage", "likes", "comments", "shares", "subscribersGained", "subscribersLost" }
            .Select(name => new AnalyticsColumn(name, "METRIC", "FLOAT")).ToArray(), [Enumerable.Range(1, 9).Select(x => JsonSerializer.SerializeToElement(x)).ToArray()]);
        public YouTubeReportingState State => M.F.States["youtube.reporting"].Payload.Deserialize<YouTubeReportingState>(JsonOptions)!;
        public PersonalTodoItem Todo => M.F.Todos.Single(x => x.CorrelationId == "youtube.reporting");
        public YouTubeReportCycle Cycle(int cycle) => M.F.States[$"youtube.report-cycle:{M.F.Employee:N}:{cycle}"].Payload.Deserialize<YouTubeReportCycle>(JsonOptions)!;
        public ReportingFixture() => M.F.Runtime.RegisterCapability<AnalyticsSummaryRequest, OfficialAnalytics>(YouTubeCapabilities.AnalyticsSummary, (request, ct) =>
        {
            ct.ThrowIfCancellationRequested(); Reads++; Requests.Add(request);
            if (Failure is { } failure) throw new PlatformCapabilityException(YouTubeCapabilities.AnalyticsSummary, failure, "Private provider diagnostic");
            return Task.FromResult(Analytics);
        });
        public Task Start() => M.Start();
        public async Task<PersonalTodoResult> Process(PersonalTodoItem? todo = null, CancellationToken ct = default)
        {
            var agent = new YouTubeManagerAgent(Model, M.Clock);
            if (Configured)
            {
                var update = await agent.ExecuteCapabilityAsync(new(Guid.NewGuid(), AgentConfigurationCapabilities.Update,
                    JsonSerializer.SerializeToElement(new UpdateAgentConfigurationRequest(new Dictionary<string, JsonElement>
                    { ["llmProviderId"] = JsonSerializer.SerializeToElement(M.F.Input.ProviderProfileId.ToString("D")), ["llmModel"] = JsonSerializer.SerializeToElement("approved-model") }), JsonOptions)), M.Context(), default);
                Assert.True(update.Succeeded);
            }
            return await agent.HandlePersonalTodoAsync(todo ?? Todo, M.Context(), ct);
        }
    }
}
