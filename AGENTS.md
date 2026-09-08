# YouTube Manager repository instructions

This repository contains one standalone C-Sweet protocol-v2 agent. Its purpose is:

> Translate YouTube business requests into verified reads, durable drafts and approved channel work.

## Invariants

- Keep `com.csweet.agent.platform.youtube` and version `0.1.0` synchronized between agent code,
  `csweet-plugin.json`, tests, and releases.
- The root manifest is the reviewed authority request. Keep `provides`, `requires`, events,
  configuration, credentials, web access, and UI contributions synchronized with implementation
  and tests.
- Request the minimum authority needed. Manifest declarations never grant access.
- Use typed callbacks and `AgentRuntimeContext.Platform`. Do not implement MCP/JSON-RPC, access
  runtime/workload/lease tokens, connect directly to databases or Docker, or handle provider
  credentials.
- Agent work is delivered at least once. Honor cancellation and use stable domain idempotency keys
  for external mutations.
- Unknown capabilities and events must fail or be ignored safely without leaking sensitive data.
- Hide implementation mechanics, never consent, approval requirements, risks or failures. Do not
  advertise incomplete capabilities or describe drafts as published work.
- Persist accepted intake, evidence and generated results before dependent effects. Do not create
  personal work for information, preferences, acknowledgements or plain-text approvals.
- Use the deterministic connector through its typed package client. Never bypass its broker.
- Existing-video edits must save the owned snapshot, exact draft and conditional request before
  requesting approval. Never substitute resource versions after approval, overwrite later changes,
  or treat matching current text as proof of an indeterminate action. New revision drafts need new
  decisions; lost checkpoints reuse their original domain keys.
- Edit follow-ups bind requester, conversation, routed revision and retained source hashes. Persist
  accepted replies before waking the same obligation. Never apply stale replies to newer drafts,
  reclassify uncertain effects through chat, or complete a conflict that still needs resumable work.
- Scheduled reports must freeze their period and official evidence before reasoning, preserve missing
  data rather than inventing zeros, and save narrative/delivery receipts before advancing. Never derive
  replacement metrics or infer complete date coverage from an aggregate response. Use the host's standard
  reasoning configuration; no custom credential wizard or provider-specific host code.
- Current unpublished version is 0.1.0, using SDK 3.38.0 and connector 0.1.0. Verify package-only
  dependencies; do not introduce sibling source references.

## Verification

Run from the repository root:

```powershell
dotnet test
dotnet run --project src/CSweet.Agent.Platform.YouTube -- --self-test
```

Any new capability, grant, event, configuration field, credential, or network rule requires a
manifest update, a README explanation, and tests.
