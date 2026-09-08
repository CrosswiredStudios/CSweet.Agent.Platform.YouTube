using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube;

public sealed record EngagementParent(string Id, string? VideoId);
public sealed record EngagementScan(string ChannelId, DateTimeOffset StartedAt, string? ThreadToken = null,
    IReadOnlyList<EngagementParent>? Parents = null, int ParentIndex = 0, string? ReplyToken = null,
    int Page = 0, long CommentCount = 0, IReadOnlyList<string>? SampleKeys = null, bool Complete = false,
    long NewComments = 0, long ChangedComments = 0);
public sealed record EngagementPageReceipt(string CursorHash, EngagementScan Next);
public sealed record EngagementPageComment(string Id, string VersionKey, bool TopLevel);
public sealed record EngagementPendingPage(string CursorHash, EngagementScan Next, IReadOnlyList<EngagementPageComment> Comments);
public sealed record EngagementSeen(int FirstPage);
public sealed record EngagementText(string Value);
public sealed record EngagementComment(string ChannelId, YouTubeComment Comment, IReadOnlyList<string> TextKeys,
    DateTimeOffset ObservedAt, string ScanKey);
public sealed record EngagementInboxPointer(string VersionKey, DateTimeOffset ObservedAt, string? ContentHash = null);
public sealed record EngagementChange(string VersionKey, bool New, bool Changed);

public sealed partial class YouTubeManagerAgent
{
    private async Task<PersonalTodoResult> ProcessEngagementDraft(PersonalTodoItem item,
        AgentOperatingState<TurnState> turn, Preferences? preferences, AgentRuntimeContext context, CancellationToken ct)
    {
        try
        {
            if (turn.Payload.Phase is not ("Generated" or "Delivered"))
            {
                var scan = await AdvanceEngagementScan($"youtube.engagement:{item.Id:N}", context, ct);
                if (!scan.Complete)
                    return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(1),
                        "Comment review is saved. Continue the next page without repeating completed work.");
                var review = await AdvanceEngagementDrafts(item, turn.Payload.Input, scan, preferences, context, ct);
                if (!review.Complete)
                    return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(1),
                        "The review batch is saved. Continue preparing the remaining comments; no replies have been posted.");
                var draft = scan.CommentCount == 0 ? "No available published comments were returned, so no reply drafts were created." :
                    $"Comment review complete: {review.Drafted} reply drafts, {review.NeedsReview} items needing human review, " +
                    $"and {review.NoReply} comments needing no reply. The drafts and review notes are above.";
                draft += " This covers returned published threads and replies, not held or spam queues or a guaranteed point-in-time snapshot. Nothing has been posted.";
                turn = await Save(context, item.CorrelationId!, turn.Payload with { Phase = "Generated", Response = draft }, turn.Revision, ct);
            }
            await context.Platform.Communication.SendMessageAsync(Guid.Parse(turn.Payload.Input.ConversationId),
                turn.Payload.Response!, $"youtube-result:{turn.Payload.Input.MessageId:N}", ct);
            await Save(context, item.CorrelationId!, turn.Payload with { Phase = "Delivered" }, turn.Revision, ct);
            return PersonalTodoResult.Completed("The saved comment review was delivered; no replies were posted.");
        }
        catch (PlatformCapabilityException e) when (YouTubeCapabilities.All.Contains(e.Capability) &&
            e.Code is PlatformCapabilityErrorCode.BudgetExceeded or PlatformCapabilityErrorCode.Unavailable)
        {
            return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddHours(1),
                "Comment review is saved. Provider availability or usage limits require a later check.");
        }
        catch (Exception e) when (e is PlatformCapabilityException or InvalidOperationException or JsonException)
        {
            return PersonalTodoResult.Blocked("Comment review could not safely continue. Check channel access and the saved review before resuming. Nothing was posted.");
        }
    }

    // One provider page per durable dispatch. The receipt is persisted before moving the scan cursor.
    private static async Task<EngagementScan> AdvanceEngagementScan(string key, AgentRuntimeContext context, CancellationToken ct,
        string? expectedChannel = null, string? comparisonScope = null)
    {
        var client = new YouTubeClient(context.Platform);
        var saved = await context.Platform.ReadOperatingStateAsync<EngagementScan>(key, ct);
        var channels = await client.ReadChannelAsync(ct);
        if (channels.Items.Count != 1 || channels.NextPageToken is not null || string.IsNullOrWhiteSpace(channels.Items[0].Id))
            throw new InvalidOperationException("One confirmed channel is required.");
        if (expectedChannel is not null && channels.Items[0].Id != expectedChannel)
            throw new PlatformCapabilityException(YouTubeCapabilities.ReadChannel, PlatformCapabilityErrorCode.Denied, "The monitoring channel binding changed.");
        if (saved is null)
            saved = await Write(context, key, new EngagementScan(channels.Items[0].Id, DateTimeOffset.UtcNow), null,
                $"{key}:start", "Scanning", ct);
        var scan = saved.Payload;
        if (scan.ChannelId != channels.Items[0].Id)
            throw new InvalidOperationException("The scan channel changed.");
        if (scan.Complete) return scan;
        if (scan.Page >= 10000)
            throw new InvalidOperationException("The scan binding or page budget requires review.");
        var parents = scan.Parents ?? [];
        var parent = scan.ParentIndex < parents.Count ? parents[scan.ParentIndex] : null;
        var cursor = Hash(JsonSerializer.Serialize(new { scan.ChannelId, parent = parent?.Id,
            token = parent is null ? scan.ThreadToken : scan.ReplyToken }));
        var receiptKey = $"{key}:page:{scan.Page}";
        var receipt = await context.Platform.ReadOperatingStateAsync<EngagementPageReceipt>(receiptKey, ct);
        if (receipt is not null)
        {
            if (receipt.Payload.CursorHash != cursor) throw new InvalidOperationException("The saved page binding changed.");
            return (await Write(context, key, receipt.Payload.Next, saved.Revision, $"{key}:advance:{scan.Page}",
                receipt.Payload.Next.Complete ? "Scanned" : "Scanning", ct)).Payload;
        }
        var visitedKey = $"{key}:cursor:{cursor}";
        var visited = await context.Platform.ReadOperatingStateAsync<EngagementSeen>(visitedKey, ct);
        if (visited is not null && visited.Payload.FirstPage != scan.Page)
            throw new InvalidOperationException("Provider pagination repeated a cursor.");
        if (visited is null) await Write(context, visitedKey, new EngagementSeen(scan.Page), null, visitedKey, "Visited", ct);
        var pendingKey = $"{key}:pending:{scan.Page}";
        var frozen = await context.Platform.ReadOperatingStateAsync<EngagementPendingPage>(pendingKey, ct);
        if (frozen is not null && frozen.Payload.CursorHash != cursor)
            throw new InvalidOperationException("The frozen provider page changed its binding.");
        var records = new List<(YouTubeComment Comment, bool TopLevel)>();
        EngagementScan next = frozen?.Payload.Next ?? scan;
        if (frozen is null && parent is null)
        {
            var page = await client.ListCommentThreadsAsync(new(scan.ThreadToken), ct);
            ValidatePage(page.Items.Count, page.NextPageToken);
            var pending = new List<EngagementParent>();
            foreach (var thread in page.Items)
            {
                var snippet = thread.Snippet;
                if (snippet?.ChannelId != scan.ChannelId || snippet.TopLevelComment is null || snippet.TotalReplyCount is null or < 0)
                    throw new InvalidOperationException("A thread did not prove its channel or reply count.");
                ValidateComment(snippet.TopLevelComment, scan.ChannelId, null);
                records.Add((snippet.TopLevelComment with { Snippet = snippet.TopLevelComment.Snippet! with
                    { VideoId = snippet.TopLevelComment.Snippet!.VideoId ?? snippet.VideoId } }, true));
                if (snippet.TotalReplyCount > 0 && await context.Platform.ReadOperatingStateAsync<EngagementSeen>(
                        $"{key}:parent:{Hash(snippet.TopLevelComment.Id)}", ct) is null)
                    pending.Add(new(snippet.TopLevelComment.Id, snippet.VideoId));
            }
            next = scan with { ThreadToken = page.NextPageToken, Parents = pending.DistinctBy(x => x.Id).ToArray(),
                ParentIndex = 0, ReplyToken = null, Complete = page.NextPageToken is null && pending.Count == 0 };
        }
        else if (frozen is null && parent is not null)
        {
            var page = await client.ListCommentRepliesAsync(new(parent.Id, scan.ReplyToken), ct);
            ValidatePage(page.Items.Count, page.NextPageToken);
            foreach (var comment in page.Items)
            {
                ValidateComment(comment, scan.ChannelId, parent.Id);
                records.Add((comment with { Snippet = comment.Snippet! with { VideoId = comment.Snippet!.VideoId ?? parent.VideoId } }, false));
            }
            var index = page.NextPageToken is null ? scan.ParentIndex + 1 : scan.ParentIndex;
            next = scan with { ParentIndex = index, ReplyToken = page.NextPageToken,
                Complete = page.NextPageToken is null && index == parents.Count && scan.ThreadToken is null };
        }
        var pageComments = new List<EngagementPageComment>();
        foreach (var record in records.DistinctBy(x => x.Comment.Id))
        {
            ct.ThrowIfCancellationRequested();
            var text = record.Comment.Snippet!.TextOriginal ?? record.Comment.Snippet.TextDisplay ?? "";
            var textKeys = new List<string>();
            for (var offset = 0; offset < text.Length;)
            {
                var length = Math.Min(500, text.Length - offset);
                if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]) && char.IsLowSurrogate(text[offset + length])) length--;
                var chunk = text.Substring(offset, length);
                offset += length;
                var textKey = $"youtube.engagement-text:{Hash(chunk)}";
                if (await context.Platform.ReadOperatingStateAsync<EngagementText>(textKey, ct) is null)
                    await Write(context, textKey, new EngagementText(chunk), null, textKey, "Evidence", ct);
                textKeys.Add(textKey);
            }
            var value = new EngagementComment(scan.ChannelId, record.Comment with
            { Snippet = record.Comment.Snippet with { TextOriginal = null, TextDisplay = null } }, textKeys, scan.StartedAt, key);
            var versionKey = $"youtube.engagement-version:{Hash(JsonSerializer.Serialize(value))}";
            if (await context.Platform.ReadOperatingStateAsync<EngagementComment>(versionKey, ct) is null)
                await Write(context, versionKey, value, null, versionKey, "Evidence", ct);
            pageComments.Add(new(record.Comment.Id, versionKey, record.TopLevel));
        }
        // Freeze the full page before deduplication, inbox updates or counts. A checkpoint failure
        // must not mix one provider response with a later response for the same moving page token.
        if (frozen is null)
            frozen = await Write(context, pendingKey, new EngagementPendingPage(cursor, next, pageComments), null, pendingKey, "Frozen", ct);
        var count = scan.CommentCount;
        var newComments = scan.NewComments; var changedComments = scan.ChangedComments;
        var sample = (scan.SampleKeys ?? []).ToList();
        foreach (var record in frozen.Payload.Comments)
        {
            ct.ThrowIfCancellationRequested();
            var identity = Hash(scan.ChannelId + ":" + record.Id);
            var seenKey = $"{key}:comment:{identity}";
            var seen = await context.Platform.ReadOperatingStateAsync<EngagementSeen>(seenKey, ct);
            if (seen is not null && seen.Payload.FirstPage != scan.Page) continue;
            var inboxKey = $"youtube.engagement-inbox:{identity}";
            var inbox = await context.Platform.ReadOperatingStateAsync<EngagementInboxPointer>(inboxKey, ct);
            var comparisonKey = comparisonScope is null ? inboxKey : $"{comparisonScope}:{identity}";
            var compared = comparisonScope is null ? inbox :
                await context.Platform.ReadOperatingStateAsync<EngagementInboxPointer>(comparisonKey, ct);
            var value = (await context.Platform.ReadOperatingStateAsync<EngagementComment>(record.VersionKey, ct))?.Payload
                ?? throw new InvalidOperationException("Frozen comment evidence is missing.");
            var contentHash = Hash(JsonSerializer.Serialize(new { value.ChannelId, value.Comment.Id, value.TextKeys,
                value.Comment.Snippet!.ParentId, value.Comment.Snippet.VideoId, value.Comment.Snippet.ModerationStatus }));
            var changeKey = $"{key}:change:{identity}";
            var change = await context.Platform.ReadOperatingStateAsync<EngagementChange>(changeKey, ct);
            if (change is null)
                change = await Write(context, changeKey, new EngagementChange(record.VersionKey, compared is null,
                    compared is not null && compared.Payload.ObservedAt <= scan.StartedAt && compared.Payload.ContentHash != contentHash),
                    null, changeKey, "Compared", ct);
            if (change.Payload.VersionKey != record.VersionKey)
                throw new InvalidOperationException("The comment change belongs to another frozen version.");
            if (inbox is null || inbox.Payload.ObservedAt <= scan.StartedAt)
                await Write(context, inboxKey, new EngagementInboxPointer(record.VersionKey, scan.StartedAt, contentHash), inbox?.Revision,
                    $"{key}:inbox:{scan.Page}:{identity}", "Synchronized", ct);
            if (comparisonScope is not null && (compared is null || compared.Payload.ObservedAt <= scan.StartedAt))
                await Write(context, comparisonKey, new EngagementInboxPointer(record.VersionKey, scan.StartedAt, contentHash), compared?.Revision,
                    $"{key}:comparison:{scan.Page}:{identity}", "Compared", ct);
            if (seen is null) await Write(context, seenKey, new EngagementSeen(scan.Page), null, seenKey, "Observed", ct);
            count++;
            if (change.Payload.New) newComments++;
            if (change.Payload.Changed) changedComments++;
            if (record.TopLevel && sample.Count < 5) sample.Add(record.VersionKey);
        }
        next = next with { Page = scan.Page + 1, CommentCount = count, SampleKeys = sample,
            NewComments = newComments, ChangedComments = changedComments };
        if (parent is not null && next.ReplyToken is null)
        {
            var parentKey = $"{key}:parent:{Hash(parent.Id)}";
            if (await context.Platform.ReadOperatingStateAsync<EngagementSeen>(parentKey, ct) is null)
                await Write(context, parentKey, new EngagementSeen(scan.Page), null, parentKey, "Scanned", ct);
        }
        await Write(context, receiptKey, new EngagementPageReceipt(cursor, next), null, receiptKey, "Saved", ct);
        return (await Write(context, key, next, saved.Revision, $"{key}:advance:{scan.Page}", next.Complete ? "Scanned" : "Scanning", ct)).Payload;
    }

    private static void ValidatePage(int count, string? token)
    {
        if (count > 100 || token is not null && (string.IsNullOrWhiteSpace(token) || token.Length > 1024))
            throw new InvalidOperationException("An invalid provider page was returned.");
    }

    private static void ValidateComment(YouTubeComment comment, string channel, string? parent)
    {
        if (string.IsNullOrWhiteSpace(comment.Id) || comment.Id.Length > 128 || comment.Snippet is null ||
            comment.Snippet.ChannelId is { } owner && owner != channel || comment.Snippet.ParentId != parent ||
            (comment.Snippet.TextOriginal?.Length ?? 0) > 10000 || (comment.Snippet.TextDisplay?.Length ?? 0) > 10000)
            throw new InvalidOperationException("Comment ownership, parent or content bounds did not match.");
    }
}
