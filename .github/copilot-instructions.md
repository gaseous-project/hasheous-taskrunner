# Hasheous Task Runner Copilot Instructions

This file records architecture and security decisions from the March 12, 2026 runner review so future Copilot sessions can continue from agreed outcomes.

## Review Context

- Product type: Publicly distributed task runner executable.
- Operational model: Can run interactive, Windows service, or containerized unattended.
- Trust model: Runner is remotely orchestrated by host tasks.

## Agreed Outcomes From Review

1. Auto-update integrity checks must be fail-closed by default.
2. A user-configurable bypass switch may allow updates without integrity checks when explicitly enabled.
3. Disk usage gating exists via capability checks and host-side task assignment.
4. Thread-safety improvements are required in task scheduling and shared state.
5. AI task validation should be tightened without breaking template-driven host task generation.
6. Re-registration failure behavior should be consistent across modes:
	 - Unattended: finish in-flight tasks, stop taking new tasks until registration succeeds, keep process alive.
	 - Interactive: finish in-flight tasks, stop taking new tasks until registration succeeds, keep process alive.
7. Debug-only TLS relaxation risk is accepted for now.
8. Plaintext local secret persistence risk is accepted for now.
9. Windows service argument handling bug and related hardening are deferred.

## Implementation Guidance For Future Changes

### 1) Updater Integrity Policy

- Default behavior: block update if checksum cannot be fetched or verified.
- Add configuration key: `AllowInsecureUpdate` (default `false`).
- If `AllowInsecureUpdate=true`, permit update with explicit warning log.
- Keep current rollback behavior.

Suggested config precedence should remain:

- Defaults
- `~/.hasheous-taskrunner/config.json`
- Environment variables
- CLI args

### 2) Task Runner Concurrency Safety

- Replace mutable shared structures with synchronization:
	- Use `ConcurrentDictionary<long, TaskExecutor>` for active executors.
	- Replace `IsRunningTask` bool gating with `SemaphoreSlim` (single-cycle guard).
- Ensure UI reads from snapshots to avoid concurrent enumeration exceptions.
- Do not spawn overlapping fetch cycles.

### 3) AI Task Validation

- Keep compatibility with template-generated prompts.
- Validate required keys before execution:
	- Require `sources` key for tasks that expect RAG data.
	- Require at least one model key and at least one prompt key (existing behavior).
- Add bounded checks to reduce host/runner blast radius:
	- max source count
	- max source item size
	- max total payload size
- Fail verification early with explicit details instead of failing during execute.

### 4) Registration Failure State Machine

Use an explicit registration health state (for example: `Healthy`, `Degraded`, `BlockingNewTasks`).

- Healthy: normal operation.
- Registration/re-registration failure:
	- Set state to block new task fetch/acceptance.
	- Allow active tasks to complete and submit while API key remains valid.
- Recovery:
	- Background retry with backoff while blocked.
	- On success, transition to healthy and resume intake.

Mode-specific behavior:

- Interactive mode: remain alive and continue retrying registration; provide clear console guidance while intake is blocked.
- Unattended mode: remain alive and continue retrying registration.

### 5) HTTP Client Header Hygiene

- Continue reusing `HttpClient` (do not create per request).
- Avoid appending duplicate headers on each request.
- Consider separating clients by auth phase:
	- registration client (bootstrap headers)
	- worker client (runtime API key header)
- If refactoring substantially, a typed `IHttpClientFactory` setup is acceptable.

### 6) HTTP Boundary Isolation (Host vs External Services)

- Never send host security headers (`X-API-Key`, `X-TaskWorker-API-Key`, or future auth headers) to non-host endpoints.
- Keep host communication and external integrations (for example Ollama) on separate HTTP client paths.
- Prefer request-scoped auth headers for host requests instead of persistent global defaults.
- If adding more job types that call external services, each integration should have its own client abstraction with explicit allowed headers.
- Add an allowlist guard for sensitive headers so they are only attached when request URI matches configured host origin.
- Include tests that verify host auth headers are absent from Ollama and other external service requests.

## Deferred Items

- OS-native secure secret storage migration.
- Windows service command parsing and service install hardening.

## Implementation Status (Updated 2026-03-12)

The following review outcomes have now been implemented in the codebase:

1. Updater integrity policy is fail-closed by default with `AllowInsecureUpdate` override.
2. Task runner concurrency hardening uses `ConcurrentDictionary` and single-cycle `SemaphoreSlim` guard.
3. AI task verification includes required key checks and bounded payload/source limits.
4. Registration health state machine (`Healthy`, `Degraded`, `BlockingNewTasks`) gates intake and supports recovery loops.
5. Host API client header handling avoids duplicate sensitive defaults and injects auth per request.
6. HTTP boundary isolation prevents host auth headers from being used against non-host absolute URLs.

### Required Follow-up Changes

Implemented test coverage now exists for:
	- registration forced host re-registration vs local short-circuit behavior
	- host-boundary isolation for non-host absolute URLs
	- request-scoped host auth header behavior (no sensitive default-header persistence)
	- update integrity block/allow branches
	- task loop overlap prevention and snapshot safety

Manual degraded-mode verification checklist has been added to `README.md`.

## Notes For Future Copilot Sessions

- Treat this file as the source of truth for the above decisions.
- When implementing any of these items, include tests or at minimum explicit manual verification notes for:
	- update verification paths
	- registration fail/recovery behavior
	- concurrency safety in task loop and TUI
