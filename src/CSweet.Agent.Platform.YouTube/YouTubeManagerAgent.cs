using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CSweet.Agent.SDK;
using CSweet.Plugins.Platform.YouTube;
using CSweet.WorkManagement.Contracts;
using Microsoft.Extensions.AI;

namespace CSweet.Agent.Platform.YouTube;

public sealed partial class YouTubeManagerAgent(IAgentLlmClientFactory? modelFactory = null, TimeProvider? clock = null) : CSweetAgentBase
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private static readonly HashSet<string> ReadIntents = ["channel", "video", "playlists", "playlist-items", "comments", "replies", "captions", "broadcast", "stream", "analytics", "members", "members-next", "membership-levels"];
    public override string AgentId => YouTubeManagerProfile.Id;
    public override string Version => "0.2.1";

    protected override async Task<AgentWorkResult> ExecuteCapabilityCoreAsync(AgentCapabilityRequest request,
        AgentRuntimeContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Capability != YouTubeManagerProfile.Assistant)
            return AgentWorkResult.Failure("This request is not supported by YouTube Manager.");
        try
        {
            var input = request.Arguments.Deserialize<AssistantRequest>(Json)
                ?? throw new InvalidOperationException("A conversation request is required.");
            var response = await Converse(input, context, ct);
            return AgentWorkResult.Success(new AssistantResponse(input.ConversationId, response, [], DateTimeOffset.UtcNow));
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { return AgentWorkResult.Failure("I couldn't safely interpret or save that request. Please review the request and try again."); }
        catch (PlatformCapabilityException)
        { return AgentWorkResult.Failure("I couldn't complete that step with the current access. The saved work can be resumed after access is restored."); }
    }

    public override async Task HandleEventAsync(AgentEventEnvelope message, AgentRuntimeContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (message.EventType == AgentLifecycleEvents.Onboarded)
        {
            await StartMonitoring(message, context, ct);
        }
        else if (message.EventType == PluginSetupEvents.Requested)
        {
            var setup = DeserializePayload<PluginSetupRequestedEvent>(message.Data);
            if (setup is null || setup.InstallationId.ToString("D") != context.InstallationId ||
                setup.OrganizationId.ToString("D") != context.BusinessId) return;
            var sent = await context.Platform.Communication.SendMessageAsync(setup.ConversationId,
                setup.Reminder ? "Your YouTube connection is still waiting for setup. You can pick up where you left off."
                    : "I'm your YouTube Manager. Connect your channel below, confirm it is the right one, and we'll agree on your goals. Google sign-in must be completed by an authorized human. Public changes remain subject to approval.",
                $"youtube-setup:{message.EventId:N}", ct);
            await context.Platform.SuggestUserActionAsync(new(sent.Id, null, UserActionWorkflows.PluginSetupOpen,
                "Connect YouTube", "Securely connect or resume setup; no credentials to copy.", SerializePayload(new { }),
                $"youtube-connect:{message.EventId:N}"), ct);
        }
        else if (message.EventType == ConnectorActionEvents.Changed)
        {
            await WakeReply(message, context, ct);
            await WakeUpload(message, context, ct);
            await WakeVideoEdit(message, context, ct);
        }
        else if (message.EventType == CommunicationEvents.MessageReceived)
        {
            var received = DeserializePayload<CommunicationMessageReceivedEvent>(message.Data);
            if (received is null || received.TurnId == Guid.Empty) return;
            var input = new AssistantRequest(received.ProviderProfileId, received.ConversationId, received.Message,
                received.Context, received.UserId, received.MessageId, received.TurnId);
            var stream = context.CreateTurnStream(received.ConversationId, received.TurnId, received.Attempt);
            await stream.WriteDraftAsync("I'm reviewing your YouTube request.", ct);
            await stream.FlushAsync(ct);
            var response = await Converse(input, context, ct);
            await stream.CommitAsync(response, ct);
        }
    }

    private async Task<string> Converse(AssistantRequest input, AgentRuntimeContext context, CancellationToken ct)
    {
        if (!Guid.TryParse(input.ConversationId, out var conversation) || conversation == Guid.Empty ||
            input.MessageId == Guid.Empty || string.IsNullOrWhiteSpace(input.Prompt) || input.Prompt.Length > 16000)
            throw new ArgumentException("A bounded, persisted conversation message is required.");
        var history = await context.Platform.Communication.ReadChatAsync(conversation, ct);
        var source = history.Messages.SingleOrDefault(x => x.Id == input.MessageId && x.ChatId == conversation);
        if (source is null || source.SenderOrganizationUserId is null || source.Content != input.Prompt)
            throw new InvalidOperationException("The source message could not be verified.");
        var key = $"youtube.turn:{input.MessageId:N}";
        AgentOperatingState<TurnState>? saved;
        try { saved = await context.Platform.ReadOperatingStateAsync<TurnState>(key, ct); }
        catch (PlatformCapabilityException e) when (e.Code == PlatformCapabilityErrorCode.Denied)
        {
            // Setup assistance has no operating-state, provider or business-data grants.
            await SuggestSetup(input, context, ct);
            return await Generate(input, context, "Explain or guide setup only. No channel is verified for ordinary work yet. " +
                "The native setup action is available. Do not promise or claim external actions. " +
                "Use the conversation as context, but never request credentials.", input.Prompt, ct);
        }
        var hash = Hash(input.Prompt);
        if (saved is not null && saved.Payload.InputHash != hash)
            throw new InvalidOperationException("A saved request changed.");
        saved ??= await Save(context, key, new(hash, input, "Accepted"), null, ct);
        if (saved.Payload.Response is { } response) return response;
        if (saved.Payload.Intake is null)
        {
            var uploadPointer = await context.Platform.ReadOperatingStateAsync<UploadConversationPointer>(UploadPointerKey(conversation, source.SenderOrganizationUserId.Value), ct);
            var uploadContext = uploadPointer is null ? null : await context.Platform.ReadOperatingStateAsync<TurnState>($"youtube.turn:{uploadPointer.Payload.RootMessageId:N}", ct);
            var editPointer = await context.Platform.ReadOperatingStateAsync<VideoEditConversationPointer>(EditPointerKey(conversation, source.SenderOrganizationUserId.Value), ct);
            var editContext = editPointer is null ? null : await context.Platform.ReadOperatingStateAsync<TurnState>($"youtube.turn:{editPointer.Payload.RootMessageId:N}", ct);
            var waitingEdit = editContext?.Payload.VideoEdit is { NoticeSent: true, Status: "NeedsDetails" or "Conflict" } candidate &&
                candidate.RequesterId == source.SenderOrganizationUserId && editContext.Payload.Input.ConversationId == input.ConversationId ? candidate : null;
            var raw = await Generate(input, context, YouTubeManagerProfile.Routing,
                JsonSerializer.Serialize(new { request = input.Prompt, history = history.Messages.TakeLast(12).Select(x => new { x.SenderEmployeeType, x.Content }),
                    outstandingUploadQuestion = uploadContext?.Payload.UploadWork is null ? uploadContext?.Payload.UploadIntake?.Question : null,
                    uploadAlreadyPrepared = uploadContext?.Payload.UploadWork is not null,
                    outstandingVideoEditQuestion = waitingEdit?.Fields?.Question,
                    videoEditConflictNeedsFreshReview = waitingEdit?.Status == "Conflict" }), ct);
            var intake = ParseIntake(raw);
            ValidateResourceSource(intake, input.Prompt);
            // Bind a follow-up to the obligation used for routing, not a later conversation pointer.
            saved = await Save(context, key, saved.Payload with { Phase = "Routed", Intake = intake,
                UploadRootMessageId = intake.Kind == "UploadFollowup" ? uploadPointer?.Payload.RootMessageId ?? Guid.Empty : null,
                EditFollowup = intake.Kind == "VideoEditFollowup" && waitingEdit is not null
                    ? new(editPointer!.Payload.RootMessageId, waitingEdit.Revision, waitingEdit.ActionId, waitingEdit.Status) : null }, saved.Revision, ct);
        }
        var route = saved.Payload.Intake!;
        string answer;
        try
        {
            switch (route.Kind)
            {
                case "Upload":
                case "UploadFollowup":
                    return await ConverseUpload(saved, source, context, ct);
                case "Action" when route.Intent == "video-upload" && saved.Payload.UploadIntake is not null:
                    return await ConverseUpload(saved, source, context, ct);
                case "Action" when route.Intent == "video-edit":
                    return await ConverseVideoEdit(saved, source, context, ct);
                case "VideoEditFollowup":
                    return await ConverseVideoEditFollowup(saved, source, context, ct);
                case "Setup":
                    await SuggestSetup(input, context, ct);
                    answer = "Use the connection settings action to review access or make that change. Google consent must be completed by an authorized human; I cannot change permissions silently.";
                    break;
                case "Approval":
                    answer = "Please use the approval controls on the proposed action. A chat message alone cannot authorize a public change.";
                    break;
                case "Preference":
                    if (context.Identity?.ManagerEmployeeId != source.SenderOrganizationUserId.Value.ToString("D"))
                        answer = "I'll need my assigned manager to confirm a change to the channel's working preferences.";
                    else
                    {
                        var preferences = await context.Platform.ReadOperatingStateAsync<Preferences>("youtube.preferences", ct);
                        var values = preferences?.Payload.Values.ToDictionary(x => x.Key, x => x.Value) ?? [];
                        ValidatePreference(route.Preference!, route.Value!);
                        values[route.Preference!] = route.Value!;
                        await Write(context, "youtube.preferences", new Preferences(values), preferences?.Revision,
                            $"youtube-preference:{input.MessageId:N}", "Configured", ct);
                        answer = route.Preference == "paused" && route.Value == "false"
                            ? $"Work is resumed. {await ResumePaused(context, input.MessageId, ct)} saved requests can continue now; any remaining paused requests resume at their next check. Other access or approval blocks remain in place."
                            : route.Preference == "paused" ? $"I've paused queued preparation. {await PausePendingReplies(context, input.MessageId, ct)} pending replies were cancelled before execution. Changes already in progress or with uncertain results still need review. Saved drafts remain available."
                            : "I've recorded that preference. No new task or public change was started.";
                    }
                    break;
                case "Action":
                case "Deliverable":
                    if (route.Intent == "publication-plan" && saved.Payload.PublicationMedia is null)
                        saved = await Save(context, key, saved.Payload with { PublicationMedia = PublicationMedia.Capture(source) }, saved.Revision, ct);
                    if (route.Kind == "Action" && await ResumeExistingReply(route.ResourceId!, input, context, ct) is { } existingReply)
                    {
                        answer = existingReply;
                        break;
                    }
                    var todo = await context.Platform.PersonalTodo.AddAsync(new(
                        route.Kind == "Action" ? "Prepare and approve a YouTube comment reply" : route.Intent == "analytics" ? "Prepare YouTube performance report" : route.Intent == "reply-drafts" ? "Draft YouTube comment replies" : "Prepare YouTube publication plan",
                        route.Kind == "Action" ? "Verify the target, save the draft, request exact approval and reconcile the result." : "Prepare and review the requested deliverable; do not publish external changes.", "Normal", null,
                        $"youtube-{(route.Kind == "Action" ? "reply-work" : "deliverable")}:{input.MessageId:N}", SourceConversationId: conversation, SourceMessageId: input.MessageId,
                        CorrelationId: key), ct);
                    saved = await Save(context, key, saved.Payload with { Phase = "Queued", TodoId = todo.Id }, saved.Revision, ct);
                    answer = route.Kind == "Action" ? "I'll prepare the reply and send it for approval. I'll report the confirmed result here; nothing has been posted yet."
                        : "I've saved that request and queued the draft. I'll bring the result back here; nothing will be published automatically.";
                    break;
                case "Read":
                    if (IsMembershipRead(route.Intent)) saved = await LoadMembershipRead(saved, source, context, ct);
                    if (saved.Payload.Evidence is null)
                    {
                        var client = new YouTubeClient(context.Platform);
                        if (route.ResourceQuery is not null && saved.Payload.ResolvedResourceId is null)
                        {
                            var resolution = await new YouTubeResourceResolver(client).ResolveAsync(route.Intent, route.ResourceQuery, ct);
                            if (resolution.ResourceId is null)
                            {
                                answer = resolution.Clarification!;
                                break;
                            }
                            // Keep the verified target stable if the detail read or subsequent delivery fails.
                            saved = await Save(context, key, saved.Payload with { Phase = "ResourceResolved", ResolvedResourceId = resolution.ResourceId }, saved.Revision, ct);
                        }
                        var evidence = await Read(route with { ResourceId = saved.Payload.ResolvedResourceId ?? route.ResourceId }, client, ct);
                        saved = await Save(context, key, saved.Payload with { Phase = "EvidenceSaved", Evidence = evidence }, saved.Revision, ct);
                    }
                    answer = await Generate(input, context, "Answer the request using only the supplied provider evidence. " +
                        "This is a bounded read, not synchronization. Say when a page is incomplete. No mutations occurred. " +
                        "Membership page entries are not a channel total. Missing profiles still represent members; do not infer their identities. " +
                        "An empty level list means no configured levels were returned, not ineligibility. If more members are available, offer to show the next page.",
                        JsonSerializer.Serialize(new { request = input.Prompt, evidence = saved.Payload.Evidence }), ct);
                    break;
                case "Unavailable":
                    answer = "That particular channel-changing operation isn't enabled in this build. I can help prepare the content and explain the next step, but I haven't made that change.";
                    break;
                default: answer = route.Reply; break;
            }
        }
        catch (MembershipReadUnavailableException error)
        {
            // Only locally authored recovery text is exposed, never provider diagnostics.
            saved = await context.Platform.ReadOperatingStateAsync<TurnState>(key, ct) ?? saved;
            await Save(context, key, saved.Payload with { Phase = "ReadDeferred" }, saved.Revision, ct);
            return error.Message;
        }
        catch (PlatformCapabilityException error) when (YouTubeCapabilities.All.Contains(error.Capability))
        {
            if (IsMembershipRead(route.Intent) || route.Intent == "video-edit")
                saved = await context.Platform.ReadOperatingStateAsync<TurnState>(key, ct) ?? saved;
            // Provider errors are not model instructions or user-facing diagnostics. Keep the
            // target and intake resumable, and guide access recovery without escalating consent.
            await Save(context, key, saved.Payload with { Phase = "ReadDeferred" }, saved.Revision, ct);
            if (IsMembershipRead(route.Intent))
            {
                if (error.Code == PlatformCapabilityErrorCode.Denied)
                {
                    await SuggestSetup(input, context, ct);
                    return "I don't currently have membership access. The channel owner can review the optional membership permission in connection settings. Google must also enable membership API access for this channel; your YouTube Partner Manager may need to help. Consent alone doesn't establish eligibility. Nothing has been changed.";
                }
                return error.Code == PlatformCapabilityErrorCode.BudgetExceeded
                    ? "Membership reads are waiting on the current usage limit. The saved request can be retried later; I haven't changed anything."
                    : "I couldn't verify the membership information. This may be temporary, an expired page, or unavailable channel eligibility—not proof that there are no members. Ask me to retry, or start a new member list; the channel owner may need their YouTube Partner Manager's help.";
            }
            if (error.Code == PlatformCapabilityErrorCode.Denied)
            {
                await SuggestSetup(input, context, ct);
                return "I can't read that with the current channel access. Use YouTube connection settings to check the connected channel and enabled features, or confirm the shared link is for this channel. I haven't changed anything.";
            }
            return error.Code switch
            {
                PlatformCapabilityErrorCode.NotFound => "I couldn't find that item in the connected channel. Please confirm its title or share its YouTube link. Nothing has been changed.",
                PlatformCapabilityErrorCode.BudgetExceeded => "YouTube access is at its current usage limit. I haven't changed anything or started a retry loop. The saved request can be retried when access is available again.",
                _ => "I couldn't confirm that information from YouTube just now. The request is saved so it can be retried; I haven't changed anything."
            };
        }
        await Save(context, key, saved.Payload with { Response = answer, Phase = saved.Payload.TodoId is null ? "Answered" : "Queued" }, saved.Revision, ct);
        return answer;
    }

    public override async Task<PersonalTodoResult> HandlePersonalTodoAsync(PersonalTodoItem item, AgentRuntimeContext context, CancellationToken ct)
    {
        if (item.CorrelationId == MonitoringKey) return await ProcessMonitoring(item, context, ct);
        if (item.CorrelationId == ReportingKey) return await ProcessReporting(item, context, ct);
        if (item.CorrelationId is not { } key || !key.StartsWith("youtube.turn:", StringComparison.Ordinal))
            return PersonalTodoResult.Blocked("This work is not a recognized YouTube request.");
        if (item.OwnerOrganizationUserId.ToString("D") != context.Identity?.EmployeeId ||
            item.CreatedByOrganizationUserId != item.OwnerOrganizationUserId)
            return PersonalTodoResult.Blocked("This work was not created by this YouTube Manager.");
        var state = await context.Platform.ReadOperatingStateAsync<TurnState>(key, ct);
        if (state?.Payload.Intake is not { Kind: "Deliverable" or "Action" } route || state.Payload.TodoId is { } todoId && todoId != item.Id ||
            item.SourceMessageId != state.Payload.Input.MessageId || item.SourceConversationId?.ToString("D") != state.Payload.Input.ConversationId)
            return PersonalTodoResult.Blocked("The requested work does not match its saved conversation.");
        if (route is { Kind: "Action", Intent: "video-upload" }) return await ProcessUpload(item, state, context, ct);
        if (route is { Kind: "Action", Intent: "video-edit" }) return await ProcessVideoEdit(item, state, context, ct);
        if (route.Kind == "Action") return await ProcessReply(item, state, context, ct);
        if (state.Payload.Phase == "Delivered") return PersonalTodoResult.Completed("The requested draft is already delivered.");
        try
        {
            // Recover a crash after idempotent queue insertion but before its returned ID was saved.
            if (state.Payload.TodoId is null)
                state = await Save(context, key, state.Payload with { TodoId = item.Id, Phase = "Queued" }, state.Revision, ct);
            var preferences = await context.Platform.ReadOperatingStateAsync<Preferences>("youtube.preferences", ct);
            if (preferences?.Payload.Values.GetValueOrDefault("paused") == "true")
            {
                if (!state.Payload.Paused)
                    await Save(context, key, state.Payload with { Paused = true }, state.Revision, ct);
                return PersonalTodoResult.WaitingUntil(DateTimeOffset.UtcNow.AddMinutes(15), "YouTube work is paused. Check the saved preference before continuing.");
            }
            if (state.Payload.Paused)
                state = await Save(context, key, state.Payload with { Paused = false }, state.Revision, ct);
            if (route.Intent == "publication-plan" && state.Payload.PublicationMedia is { Count: > 0 } publicationMedia)
            {
                var chat = await context.Platform.Communication.ReadChatAsync(Guid.Parse(state.Payload.Input.ConversationId), ct);
                var original = chat.Messages.SingleOrDefault(x => x.Id == state.Payload.Input.MessageId &&
                    x.ChatId.ToString("D") == state.Payload.Input.ConversationId && x.Content == state.Payload.Input.Prompt)
                    ?? throw new InvalidOperationException("The original video message is no longer available.");
                PublicationMedia.RequireUnchanged(publicationMedia, original);
            }
            if (route.Intent == "reply-drafts")
                return await ProcessEngagementDraft(item, state, preferences?.Payload, context, ct);
            if (state.Payload.Phase is not ("Generated" or "Delivered"))
            {
                if (state.Payload.Evidence is null && route.Intent != "publication-plan")
                {
                    var evidence = await Read(route with { Intent = route.Intent == "reply-drafts" ? "comments" : route.Intent }, new YouTubeClient(context.Platform), ct);
                    state = await Save(context, key, state.Payload with { Phase = "EvidenceSaved", Evidence = evidence }, state.Revision, ct);
                }
                var draft = await Generate(state.Payload.Input, context,
                    "Produce the requested report, reply drafts or publication plan. Label it clearly as a draft. " +
                    "Use official supplied evidence only; explain sample/page limitations. Do not publish, invent numbers, or claim approvals. " +
                    "For reply drafts quote the comment ID as a review reference and keep each response tied to that comment.",
                    JsonSerializer.Serialize(new { request = state.Payload.Input.Prompt, evidence = state.Payload.Evidence,
                        attachedVideos = state.Payload.PublicationMedia?.Select(x => new { x.FileName, x.ContentType, x.SizeBytes }),
                        attachmentGuidance = "These are file descriptors, not viewed video content. Do not invent what a video shows or expose internal identifiers. Refer to the file by name. A publication plan does not upload or authorize anything.",
                        preferences = preferences?.Payload }), ct);
                state = await Save(context, key, state.Payload with { Phase = "Generated", Response = draft }, state.Revision, ct);
            }
            await context.Platform.Communication.SendMessageAsync(Guid.Parse(state.Payload.Input.ConversationId),
                state.Payload.Response!, $"youtube-result:{state.Payload.Input.MessageId:N}", ct);
            await Save(context, key, state.Payload with { Phase = "Delivered" }, state.Revision, ct);
            return PersonalTodoResult.Completed("The requested draft was saved and delivered for review.");
        }
        catch (PlatformCapabilityException)
        { return PersonalTodoResult.Blocked("Access or persistence is unavailable. Restore it and resume this work; nothing was published."); }
        catch (InvalidOperationException) when (route.Intent == "publication-plan")
        { return PersonalTodoResult.Blocked("The original video attachment or saved plan could not be verified. Review the source request before continuing; nothing was published."); }
    }

    private async Task<string> Generate(AssistantRequest input, AgentRuntimeContext context, string instruction, string data, CancellationToken ct)
    {
        var provider = input.ProviderProfileId;
        if (provider == Guid.Empty) throw new InvalidOperationException("A reasoning provider must be configured.");
        var selection = new AgentLlmSelection(provider, Settings.GetGuid("llmProviderId") == provider ? Settings.GetString("llmModel") : null,
            new(Guid.Parse(input.ConversationId), input.ChatTurnId == Guid.Empty ? null : input.ChatTurnId, "youtube-manager"));
        using var client = modelFactory is null ? context.CreateChatClient(selection) : await modelFactory.CreateChatClientAsync(selection, ct);
        var result = await context.Platform.Calendar.GetResponseAsync(client, [new ChatMessage(ChatRole.System, YouTubeManagerProfile.Instructions + "\n" + instruction),
            new ChatMessage(ChatRole.User, data)], await context.Platform.Calendar.WithToolsAsync(new ChatOptions
            { MaxOutputTokens = ResolveOutputTokens(Settings), Temperature = 0.2f, Reasoning = new() { Output = ReasoningOutput.None, Effort = ReasoningEffort.Low } }, ct), ct);
        if (string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 16000 || result.FinishReason == ChatFinishReason.Length)
            throw new InvalidOperationException("The reasoning result was empty or incomplete.");
        return result.Text.Trim();
    }

    private static async Task<int> ResumePaused(AgentRuntimeContext context, Guid messageId, CancellationToken ct)
    {
        var directory = await context.Platform.PersonalTodo.ListAsync(ct);
        var count = 0;
        foreach (var item in directory.Boards.Where(x => x.OwnerOrganizationUserId.ToString("D") == context.Identity?.EmployeeId)
            .SelectMany(x => x.Items).Where(x => x.Status is "Blocked" or "Running" && x.ArchivedAt is null &&
                x.OwnerOrganizationUserId.ToString("D") == context.Identity?.EmployeeId && x.CreatedByOrganizationUserId == x.OwnerOrganizationUserId).Take(25))
        {
            ct.ThrowIfCancellationRequested();
            if (item.CorrelationId is not { } key || !key.StartsWith("youtube.turn:", StringComparison.Ordinal)) continue;
            var saved = await context.Platform.ReadOperatingStateAsync<TurnState>(key, ct);
            if (saved?.Payload is not { Paused: true, Intake.Kind: "Deliverable" or "Action" } state || state.TodoId != item.Id ||
                state.Input.MessageId != item.SourceMessageId || state.Input.ConversationId != item.SourceConversationId?.ToString("D")) continue;
            // Queue first: a crash cannot clear the only durable indication that this card needs a wake.
            await context.Platform.PersonalTodo.RequeueAsync(new(item.Id, item.Revision, $"youtube-resume:{messageId:N}:{item.Id:N}"), ct);
            await Save(context, key, state with { Paused = false }, saved.Revision, ct);
            count++;
        }
        return count;
    }

    private static async Task SuggestSetup(AssistantRequest input, AgentRuntimeContext context, CancellationToken ct)
    {
        if (input.ChatTurnId == Guid.Empty) return;
        await context.Platform.SuggestUserActionAsync(new(null, input.ChatTurnId, UserActionWorkflows.PluginSetupOpen,
            "YouTube connection settings", "Connect, resume setup, or review permissions securely.", SerializePayload(new { }),
            $"youtube-settings:{input.MessageId:N}"), ct);
    }

    public static Intake ParseIntake(string text)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Object || document.RootElement.EnumerateObject().GroupBy(x => x.Name).Any(x => x.Count() != 1))
            throw new InvalidOperationException("Ambiguous routing response.");
        var route = document.RootElement.Deserialize<Intake>(Json) ?? throw new InvalidOperationException("Missing routing result.");
        if (route.Kind is not ("Information" or "Read" or "Preference" or "Deliverable" or "Action" or "Upload" or "UploadFollowup" or "VideoEditFollowup" or "Setup" or "Approval" or "Clarify" or "Unavailable") ||
            string.IsNullOrWhiteSpace(route.Reply) || route.Reply.Length > 4000 || route.ResourceId?.Length > 128 || route.ResourceQuery?.Length > 256)
            throw new InvalidOperationException("Invalid routing result.");
        if (route.Kind == "Read" && !ReadIntents.Contains(route.Intent) || route.Kind == "Deliverable" && route.Intent is not ("analytics" or "reply-drafts" or "publication-plan"))
            throw new InvalidOperationException("Unsupported work intent.");
        if (route.Kind == "Preference") ValidatePreference(route.Preference ?? "", route.Value ?? "");
        if (IsMembershipRead(route.Intent) && (route.Kind != "Read" || route.ResourceId is not null || route.ResourceQuery is not null || route.StartDate is not null || route.EndDate is not null))
            throw new InvalidOperationException("Membership reads use only the bound account and retained paging state.");
        if (route.Kind is "Upload" or "UploadFollowup" && (route.Intent != "video-upload" || route.ResourceId is not null || route.ResourceQuery is not null || route.StartDate is not null || route.EndDate is not null))
            throw new InvalidOperationException("Upload intake cannot supply a guessed resource or scheduled execution date.");
        if (route.Kind == "VideoEditFollowup" && (route.Intent != "video-edit" || route.ResourceId is not null || route.ResourceQuery is not null ||
            route.StartDate is not null || route.EndDate is not null || route.Preference is not null || route.Value is not null))
            throw new InvalidOperationException("An edit follow-up resumes only its exact saved obligation.");
        if (route.Kind == "Action" && route.Intent != "video-edit" && (route.Intent != "comment-reply" || string.IsNullOrWhiteSpace(route.ResourceId) ||
            route.ResourceQuery is not null || route.ResourceId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '_' and not '-')))
            throw new InvalidOperationException("An explicit top-level comment reference is required for a reply.");
        if (route.Intent == "analytics") _ = DateRange(route);
        if (route.Kind == "Action" && route.Intent == "video-edit" && (route.ResourceId is null && string.IsNullOrWhiteSpace(route.ResourceQuery) ||
            route.ResourceId is { } videoId && (videoId.Length != 11 || videoId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '_' and not '-')) ||
            route.StartDate is not null || route.EndDate is not null))
            throw new InvalidOperationException("An edit requires a supplied video link or title, not a schedule.");
        if (route.ResourceQuery is not null && (!(route.Kind == "Action" && route.Intent == "video-edit") &&
            (route.Kind != "Read" || route.Intent is not ("video" or "captions" or "playlist-items")) ||
            string.IsNullOrWhiteSpace(route.ResourceQuery) || route.ResourceId is not null))
            throw new InvalidOperationException("Title lookup requires one supported read target.");
        if (route.Kind == "Read" && route.Intent is "video" or "playlist-items" or "replies" or "captions" or "broadcast" or "stream" &&
            route.ResourceQuery is null &&
            (string.IsNullOrWhiteSpace(route.ResourceId) || route.ResourceId.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not '_' and not '-')))
            throw new InvalidOperationException("A provider resource reference is required.");
        return route;
    }

    private static void ValidateResourceSource(Intake route, string prompt)
    {
        // Model output cannot introduce a different resource, even for a read. Resolved IDs come
        // only from authenticated channel listings and are saved separately from model intake.
        if (route.Kind is not ("Read" or "Action")) return;
        if (route.ResourceQuery is { } query && !prompt.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            route.ResourceId is { } id && !System.Text.RegularExpressions.Regex.IsMatch(prompt,
                $@"(?<![A-Za-z0-9_-]){System.Text.RegularExpressions.Regex.Escape(id)}(?![A-Za-z0-9_-])"))
            throw new InvalidOperationException("The requested resource reference is not present in the source message.");
    }

    private static void ValidatePreference(string key, string value)
    {
        if (value.Length is < 1 or > 2000 || key is not ("objectives" or "brandVoice" or "privacyDefault" or "timezone" or "commentCadenceMinutes" or "reportCadence" or "paused"))
            throw new InvalidOperationException("Unsupported preference.");
        if (key == "privacyDefault" && value is not ("private" or "unlisted" or "public") ||
            key == "paused" && value is not ("true" or "false") || key == "reportCadence" && value is not ("weekly" or "monthly") ||
            key == "commentCadenceMinutes" && (!int.TryParse(value, out var cadence) || cadence is < 15 or > 1440))
            throw new InvalidOperationException("Invalid preference value.");
        if (key == "timezone" && !TimeZoneInfo.TryFindSystemTimeZoneById(value, out _)) throw new InvalidOperationException("Unknown timezone.");
    }

    private static AnalyticsSummaryRequest DateRange(Intake route)
    {
        if (!DateOnly.TryParseExact(route.StartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !DateOnly.TryParseExact(route.EndDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end) || end < start || end.DayNumber - start.DayNumber > 366)
            throw new InvalidOperationException("An explicit ordered report date range is required.");
        return new(start, end);
    }

    private static async Task<JsonElement> Read(Intake route, YouTubeClient youtube, CancellationToken ct) => route.Intent switch
    {
        "channel" => SerializePayload(await youtube.ReadChannelAsync(ct)),
        "video" => SerializePayload(await youtube.ReadVideoAsync(new(route.ResourceId!), ct)),
        "playlists" => SerializePayload(await youtube.ListPlaylistsAsync(new(), ct)),
        "playlist-items" => SerializePayload(await youtube.ListPlaylistItemsAsync(new(route.ResourceId!), ct)),
        "comments" => SerializePayload(await youtube.ListCommentThreadsAsync(new(), ct)),
        "replies" => SerializePayload(await youtube.ListCommentRepliesAsync(new(route.ResourceId!), ct)),
        "captions" => SerializePayload(await youtube.ListCaptionsAsync(new(route.ResourceId!), ct)),
        "broadcast" => SerializePayload(await youtube.ReadLiveBroadcastAsync(new(route.ResourceId!), ct)),
        "stream" => SerializePayload(await youtube.ReadLiveStreamAsync(new(route.ResourceId!), ct)),
        "analytics" => SerializePayload(await youtube.ReadAnalyticsAsync(DateRange(route), ct)),
        _ => throw new InvalidOperationException("Unsupported read.")
    };

    private static Task<AgentOperatingState<TurnState>> Save(AgentRuntimeContext context, string key, TurnState state, long? revision, CancellationToken ct) =>
        Write(context, key, state, revision, $"youtube-state:{Hash(key + JsonSerializer.Serialize(state, Json))}", state.Phase, ct);
    private static Task<AgentOperatingState<T>> Write<T>(AgentRuntimeContext context, string key, T payload,
        long? revision, string idempotency, string status, CancellationToken ct) => context.Platform.WriteOperatingStateAsync(
            new WriteAgentOperatingStateRequest<T>(key, "youtube.manager.state.v1", 1, status,
                new Dictionary<string, string>(), [], Hash(JsonSerializer.Serialize(payload, Json)), [], Guid.Empty,
                payload, revision, idempotency), ct);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
