using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube;

public sealed record VideoEditConversationPointer(Guid RootMessageId, long SourceSequence);
public sealed record VideoEditFollowupBinding(Guid RootMessageId, int Revision, Guid? ActionId, string Status);
public sealed record VideoEditClarification(Guid MessageId, long Sequence, string ContentHash);

public sealed partial class YouTubeManagerAgent
{
    private static string EditPointerKey(Guid conversation, Guid requester) => $"youtube.edit-conversation:{conversation:N}:{requester:N}";

    private async Task<string> ConverseVideoEditFollowup(AgentOperatingState<TurnState> turn, CommunicationMessage source,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var input = turn.Payload.Input; var key = $"youtube.turn:{input.MessageId:N}";
        async Task<string> Answer(string answer)
        {
            await Save(context, key, turn.Payload with { Response = answer, Phase = "Answered" }, turn.Revision, ct);
            return answer;
        }
        if (turn.Payload.EditFollowup is not { } binding || binding.RootMessageId == Guid.Empty)
            return await Answer("I don't have a video edit waiting for your details here. Tell me which video and change you want; I'll prepare it for review.");
        var rootKey = $"youtube.turn:{binding.RootMessageId:N}";
        var root = await context.Platform.ReadOperatingStateAsync<TurnState>(rootKey, ct);
        if (root?.Payload.VideoEdit is not { } edit || root.Payload.Input.ConversationId != input.ConversationId ||
            edit.RequesterId != source.SenderOrganizationUserId || edit.RequesterId == Guid.Empty || root.Payload.TodoId is not { } todoId)
            return await Answer("I couldn't match that reply to your saved edit in this conversation. Please review the original request.");
        var reference = new VideoEditClarification(source.Id, source.Sequence, Hash(source.Content));
        var applied = edit.Clarifications?.SingleOrDefault(x => x.MessageId == source.Id);
        if (applied is not null && applied != reference) throw new InvalidOperationException("The retained clarification changed.");
        var targetKey = EditTargetKey(edit.ChannelId, edit.Snapshot.Id);
        var reservation = await context.Platform.ReadOperatingStateAsync<VideoEditReservation>(targetKey, ct);
        var pointer = await context.Platform.ReadOperatingStateAsync<VideoEditConversationPointer>(EditPointerKey(source.ChatId, edit.RequesterId), ct);
        if (applied is null)
        {
            if (pointer?.Payload.RootMessageId != binding.RootMessageId || source.Sequence <= pointer.Payload.SourceSequence ||
                edit.Revision != binding.Revision || edit.ActionId != binding.ActionId || edit.Status != binding.Status || !edit.NoticeSent ||
                edit.Status is not ("NeedsDetails" or "Conflict") || reservation?.Payload.SourceMessageId != binding.RootMessageId ||
                reservation.Payload.StateKey != rootKey || reservation.Payload.TodoId != todoId)
                return await Answer("That edit has moved on since this question. Please use its current review; this reply has not changed or approved it.");
            if (edit.Revision >= 10 || (edit.Clarifications?.Count ?? 0) >= 10 ||
                source.Sequence <= (edit.Clarifications?.Select(x => x.Sequence).DefaultIfEmpty(0).Max() ?? 0))
                return await Answer("This edit needs a fresh review before more revisions. I won't change a newer draft using an older reply.");
            var client = new YouTubeClient(context.Platform);
            if (edit.Status == "Conflict")
            {
                if (edit.ActionId is not { } conflict) throw new InvalidOperationException("The conflicting action is missing.");
                await RequireEditConflict(client, conflict, ct);
            }
            else if (edit.ActionId is not null) throw new InvalidOperationException("A clarification cannot replace a prepared action.");
            if (edit.PreviousActionId is { } previous)
            {
                var decision = await client.ReadMetadataEditAsync(previous, ct);
                if (decision is not { Status: "RevisionRequested", Decision.Decision: "RequestRevision" } || decision.Decision.Comment != edit.Feedback)
                    throw new InvalidOperationException("The prior revision decision is no longer current.");
            }
            var channels = await client.ReadChannelAsync(ct);
            var videos = await client.ReadVideoAsync(new(edit.Snapshot.Id), ct);
            if (channels.Items.Count != 1 || channels.NextPageToken is not null || channels.Items[0].Id != edit.ChannelId ||
                videos.Items.Count != 1 || videos.NextPageToken is not null || videos.Items[0].Id != edit.Snapshot.Id || videos.Items[0].Snippet?.ChannelId != edit.ChannelId)
                throw new InvalidOperationException("The saved edit's channel and video could not be verified.");
            _ = YouTubeClient.StrongProviderETag(videos.Items[0].Etag);
            var next = new VideoEditWork(edit.ChannelId, videos.Items[0], Revision: edit.Revision + 1,
                PreviousActionId: edit.PreviousActionId, Feedback: edit.Feedback, PreviousRequest: edit.Request ?? edit.PreviousRequest,
                RequesterId: edit.RequesterId, Clarifications: [.. edit.Clarifications ?? [], reference],
                ConflictActionId: edit.Status == "Conflict" ? edit.ActionId : edit.ConflictActionId);
            root = await Save(context, rootKey, root.Payload with { VideoEdit = next, Phase = "EditFollowupSaved" }, root.Revision, ct);
            edit = next;
        }
        // Root persistence precedes the wake. A replay recognizes the exact source reference and
        // never refreshes the snapshot, increments the revision or creates another personal task.
        if (reservation?.Payload.SourceMessageId == binding.RootMessageId && reservation.Payload.Status != edit.Status)
            await Write(context, targetKey, reservation.Payload with { Status = edit.Status }, reservation.Revision,
                $"youtube-edit-followup-reservation:{source.Id:N}", edit.Status, ct);
        var directory = await context.Platform.PersonalTodo.ListAsync(ct);
        var item = directory.Boards.SelectMany(x => x.Items).SingleOrDefault(x => x.Id == todoId &&
            x.OwnerOrganizationUserId.ToString("D") == context.Identity?.EmployeeId && x.CreatedByOrganizationUserId == x.OwnerOrganizationUserId &&
            x.SourceMessageId == binding.RootMessageId && x.SourceConversationId == source.ChatId && x.CorrelationId == rootKey && x.ArchivedAt is null);
        if (item is null) throw new InvalidOperationException("The saved edit obligation is unavailable.");
        if (edit.Status == "Preparing" && edit.Notice is null && item.Status is "Running" or "Blocked")
            await context.Platform.PersonalTodo.RequeueAsync(new(item.Id, item.Revision, $"youtube-edit-followup-wake:{source.Id:N}"), ct);
        return await Answer("I've added your reply to the same video-edit request. I'll prepare a fresh draft for approval; your message has not approved or applied a change.");
    }

    private static IReadOnlyList<string> RequireEditClarifications(VideoEditWork edit, CommunicationMessages chat, string conversationId)
    {
        var references = edit.Clarifications ?? [];
        if (references.Count > 10 || references.Select(x => x.MessageId).Distinct().Count() != references.Count)
            throw new InvalidOperationException("The edit's clarification history is malformed.");
        var content = new List<string>(); long sequence = 0;
        foreach (var reference in references)
        {
            var message = chat.Messages.SingleOrDefault(x => x.Id == reference.MessageId && x.ChatId.ToString("D") == conversationId &&
                x.SenderOrganizationUserId == edit.RequesterId && x.Sequence == reference.Sequence);
            if (message is null || reference.Sequence <= sequence || Hash(message.Content) != reference.ContentHash)
                throw new InvalidOperationException("A source clarification is missing, changed or belongs to another requester.");
            sequence = reference.Sequence; content.Add(message.Content);
        }
        if (content.Sum(x => x.Length) > 32000) throw new InvalidOperationException("The edit's clarification history requires a bounded fresh review.");
        return content;
    }

    private static async Task RequireEditConflict(YouTubeClient client, Guid actionId, CancellationToken ct)
    {
        var action = await client.ReadMetadataEditAsync(actionId, ct);
        if (action is not { Status: "Blocked", ConditionCode: "resource_changed" })
            throw new InvalidOperationException("Only a confirmed version conflict can start a fresh conditional review.");
    }
}
