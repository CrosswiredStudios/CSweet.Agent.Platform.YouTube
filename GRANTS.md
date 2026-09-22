# YouTube Manager grants

The manifest requests authority; installation and connector binding grant it separately.

- `youtube.api.video.metadata.update.v1` is requested for existing-video conversational edits.
  The plugin preserves the complete writable snippet and the host enforces its exact strong version
  condition and authenticated resource ownership. This requires protocol 2.3, progressive content
  consent, a separate consumer grant and exact action approval. Existing action request/read/cancel,
  video/channel reads, category catalog and durable-state/work grants are reused. Drafts and verified
  results are persisted before effects and delivery. No authenticated HTTP, arbitrary request body,
  privacy/scheduling mutation or chat-based approval is exposed. Revision feedback never substitutes
  for a new decision; conflicts and uncertain results never authorize a blind resend.
  Same-obligation clarification uses the existing chat-read, operating-state and personal-work grants.
  Requester/conversation/revision bindings and retained source hashes are checked before drafting;
  answering a question cannot grant consent, substitute a target, approve a plan or reset uncertainty.

- `youtube.api.member.list.v1` and `youtube.api.membership-level.list.v1` bind to the same explicitly
  selected connector. Optional membership consent, Google eligibility and each consumer's grant are
  independent. The host enforces authenticated response ownership using protocol 2.2; the agent also
  verifies the confirmed channel. Member-page progress is private to the requester and conversation,
  saved before downstream reads and advanced after evidence is durable. Tokens and internal member,
  level and channel IDs are never sent to the reasoning model. These are informational reads with no
  personal task or external effect. Missing profiles are retained; no membership price/revenue or
  mutation capability is inferred. Existing native settings guide consent and eligibility recovery.

- `youtube.api.video-category.list.v1` reads the plugin's country/language-specific category catalog.
  Prepared uploads require a saved display-name choice that still resolves to their exact category ID.
  Names and tags are shown in the review preview. No category ID is guessed or asked of the user, and
  reading the catalog does not grant publishing permission. Conversation presents current category names.

- `youtube.api.video.upload.v1` is requested for conversational intake and its prepared-upload worker. It requires an
  independently granted publishing scope, exact approval, a verified retained attachment and saved
  metadata. It never gives the agent file bytes, credentials or a resumable session URL. Incomplete
  conversational details prompt questions, never defaulted publication choices. The worker rereads
  the authoritative action and provider state, preserving uncertain results without replacement sends.
  Connector action read/cancel grants also cover this worker; cancellation only applies before execution.
  Authoritative revision feedback may generate metadata changes, never authority. Saved revision
  candidates require a fresh exact approval and retain the same media/channel. Old executing or
  uncertain requests cannot be replaced. Revision history, clarification correlations and notices use
  existing private operating state; no additional credential, network or provider capability is added.

- `com.csweet.agent.onboarded.v1` starts only this organization/employee's channel-bound monitoring and
  reporting duties after an authenticated connector read. `agent.onboarding.complete.v1` acknowledges the
  stable source event only after its introduction and both tasks are durable. Setup assistance cannot start them.
- The monitoring duty uses the existing personal queue and connector reads, with no LLM or public
  mutations. Operating state persists independent per-comment comparisons, cycle receipts, pending
  daily counts and the next due time. It never silently adopts a different channel. Intro, daily change
  summaries and reconnect notices stay in the host-designated onboarding conversation. Repeated access
  failures do not spam that conversation. Manager/CEO changes do not yet reroute this digest destination.

- `assistant.converse.v1` accepts a persisted conversation/message, verifies it through chat read,
  routes intent and returns an answer. Another agent can interact through the same conversation.
- `platform.llm.chat-stream.v1` uses the conversation's approved reasoning profile, or the standard
  installation reasoning configuration for scheduled reports. No tools are supplied to the model:
  bounded validated routing selects deterministic typed reads. Native configuration describe/update
  contracts expose `llmProviderId`, `llmModel`, `maxContextWindowTokens` and `maxOutputTokens`; they are not conversational preferences or secrets.
- The reporting duty uses existing channel/analytics reads, operating-state, personal-work and message
  grants. It freezes the date range, validates official metric columns through the plugin, saves evidence
  before reasoning, saves narrative before delivery and persists the receipt before advancing. Neither
  scope consent nor reporting grants authorize public changes. Reconnect checks preserve the original
  channel; copied report work cannot select another employee or conversation. Missing rows do not become
  zero metrics. These report/evidence/conversation copies must participate in the pending retention purge.
- `communication.chat.read.v1` verifies the source and reads bounded recent conversation context.
  Publication planning uses its retained attachment descriptors to checkpoint exact video sources,
  and rechecks them before preparing or redelivering a saved plan. These opaque references are not
  grants to media bytes or permission to upload; no additional grant is requested by this increment.
- `communication.message.send.v1` delivers idempotent setup introductions and completed drafts.
- `platform.user-action.suggest.v1` offers the host-owned setup/settings action with empty parameters.
- Operating-state read/write checkpoints accepted requests, routed intentions, provider evidence,
  preferences and generated drafts. These records are not approvals.
  Requested engagement scans also store channel/comment-keyed inbox pointers, immutable normalized
  comment versions, Unicode-safe text shards, per-scan deduplication markers and frozen page receipts.
  They use existing channel/thread/reply read grants only. Scan cursors advance after a complete page
  is durable; completed pages are not refetched after a lost cursor checkpoint. No host-authored
  engagement digest, generic action dispatcher or new permission is used by this workflow.
  Full-inbox review decisions and delivery batches also live in operating state. The model receives
  each full normalized comment as untrusted data and can return only a bounded Draft, NoReply or
  NeedsReview decision. Source-bound drafts survive retries without repeated generation. Review batches
  are delivered idempotently to the source conversation, not organization-wide document storage.
  These saved draft decisions never authorize external mutations or create managed-action approvals.
- Personal-todo add/read/requeue/defer/claim/complete/block/release own requested deliverables across runtime restarts.
  Resume requeues only self-created, source-matched work with a saved pause marker, never another
  employee's work or an unrelated access/approval block.
  Paused cards retain a 15-minute durable preference check; no provider or model calls occur while
  paused. A manager's resume turn wakes at most 25 matching cards immediately; others use that check.
  Claim and terminal transition mechanics remain SDK-private.
- Eleven `youtube.api.*.v1` read requirements bind exclusively to the declared `youtube` dependency:
  channel, video, playlists, playlist items, comment threads/replies, individual comments, captions, broadcast, stream and analytics.
  Extra consent, provider eligibility and consuming-agent grants remain independent host checks.
  Video/caption title lookup uses channel content details plus at most ten uploads-playlist pages;
  playlist-title lookup uses at most ten channel-bound playlist pages. No search/network grant was
  added. Lookup results remain untrusted data and cannot authorize a mutation. Model-supplied IDs
  or title fragments must occur in the verified current request; resolved IDs come from provider reads.

- `youtube.api.comment.reply.v1` requests one reviewed reply through the independent connector
  action request/read/cancel controls. No direct invocation can bypass the broker's approval boundary.
- `com.csweet.connector.action.changed.v1` is a wake hint only. The agent validates its saved action,
  task and source conversation, then rereads authoritative status. A hint cannot approve or complete work.
- Operating state also holds reply-target reservations, action correlations, drafts and bounded
  reconciliation references. Uncertain outcomes prohibit replacement sends. Manager pause attempts
  cancellation of at most 25 inspected active tasks; other pending replies recheck on their durable wake.

No credential, web, employee-management or raw authenticated HTTP authority is requested.
Before activation, the host grants only protected setup conversation, model text and native setup UI.
Plain-text approvals cannot authorize publication. Generic operating-state retention/purge integration
is still outstanding; this build is not ready for production YouTube data retention compliance.
