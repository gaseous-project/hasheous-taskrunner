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
6. Re-registration failure behavior must differ by mode:
	 - Unattended: finish in-flight tasks, stop taking new tasks until registration succeeds, keep process alive.
	 - Interactive: finish in-flight tasks, stop taking new tasks, show clear console guidance, then exit.
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

- Interactive mode: after draining active tasks and reporting status, exit process with actionable message.
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

## Notes For Future Copilot Sessions

- Treat this file as the source of truth for the above decisions.
- When implementing any of these items, include tests or at minimum explicit manual verification notes for:
	- update verification paths
	- registration fail/recovery behavior
	- concurrency safety in task loop and TUI
