using System.Globalization;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube;

public sealed record YouTubeMonitoringState(Guid OrganizationId, Guid EmployeeId, Guid ConversationId,
    Guid OnboardingEventId, string ChannelId, DateTimeOffset NextRunAt, Guid? IntroductionId = null,
    Guid? TodoId = null, int Cycle = 0, bool Initialized = false, string? LastDigestDate = null,
    long PendingNew = 0, long PendingChanged = 0, string? Condition = null);
public sealed record YouTubeMonitoringCompletion(YouTubeMonitoringState Next, string? Message);

public sealed partial class YouTubeManagerAgent
{
    private const string MonitoringKey = "youtube.monitor";
    private DateTimeOffset MonitoringNow => (clock ?? TimeProvider.System).GetUtcNow();

    private async Task StartMonitoring(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken ct)
    {
        var onboarded = DeserializePayload<AgentOnboardedEvent>(message.Data);
        if (onboarded is null || message.EventId == Guid.Empty || onboarded.ConversationId == Guid.Empty ||
            onboarded.OrganizationId.ToString("D") != context.BusinessId ||
            onboarded.AgentOrganizationUserId.ToString("D") != context.Identity?.EmployeeId) return;
        var channel = await new YouTubeClient(context.Platform).ReadChannelAsync(ct);
        if (channel.Items.Count != 1 || channel.NextPageToken is not null || string.IsNullOrWhiteSpace(channel.Items[0].Id))
            throw new InvalidOperationException("A confirmed channel is required before monitoring can start.");
        var state = await context.Platform.ReadOperatingStateAsync<YouTubeMonitoringState>(MonitoringKey, ct);
        state ??= await SaveMonitoring(new(onboarded.OrganizationId, onboarded.AgentOrganizationUserId,
            onboarded.ConversationId, message.EventId, channel.Items[0].Id, MonitoringNow), null, context, ct);
        if (state.Payload.OrganizationId != onboarded.OrganizationId || state.Payload.EmployeeId != onboarded.AgentOrganizationUserId ||
            state.Payload.ConversationId != onboarded.ConversationId || state.Payload.OnboardingEventId != message.EventId ||
            state.Payload.ChannelId != channel.Items[0].Id)
            throw new InvalidOperationException("The saved monitoring duty belongs to a different setup or channel.");
        if (state.Payload.IntroductionId is null)
        {
            var intro = await context.Platform.Communication.SendMessageAsync(onboarded.ConversationId,
                $"YouTube is connected to {PlainReviewText(channel.Items[0].Snippet.Title)}. I'll run the initial comment synchronization, " +
                "then check again every 15 minutes after a scan finishes and send a daily change digest here. " +
                "I'll also send a weekly report of official channel metrics and suggestions here. " +
                "You can pause this work or change its cadence. Public changes still require approval. " +
                "What are your channel goals and preferred brand voice?",
                $"youtube-monitor-introduction:{message.EventId:N}", ct);
            state = await SaveMonitoring(state.Payload with { IntroductionId = intro.Id }, state.Revision, context, ct);
        }
        if (state.Payload.TodoId is null)
        {
            var todo = await context.Platform.PersonalTodo.AddAsync(new("Monitor YouTube engagement",
                "Synchronize the bound channel and report meaningful changes using durable scheduled work. Never publish replies.",
                "Normal", null, $"youtube-monitor:{context.InstallationId}", SourceConversationId: onboarded.ConversationId,
                SourceMessageId: state.Payload.IntroductionId, CorrelationId: MonitoringKey), ct);
            state = await SaveMonitoring(state.Payload with { TodoId = todo.Id }, state.Revision, context, ct);
        }
        await StartReporting(state.Payload, context, ct);
        var completed = await context.Platform.Lifecycle.CompleteOnboardingAsync(message, ct);
        if (!completed.Completed) throw new InvalidOperationException("The platform did not acknowledge onboarding.");
    }

    private async Task<PersonalTodoResult> ProcessMonitoring(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (item.OwnerOrganizationUserId.ToString("D") != context.Identity?.EmployeeId ||
            item.CreatedByOrganizationUserId != item.OwnerOrganizationUserId)
            return PersonalTodoResult.Blocked("This monitoring duty was not created by this YouTube Manager.");
        var saved = await context.Platform.ReadOperatingStateAsync<YouTubeMonitoringState>(MonitoringKey, ct);
        if (saved is null || saved.Payload.OrganizationId.ToString("D") != context.BusinessId ||
            saved.Payload.EmployeeId != item.OwnerOrganizationUserId || saved.Payload.ConversationId != item.SourceConversationId ||
            saved.Payload.IntroductionId is null || saved.Payload.IntroductionId != item.SourceMessageId ||
            saved.Payload.TodoId is { } todoId && todoId != item.Id)
            return PersonalTodoResult.Blocked("The monitoring duty does not match its saved setup.");
        if (saved.Payload.TodoId is null)
            saved = await SaveMonitoring(saved.Payload with { TodoId = item.Id }, saved.Revision, context, ct);
        var now = MonitoringNow;
        var preferences = await context.Platform.ReadOperatingStateAsync<Preferences>("youtube.preferences", ct);
        if (preferences?.Payload.Values.GetValueOrDefault("paused") == "true")
            return WaitForMonitoring(now.AddMinutes(15), "YouTube monitoring is paused; keep the saved scan for resumption.");
        if (saved.Payload.NextRunAt > now)
            return WaitForMonitoring(saved.Payload.NextRunAt, "The next YouTube check is scheduled.");
        var cycleKey = $"youtube.monitor-scan:{saved.Payload.EmployeeId:N}:{saved.Payload.Cycle}";
        try
        {
            var completion = await context.Platform.ReadOperatingStateAsync<YouTubeMonitoringCompletion>($"{cycleKey}:completion", ct);
            if (completion is null)
            {
                var scan = await AdvanceEngagementScan(cycleKey, context, ct, saved.Payload.ChannelId, "youtube.monitor-inbox");
                now = MonitoringNow;
                if (!scan.Complete)
                {
                    saved = await SaveMonitoring(saved.Payload with { NextRunAt = now.AddMinutes(1) }, saved.Revision, context, ct);
                    return WaitForMonitoring(saved.Payload.NextRunAt, "The comment page is saved; continue the next page later.");
                }
                var cadence = int.TryParse(preferences?.Payload.Values.GetValueOrDefault("commentCadenceMinutes"), out var minutes)
                    && minutes is >= 15 and <= 1440 ? minutes : 15;
                var timezone = preferences?.Payload.Values.GetValueOrDefault("timezone") ?? "UTC";
                if (!TimeZoneInfo.TryFindSystemTimeZoneById(timezone, out var zone)) zone = TimeZoneInfo.Utc;
                var today = TimeZoneInfo.ConvertTime(now, zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var next = saved.Payload with { Cycle = saved.Payload.Cycle + 1, NextRunAt = now.AddMinutes(cadence), Condition = null,
                    PendingNew = saved.Payload.PendingNew + scan.NewComments, PendingChanged = saved.Payload.PendingChanged + scan.ChangedComments };
                string? notification = null;
                if (!saved.Payload.Initialized)
                {
                    notification = $"Initial comment synchronization is saved: {scan.CommentCount} returned comments and replies. " +
                        "I'll continue checking for changes. This covers published threads, not held or spam queues. Nothing was posted.";
                    next = next with { Initialized = true, LastDigestDate = today, PendingNew = 0, PendingChanged = 0 };
                }
                else if (string.CompareOrdinal(today, saved.Payload.LastDigestDate) > 0)
                {
                    if (next.PendingNew > 0 || next.PendingChanged > 0)
                        notification = $"YouTube engagement digest: {next.PendingNew} newly observed comments and replies, " +
                            $"and {next.PendingChanged} observed content or moderation changes since the last digest. " +
                            "These are saved inbox changes, not official analytics metrics. Ask me to review or draft replies; nothing was posted.";
                    next = next with { LastDigestDate = today, PendingNew = 0, PendingChanged = 0 };
                }
                if (saved.Payload.Condition == "ConnectionRequired")
                    notification = "Monitoring has resumed for the same YouTube channel. Public changes still require approval. " + notification;
                completion = await Write(context, $"{cycleKey}:completion", new YouTubeMonitoringCompletion(next, notification),
                    null, $"{cycleKey}:completion", "Synchronized", ct);
            }
            if (completion.Payload.Message is { } message)
                await context.Platform.Communication.SendMessageAsync(saved.Payload.ConversationId, message,
                    $"youtube-monitor-result:{saved.Payload.EmployeeId:N}:{saved.Payload.Cycle}", ct);
            saved = await SaveMonitoring(completion.Payload.Next, saved.Revision, context, ct);
            return WaitForMonitoring(saved.Payload.NextRunAt, "YouTube monitoring is saved and scheduled for its next check.");
        }
        catch (PlatformCapabilityException error) when (YouTubeCapabilities.All.Contains(error.Capability))
        {
            var condition = error.Code is PlatformCapabilityErrorCode.Denied or PlatformCapabilityErrorCode.NotFound ? "ConnectionRequired" : "ProviderDeferred";
            if (condition == "ConnectionRequired" && saved.Payload.Condition != condition)
            {
                var notice = await context.Platform.Communication.SendMessageAsync(saved.Payload.ConversationId,
                    "I can't continue YouTube monitoring with the current channel access. Please review the connection below. Saved work is retained and nothing was posted.",
                    $"youtube-monitor-access:{saved.Payload.EmployeeId:N}:{saved.Payload.Cycle}", ct);
                await context.Platform.SuggestUserActionAsync(new(notice.Id, null, UserActionWorkflows.PluginSetupOpen,
                    "Review YouTube connection", "Reconnect or confirm the connected channel securely.", SerializePayload(new { }),
                    $"youtube-monitor-reconnect:{saved.Payload.EmployeeId:N}:{saved.Payload.Cycle}"), ct);
            }
            saved = await SaveMonitoring(saved.Payload with { Condition = condition, NextRunAt = now.AddHours(1) }, saved.Revision, context, ct);
            return WaitForMonitoring(saved.Payload.NextRunAt, "YouTube monitoring is waiting for access or provider availability.");
        }
        catch (Exception error) when (error is InvalidOperationException or System.Text.Json.JsonException or PlatformCapabilityException)
        {
            return PersonalTodoResult.Blocked("The saved monitoring step could not be verified or persisted. Review and resume the existing duty; no external changes were made.");
        }
    }

    private static Task<AgentOperatingState<YouTubeMonitoringState>> SaveMonitoring(YouTubeMonitoringState state, long? revision,
        AgentRuntimeContext context, CancellationToken ct) => Write(context, MonitoringKey, state, revision,
        $"youtube-monitor-state:{Hash(System.Text.Json.JsonSerializer.Serialize(state))}", "Monitoring", ct);

    private PersonalTodoResult WaitForMonitoring(DateTimeOffset scheduled, string reason)
    {
        // A saved completion may be replayed after its next due time. Keep the original durable
        // schedule, but satisfy the SDK's future-only wait requirement with an immediate follow-up.
        var minimum = MonitoringNow;
        if (minimum < DateTimeOffset.UtcNow) minimum = DateTimeOffset.UtcNow;
        minimum = minimum.AddSeconds(5);
        return PersonalTodoResult.WaitingUntil(scheduled > minimum ? scheduled : minimum, reason);
    }
}
