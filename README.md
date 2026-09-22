# YouTube Manager

Conversational C-Sweet specialist for a company's YouTube channel. The agent uses
an explicitly bound YouTube connector; it never receives Google credentials or
raw authenticated network access.

Version 0.1.0 targets .NET 10, protocol 2.3, SDK 3.40.0 and the explicitly bound
`com.csweet.connector.youtube` dependency (`>=0.1.0 <0.2.0`).

The integration package and repository are `CSweet.Plugins.Platform.YouTube`. They own
provider setup declarations, scope sets, typed API abilities and channel checks. The agent
owns conversation and business judgment; C-Sweet owns generic authorization and execution.
Installing the dependency never grants account access or permission to mutate content.

Existing-video metadata edits now have a conversational path. Supply a video share link or title and
the requested title, description, tags, category or default-language change. Ambiguous title matches
ask for a selection without creating work. The agent saves the owned snapshot, model draft and complete
replacement snippet before requesting an exact approval. Untouched fields are preserved; privacy,
audience, disclosures and scheduling are excluded. Category changes use the named assignable catalog
for an explicitly supplied country, not model-invented IDs. Draft reasoning does not receive channel IDs
or resource ETags. User and provider text cannot authorize a mutation.

Durable edit work uses stable keys, channel/video reservations and exact decision-event correlations.
Lost checkpoints reuse the saved request; authoritative revision feedback prepares a new snapshot and
draft requiring another approval, with the prior decision checked again before submission. Paused
preparation waits; pending actions can be cancelled, but executing actions are not described as cancelled.
Saved results precede delivery. Conflicts require a fresh requested review; uncertain outcomes stay
blocked without another send. A later human edit is distinguished from an unconfirmed initial edit.
Clarification answers and requests for fresh review after a confirmed version conflict now resume the
same personal work without repeating the video link. Each reply binds the original requester and
conversation, the routed edit revision and a retained source-message hash. Stale, cross-requester,
cross-conversation and changed-source replies cannot replace a newer draft. The fresh snapshot and
reply reference are saved before waking the existing task; retries do not append them or requeue ready
work again. Conflicts remain blocked rather than completed so they can resume. Changed conflict or
revision decisions are rechecked before a new action, and uncertain outcomes cannot be resumed as
confirmed conflicts. Approval wording and status questions are not clarification replies.

Up to ten clarification/revision steps are retained. Replies require their original authorized messages
to remain available; larger histories need a fresh bounded review. Ambiguous-title selection follow-ups,
handoff to another requester and oversized-state sharding remain unfinished. These snapshots, drafts
and prior revisions also need the outstanding provenance-aware retention/purge work.
Deterministic tests are not browser or real-Google edit acceptance.

SDK 3.40.0 supplies source-bound attachment metadata for connector media requests. The host validates
live chat access and freezes the original attachment with the approved media plan. Conversational
immediate-upload intake is now connected to the durable approval worker. The host's generic large-video
attachment UX still needs browser acceptance and transfer recovery work. Users supply attached videos,
not internal asset IDs. This is deterministic integration coverage, not real-Google publishing acceptance.

An internal durable worker now accepts fully prepared, source-bound upload work. It preserves the
exact request and retry key through approval waits and lost checkpoints, verifies the connected
channel before preparation, and reserves the channel/file digest against duplicate work. Category
choices include a country, display language and provider-returned name. The worker resolves that name
through the plugin's current assignable catalog and requires its ID to match the saved metadata before
requesting approval. Category names and tags appear in the preview, without asking users for numeric
category IDs. The conversation asks for missing metadata and presents current category names. The worker tracks
the existing upload through processing and verified visibility, and blocks uncertain outcomes instead
of uploading a replacement. Pause cancels only unstarted uploads. Decision events are correlated wake
hints; results are always reread. Saved notices survive delivery failures without duplicate messages.
The conversation retains one requester-scoped upload obligation across answers, later video attachments,
restart and replay. Audience, disclosure, notification, privacy and immediate-upload choices are never
defaulted. Plain-text approval cannot authorize the action. Missing access offers native connection
settings; temporary failures preserve the details without asking for consent again. Conversation-to-worker
tests verify exact approval requests and durable, deduplicated completion notices using fake responses.
Upload revision decisions now produce saved metadata changes and a fresh exact approval for the same
video/channel. The old request is retained; preview/result keys are revision-specific. A candidate is
saved before subsequent provider reads, and the old decision is rechecked before resubmission.
Unchanged drafts, file substitutions and unsupported schedules ask for clarification without another
upload. Requester-scoped clarifications bind to the exact review, so a replay cannot change a newer
review. Preparation pauses with queued work. At most twenty revisions are prepared before manual
review is required. Scheduling, explicit intentional repeat uploads and in-flight controls remain unfinished.
The manifest requests the upload grant, but that declaration grants nothing or triggers consent by itself.

Implemented conversational behavior:

- Verify incoming messages, classify intent through a bounded model call and answer questions.
- Offer native secure setup guidance without asking for copied credentials.
- On the exact host onboarding event, verify the channel, save the introduction and separate recurring
  monitoring/reporting duties, then acknowledge the stable source event. Replayed onboarding does not
  duplicate either task or introduction. Foreign organization/employee events and missing access cannot activate them.
- Run initial comment synchronization and successive scans using that durable duty. The default is
  15 minutes after a completed scan; multi-page scans defer one minute between pages. Manager cadence
  and pause preferences apply without timers or held model waits. Independent monitoring checkpoints
  prevent a requested draft scan from consuming notification changes. New comments and text/parent/video/
  moderation changes are tracked; likes alone do not create digest noise. A daily count digest goes to
  the protected onboarding conversation on the first completed scan of a new configured local day
  (UTC by default), and unchanged days stay quiet. These are inbox-change counts, not official analytics.
  Access loss produces one native reconnect notice, then hourly saved checks; recovery keeps the same
  channel and never restores autonomous mutation policies. Lost completion/delivery checkpoints reuse
  the same cycle, and overdue completions schedule a prompt follow-up rather than failing a past-due wait.
- Record manager-confirmed preferences without starting unrelated work.
- Pause queued work and resume the same correlated paused requests without duplicate cards.
- Read channel, video, playlist, comments, caption and live-status information through typed grants.
- Read current members and configured membership levels when independently granted and eligible.
  "Show the next page" resumes a requester-and-conversation-scoped cursor without asking for technical
  identifiers. One request reads at most 25 members; it is not a channel total or guaranteed snapshot.
  Unavailable profiles remain members, never guessed identities. Evidence is saved before advancing
  the cursor; failed cursor writes recover without another provider read, and stale requests cannot
  rewind newer progress. Account changes and repeating pages require a fresh list with plain-language
  guidance. Scope denial offers native optional consent and explains separate Google eligibility;
  temporary failures and usage limits retain the request without presenting a false empty result.
  Level names do not imply prices, revenue or permission to change benefits. Full membership updates
  and synchronization remain outside this completed bounded-read increment.
- Resolve video/caption and playlist-content questions by title or title fragment in the connected
  channel. Ambiguous titles produce share-link choices, not guessed IDs. Lookups are capped at ten
  pages; incomplete or repeating scans ask for a share link instead of choosing an uncertain match.
  A verified target is saved before reading details, so retrying cannot silently select another item.
- Explain denied, missing, temporarily unavailable and usage-limited reads in plain language.
  Denied reads offer the native connection-settings action; provider diagnostics stay private.
  Saved reads remain resumable without automatic retry loops or phantom tasks.
- Prepare official analytics reports, comment-reply drafts and publication plans as durable work.
- Publication plans capture verified videos attached to the source message before creating work.
  Saved attachment, message, asset, author and checksum references are rechecked on retry; changed
  sources block instead of substituting a different file. The model sees only file names/types/sizes,
  never internal media IDs, and must not infer video contents from descriptors. The typed source selector
  rejects ambiguous file names. Upload requests separately collect missing metadata and use the exact
  approval worker described above; a publication-plan request never implicitly starts an upload.
- Requested reply drafts now traverse published comment-thread pages and reply pages through durable
  one-page work steps. A channel/comment-keyed inbox deduplicates overlap; frozen page receipts recover
  checkpoint failures without mixing changing responses. Full normalized comment text is sharded into
  small records without splitting Unicode pairs. Every step rechecks the connected channel; cycles,
  malformed ownership and a 10,000-page safety ceiling require review rather than false completion.
  Usage limits or temporary unavailability defer for one hour; authorization failures block for recovery.
  After traversal, every unique returned comment and reply is reviewed from its full saved text in
  resumable batches of at most two. Each structured decision is a draft, no-reply-needed, or human-review
  outcome, never permission to post. Drafts and their source bindings are saved before review messages.
  Known channel-authored comments need no reply; missing text requires review without model guessing.
  Small labeled review batches stay in the request conversation. Rendered-size checks defer an oversized
  second entry to the next batch without truncating or regenerating its draft. Lost delivery/cursor
  checkpoints reuse the saved batch and message key. Final counts must reconcile with the saved inbox.
  This is requested full-inbox review, not an autonomous engagement schedule. Draft preparation alone
  never invokes a mutation or an approval request; posting still requires a separate exact action.
- Prepare and post a reply requested through a comment share link, subject to exact host approval
  and progressive content consent. The target and draft are saved before requesting approval.
  Decision events wake the same personal work; a five-minute durable check recovers missed wakes.
  Revision feedback produces a new saved draft and a fresh approval (up to five revisions).
  Confirmed results are saved before reporting; uncertain sends undergo read-only reconciliation
  and block rather than repost. Repeated requests reuse pending work, and unresolved reply targets
  are reserved against replacement sends. Identical text matching the last confirmed reply is blocked.
  Manager pause cancels a bounded batch of pending replies; executing/uncertain changes are never
  represented as cancelled. Cancelled actions are not restored by resuming preparation.
- Save evidence and generated results before delivery; resume failed delivery without regenerating.
- Reject chat approvals as authorization and explicitly identify other unavailable public-changing actions.

The conversation uses the platform-selected reasoning profile. Scheduled reports use the standard
`llmProviderId` / `llmModel` installation configuration, automatically seeded from the host's configured
default where available. Required `maxContextWindowTokens` (default 220,000) and `maxOutputTokens`
(default 32,000) bound every model response, including reasoning. Administrators use the existing native
configuration controls if the default is missing. No custom credential/model wizard is introduced.
The business-facing title does not convey employee-management authority.

Scheduled reporting starts one week after activation and runs independently of comment scans. It
freezes a requested seven-day period of completed Pacific-time days before reading official aggregate
metrics; a monthly preference uses the previous Pacific calendar month. Requested dates and retrieval
time are displayed explicitly. Missing aggregate rows mean unavailable evidence, never zero activity.
Metrics keep their official values and units; subscriber gains/losses stay separate, and no revenue,
derived ratios or replacement metrics are calculated. A bounded model interpretation uses only those
metrics and authorized objectives/brand guidance, with no tools, internal channel IDs or provider text.
Interpretation is distinct from the deterministic metric display, not a separately verified metric.

Each cycle persists evidence, interpretation and delivery receipt before advancing. Retries preserve
the original period, do not regenerate saved narrative and use the same message identity. A weekly or
monthly next run is scheduled after delivery; overdue work completes its saved period rather than
silently relabeling it as current. Channel access is checked before evidence and again before delivery.
Pause, quota failures, missing reasoning configuration and denied access preserve the existing task;
reconnect guidance is deduplicated. Reports go to the original protected onboarding conversation, not
a new destination inferred from external content. Automatic rerouting after manager/CEO changes,
custom weekday/time scheduling and provider-derived report retention/purge remain unfinished.

Reporting dates follow [YouTube's Pacific-time dimensions](https://developers.google.com/youtube/analytics/dimensions).
The [query API](https://developers.google.com/youtube/analytics/reference/reports/query) may return data
only through the latest day with all requested metrics available. Aggregate responses do not identify
that final day, so reports never claim the requested period is completely available.

Run `dotnet test`, `dotnet run --project src/CSweet.Agent.Platform.YouTube -- --self-test`,
and `dotnet pack src/CSweet.Agent.Platform.YouTube -c Release`. Tests use `AgentTestRuntime`,
fake inference and fake broker operations; no Google account or running host is required.

Title resolution uses the authenticated channel's uploads playlist and channel-bound playlist reads,
following the [YouTube uploads listing pattern](https://developers.google.com/youtube/v3/docs/playlistItems/list).
This is bounded lookup, not full synchronization or a snapshot guarantee while a channel is changing.
Relative references such as "the latest video" and cross-turn selection by ordinal still need implementation.

Reply recovery can require a manual YouTube review: candidate matches are not proof of which request
posted them. Complete approval-context projection for other-agent approvers, richer conversational
revision/resumption and provider-derived data purge still need work. These tests are not live Google acceptance.

Comment traversal follows the provider's [thread pagination](https://developers.google.com/youtube/v3/docs/commentThreads/list)
and [reply pagination](https://developers.google.com/youtube/v3/docs/comments/list). The default thread filter
is published comments; held/spam queues are not covered. A changing channel is not a point-in-time snapshot.
Periodic full traversals overlap and update observed comment content, but deleted/hidden-comment
reconciliation, urgent semantic alerts, automatic brand-grounded background drafting, richer daily
digests routed to a changed approver, conversational selection/revision of saved batches and
provenance-aware retention/purge remain incomplete. Partial pre-freeze evidence can
remain after an interrupted page and must be included in the eventual cleanup mechanism.

Still incomplete: scheduled publishing and remaining content/live/partner mutations, richer report comparisons and routing,
automatic connector bundle installation, CEO-to-human setup handoff, full credential/retention recovery and
real Google/browser acceptance. Draft delivery is not publication. No production-readiness claim.

**Use authorized test channels only.** Native onboarding explicitly warns that automatic provider-data
cleanup is incomplete. Background scans retain provider-derived copies; this preview is not suitable
for production accounts until retention and purge are enforced and verified.


## Business calendar

Requests business-scoped calendar read, create, update, cancel, and scheduling access. Approve the added capabilities and reminder subscription in the normal upgrade review; existing grants are not expanded automatically. Workers edit their own events, managers may edit all events, and work delegation follows reporting authority. Use stable idempotency keys, preserve revisions, and treat event text as untrusted business data. Typed operations are available through `context.Platform.Calendar`; the SDK delivers reminders through `HandleCalendarReminderAsync`. Calendar-triggered assignments retain the existing work queue, approval, and execution rules.
