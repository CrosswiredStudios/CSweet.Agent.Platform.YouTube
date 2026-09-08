using System.Text.Json;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;

namespace CSweet.Agent.Platform.YouTube;

public sealed record UploadWork(PublicationMedia Media, UploadVideoRequest Request, string ChannelId,
    Guid? ActionId = null, string? Status = null, bool PreviewSent = false, UploadReview? Review = null,
    DateTimeOffset? ProcessingDeadline = null, bool ProcessingNoticeSent = false, string? Notice = null, bool NoticeSent = false,
    UploadCategoryChoice? Category = null, int Revision = 0, Guid? PreviousActionId = null,
    UploadFields? RevisionFields = null);
public sealed record UploadCategoryChoice(string RegionCode, string Language, string DisplayName);
public sealed record UploadReservation(Guid MessageId, Guid TodoId, string Status);
public sealed record UploadActionLink(string StateKey, Guid TodoId, Guid MessageId, Guid ActionId);

public sealed partial class YouTubeManagerAgent
{
    private async Task<PersonalTodoResult> ProcessUpload(PersonalTodoItem item, AgentOperatingState<TurnState> state,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var key = item.CorrelationId!;
        if (state.Payload.UploadWork is not { } upload)
            return PersonalTodoResult.Blocked("The upload needs a saved video selection and reviewed metadata before it can proceed.");
        var youtube = new YouTubeClient(context.Platform);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        try
        {
            YouTubeClient.ValidateUpload(upload.Request);
            if (upload.Request.MediaAssetId != upload.Media.AssetId ||
                upload.Media.Source.ConversationId.ToString("D") != state.Payload.Input.ConversationId ||
                upload.Request.IdempotencyKey != UploadRequestKey(state.Payload.Input.MessageId, upload.Revision) ||
                upload.Revision is < 0 or > 20 || (upload.Revision > 0) != upload.PreviousActionId.HasValue ||
                string.IsNullOrWhiteSpace(upload.ChannelId))
                throw new InvalidOperationException("Upload request bindings changed.");
            if (upload.Notice is not null) return await DeliverUploadNotice(item, state, context, ct);
            if (state.Payload.TodoId is null)
                state = await Save(context, key, state.Payload with { TodoId = item.Id }, state.Revision, ct);
            var reservationKey = $"youtube.upload-media:{Hash(upload.ChannelId + ":" + upload.Media.Sha256)}";
            var reservation = await context.Platform.ReadOperatingStateAsync<UploadReservation>(reservationKey, ct);
            if (reservation is not null && (reservation.Payload.MessageId != state.Payload.Input.MessageId || reservation.Payload.TodoId != item.Id) &&
                reservation.Payload.Status is not ("Cancelled" or "Rejected" or "Expired"))
                return PersonalTodoResult.Blocked("This video already has a saved upload request. Review that request instead of creating another upload.");
            if (reservation is null || reservation.Payload.MessageId != state.Payload.Input.MessageId)
                await Write(context, reservationKey, new UploadReservation(state.Payload.Input.MessageId, item.Id, "Reserved"), reservation?.Revision,
                    $"youtube-upload-reserve:{state.Payload.Input.MessageId:N}", "Reserved", ct);
            var preferences = await context.Platform.ReadOperatingStateAsync<Preferences>("youtube.preferences", ct);
            var paused = preferences?.Payload.Values.GetValueOrDefault("paused") == "true";
            if (paused && upload.ActionId is null)
            {
                if (!state.Payload.Paused) await Save(context, key, state.Payload with { Paused = true }, state.Revision, ct);
                return PersonalTodoResult.WaitingUntil(now.AddMinutes(15), "Upload preparation is paused.");
            }
            if (state.Payload.Paused) state = await Save(context, key, state.Payload with { Paused = false }, state.Revision, ct);
            if (upload.ActionId is null)
            {
                if (upload.PreviousActionId is { } previousActionId)
                {
                    var previous = await youtube.ReadUploadActionAsync(previousActionId, ct);
                    if (previous.Status != "RevisionRequested" || previous.Decision?.Decision != "RequestRevision")
                        throw new InvalidOperationException("The previous upload is not safely superseded by a revision.");
                }
                var chat = await context.Platform.Communication.ReadChatAsync(upload.Media.Source.ConversationId, ct);
                var source = chat.Messages.SingleOrDefault(x => x.Id == upload.Media.Source.MessageId && x.ChatId == upload.Media.Source.ConversationId);
                if (source is null || !PublicationMedia.Capture(source).Contains(upload.Media))
                    throw new InvalidOperationException("The saved upload source is no longer available.");
                var channel = await youtube.ReadChannelAsync(ct);
                if (channel.NextPageToken is not null || channel.Items.Count != 1 || channel.Items[0].Id != upload.ChannelId)
                    throw new InvalidOperationException("The connected channel no longer matches the saved upload.");
                if (upload.Category is not { } selection)
                    throw new InvalidOperationException("Choose a category before preparing the upload.");
                var category = await youtube.ResolveVideoCategoryAsync(new(selection.RegionCode, selection.Language), selection.DisplayName, ct);
                if (category.Id != upload.Request.CategoryId)
                    throw new InvalidOperationException("The category no longer matches the saved upload metadata.");
                if (!upload.PreviewSent)
                {
                    await context.Platform.Communication.SendMessageAsync(Guid.Parse(state.Payload.Input.ConversationId),
                        "Video upload prepared for approval — not uploaded.\n\nFile: " + Quote(upload.Media.FileName) +
                        "\n\nTitle:\n" + Quote(upload.Request.Title) + "\n\nDescription:\n" + Quote(upload.Request.Description) +
                        "\n\nCategory:\n" + Quote(category.Snippet.Title) +
                        "\n\nTags:\n" + Quote(string.Join(", ", upload.Request.Tags ?? [])) +
                        $"\n\nVisibility: {upload.Request.PrivacyStatus}. Made for children: {upload.Request.MadeForKids}. " +
                        $"Altered/synthetic media disclosure: {upload.Request.ContainsSyntheticMedia}. Notify subscribers: {upload.Request.NotifySubscribers}.",
                        UploadNoticeKey("preview", state.Payload.Input.MessageId, upload.Revision), ct);
                    upload = upload with { PreviewSent = true };
                    state = await Save(context, key, state.Payload with { UploadWork = upload }, state.Revision, ct);
                }
            }
            var action = upload.ActionId is { } actionId ? await youtube.ReadUploadActionAsync(actionId, ct)
                : await youtube.RequestUploadAsync(upload.Request, upload.Media.Source, ct);
            if (upload.ActionId is null)
            {
                upload = upload with { ActionId = action.ActionId, Status = action.Status };
                state = await Save(context, key, state.Payload with { UploadWork = upload, Phase = "UploadActionSaved" }, state.Revision, ct);
            }
            var linkKey = $"youtube.upload-action:{action.ActionId:N}";
            var link = await context.Platform.ReadOperatingStateAsync<UploadActionLink>(linkKey, ct);
            var expectedLink = new UploadActionLink(key, item.Id, state.Payload.Input.MessageId, action.ActionId);
            if (link is null) await Write(context, linkKey, expectedLink, null, linkKey, "Linked", ct);
            else if (link.Payload != expectedLink) throw new InvalidOperationException("The upload action belongs to another request.");
            if (paused && action.Status is "AwaitingApproval" or "Approved")
            {
                try { action = await youtube.CancelPendingUploadAsync(action.ActionId, $"youtube-upload-pause:{action.ActionId:N}", ct); }
                catch (PlatformCapabilityException) { action = await youtube.ReadUploadActionAsync(action.ActionId, ct); }
            }
            upload = upload with { Status = action.Status };
            state = await Save(context, key, state.Payload with { UploadWork = upload, Phase = "Upload" + action.Status }, state.Revision, ct);
            if (action.Status is "AwaitingApproval" or "Approved" or "Executing")
                return PersonalTodoResult.WaitingUntil(now.AddMinutes(5), "Waiting for the exact upload decision or provider result.");
            if (action.Status == "RevisionRequested" && action.Decision is { Decision: "RequestRevision", Comment.Length: > 0 })
            {
                if (paused) return PersonalTodoResult.WaitingUntil(now.AddMinutes(15), "Upload revision preparation is paused.");
                state = await ReviseUpload(state, action, null, context, ct);
                if (state.Payload.UploadWork!.ActionId is null)
                    return PersonalTodoResult.WaitingUntil(now.AddSeconds(1), "The revised upload is saved and requires a new exact approval.");
                return await DeliverUploadNotice(item, state, context, ct);
            }
            if (action.Status is "Completed" or "Indeterminate")
            {
                var review = await new YouTubeUploadReconciler(youtube).InspectAsync(action.ActionId, upload.Request, ct);
                upload = upload with { Review = review, Status = review.Status };
                if (review.Status == "Processing")
                {
                    upload = upload with { ProcessingDeadline = upload.ProcessingDeadline ?? now.AddDays(2) };
                    state = await Save(context, key, state.Payload with { UploadWork = upload, Phase = "UploadProcessing" }, state.Revision, ct);
                    if (!upload.ProcessingNoticeSent)
                    {
                        await context.Platform.Communication.SendMessageAsync(Guid.Parse(state.Payload.Input.ConversationId),
                            "YouTube received your video and is still processing it. I'll check the existing upload; I won't upload it again.",
                            UploadNoticeKey("processing", state.Payload.Input.MessageId, upload.Revision), ct);
                        upload = upload with { ProcessingNoticeSent = true };
                        state = await Save(context, key, state.Payload with { UploadWork = upload }, state.Revision, ct);
                    }
                    if (now < upload.ProcessingDeadline)
                        return PersonalTodoResult.WaitingUntil(now.AddMinutes(15), "Check the existing video's processing status.");
                    upload = upload with { Status = "ReviewRequired", Notice = "YouTube still hasn't confirmed processing after two days. Please review the existing video; I won't upload a replacement." };
                }
                else upload = upload with { Notice = review.Status == "Processed"
                    ? $"Your video upload and requested {upload.Request.PrivacyStatus} visibility are confirmed. [View the video](https://www.youtube.com/watch?v={review.Video!.Id})."
                    : "I couldn't confirm the requested upload result. " + review.ReviewReason + " I won't upload a replacement automatically." };
            }
            else upload = upload with { Notice = action.Status switch
            {
                "Cancelled" => "The pending upload was cancelled before execution. Resuming work will not restore it.",
                "Rejected" => "The upload was rejected and will not be started.",
                "RevisionRequested" => "The approver requested changes. The saved upload has not started; its metadata needs revision and a new exact review.",
                "Expired" => "The upload approval expired before execution. It needs a fresh review.",
                _ => "The upload is blocked or its access changed. I cannot confirm a completed upload; review the saved request before trying again."
            } };
            state = await Save(context, key, state.Payload with { UploadWork = upload, Phase = "Upload" + upload.Status }, state.Revision, ct);
            return await DeliverUploadNotice(item, state, context, ct);
        }
        catch (PlatformCapabilityException error)
        {
            if (error.Code == PlatformCapabilityErrorCode.Denied)
            {
                try
                {
                    var notice = await context.Platform.Communication.SendMessageAsync(Guid.Parse(state.Payload.Input.ConversationId),
                        "This saved upload needs a YouTube access check. Review the connection and enable Publishing if needed. Google consent must be completed by an authorized human. Tell me to continue afterwards; I won't create a replacement upload.",
                        UploadNoticeKey("access", state.Payload.Input.MessageId, upload.Revision), ct);
                    await context.Platform.SuggestUserActionAsync(new(notice.Id, null, UserActionWorkflows.PluginSetupOpen,
                        "Review YouTube publishing access", "Review the connection and publishing permission securely.", SerializePayload(new { }),
                        UploadNoticeKey("settings", state.Payload.Input.MessageId, upload.Revision)), ct);
                }
                catch (PlatformCapabilityException) { }
            }
            return error.Code is PlatformCapabilityErrorCode.BudgetExceeded or PlatformCapabilityErrorCode.Unavailable
                ? PersonalTodoResult.WaitingUntil(now.AddHours(1), "Recheck the same saved upload after access or persistence recovers; do not create a replacement.")
                : PersonalTodoResult.Blocked("The saved upload needs access or persistence recovery. Do not create a replacement upload.");
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or JsonException)
        { return PersonalTodoResult.Blocked("The saved video, channel or upload result could not be verified safely. Review the original request; no replacement upload was created."); }
    }

    private static async Task<PersonalTodoResult> DeliverUploadNotice(PersonalTodoItem item, AgentOperatingState<TurnState> state,
        AgentRuntimeContext context, CancellationToken ct)
    {
        var upload = state.Payload.UploadWork!;
        var reservationKey = $"youtube.upload-media:{Hash(upload.ChannelId + ":" + upload.Media.Sha256)}";
        var reservation = await context.Platform.ReadOperatingStateAsync<UploadReservation>(reservationKey, ct);
        if (reservation?.Payload.MessageId == state.Payload.Input.MessageId && reservation.Payload.TodoId == item.Id && reservation.Payload.Status != upload.Status)
            await Write(context, reservationKey, reservation.Payload with { Status = upload.Status! }, reservation.Revision,
                $"youtube-upload-reservation-result:{state.Payload.Input.MessageId:N}:{upload.Revision}:{upload.Status}", upload.Status!, ct);
        if (!upload.NoticeSent)
        {
            await context.Platform.Communication.SendMessageAsync(Guid.Parse(state.Payload.Input.ConversationId), upload.Notice!,
                UploadNoticeKey("result", state.Payload.Input.MessageId, upload.Revision), ct);
            await Save(context, item.CorrelationId!, state.Payload with { UploadWork = upload with { NoticeSent = true } }, state.Revision, ct);
        }
        return upload.Status is "Processed" or "Cancelled" or "Rejected"
            ? PersonalTodoResult.Completed("The verified upload outcome was saved and reported.")
            : PersonalTodoResult.Blocked("The existing upload requires review. Do not upload a replacement.");
    }

    private static async Task WakeUpload(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken ct)
    {
        var changed = DeserializePayload<ConnectorActionChanged>(message.Data);
        if (changed?.Capability != YouTubeCapabilities.UploadVideo || changed.ActionId == Guid.Empty) return;
        var link = await context.Platform.ReadOperatingStateAsync<UploadActionLink>($"youtube.upload-action:{changed.ActionId:N}", ct);
        if (link is null || link.Payload.ActionId != changed.ActionId) return;
        var state = await context.Platform.ReadOperatingStateAsync<TurnState>(link.Payload.StateKey, ct);
        if (state?.Payload.UploadWork?.ActionId != changed.ActionId || state.Payload.UploadWork.NoticeSent ||
            state.Payload.TodoId != link.Payload.TodoId || state.Payload.Input.MessageId != link.Payload.MessageId) return;
        var directory = await context.Platform.PersonalTodo.ListAsync(ct);
        var item = directory.Boards.SelectMany(x => x.Items).SingleOrDefault(x => x.Id == link.Payload.TodoId &&
            x.OwnerOrganizationUserId.ToString("D") == context.Identity?.EmployeeId && x.CreatedByOrganizationUserId == x.OwnerOrganizationUserId &&
            x.CorrelationId == link.Payload.StateKey && x.SourceMessageId == link.Payload.MessageId &&
            x.SourceConversationId?.ToString("D") == state.Payload.Input.ConversationId && x.ArchivedAt is null);
        if (item?.Status is "Running" or "Blocked")
            await context.Platform.PersonalTodo.RequeueAsync(new(item.Id, item.Revision, $"youtube-upload-wake:{message.EventId:N}"), ct);
    }
}
