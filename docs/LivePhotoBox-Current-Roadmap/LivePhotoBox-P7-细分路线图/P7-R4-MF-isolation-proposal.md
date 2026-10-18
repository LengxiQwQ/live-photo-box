# R4 Media Foundation resource isolation proposal

Status: **PROPOSAL ONLY — no production worker or packaging change implemented**.
This is a general resource-lifetime design, not a special case for a computer,
HEVC package version, input filename or manufacturer. R4 remains open.

## Evidence and problem boundary

The standalone program in `tools/p7-r4-soak-validation/sdk-repro/` links only
Windows SDK / CRT libraries and calls no project code. Twenty-four complete HEVC
decodes (61 samples each) retain Semaphore counts 3..26; sixty post-runtime quiet
observations remain 26. The same executable's H264 control (55 samples each)
plateaus at 10. A separate RoInitialize/RoUninitialize variant produces the same
HEVC growth. Fresh read-only review finds no explicit COM or probe-handle imbalance
in that minimal program. This establishes that project code is not necessary to
reproduce this particular problem under the observed environment; it does not
certify every project path or every Windows installation.

The clean project's bounded regression aggregate is independently applicable to
the actual Core/Native candidate: `targeted-v35`, 223/223, zero skipped, no source
drift, exact Core/Native/sidecar runtime identities. Its success is separate from
the still-failing R4 resource criterion. Previous full mixed soaks and all failed
diagnostics remain preserved; no resource criterion is waived.

## Proposed general approach

Run Media Foundation decode/encode work in a product-owned worker process with a
bounded task lifetime. When the task ends, process retirement bounds any media
component's retained resources. Keep the same Native C++ media implementation,
WindowsMediaFoundation backend identity, codec selection, output requirements and
truthful diagnostics. Do not silently select another codec/backend or add an
external media CLI. The development `worker_isolation_probe.py` is only an
experiment using existing test harness child processes; it is not this production
implementation or evidence for IPC/publication correctness.

The intended boundary is the MF SDR conversion operation, not the application's
UI or the entire batch. C# retains business orchestration and publication; C++
retains the byte/media data plane. The worker and private request protocol must
be built and packaged by this project. Public Contract/C ABI changes are not
authorized by this proposal.

## Required design and validation before integration

- Specify a versioned private request/result protocol, diagnostics and process
  lifecycle. No native pointer or opaque plan handle may cross process boundaries.
- Verify the worker's expected installed path and build identity against the
  packaged manifest, including matching Native/sidecar versions. Do not resolve
  a worker from PATH or accept an untrusted executable override; mismatch fails
  closed before work starts.
- Define parent-owned IPC endpoints and access restrictions, bounded message and
  result sizes, request identity/sequence, timeout and strict structural parsing.
  Reject replayed, foreign, truncated, oversized or mismatched results before any
  mutation/publication; a result path alone is never authority.
- Preserve artifact identity, source fingerprint, role, workspace/root identity,
  ownership receipts and authorization at the actual mutation site. Current
  process-local ownership registration cannot be assumed valid in another process.
- Define a checked ownership handoff or child-owned staging protocol using the
  existing Contract. Reinspect and reconcile outputs before parent publication;
  unexpected child output never becomes success.
- Supervise owned processes with Windows Job lifetime, bounded output and
  concurrency, cancellation, exit/error classification and cleanup. Avoid orphan
  work and leave cancellation/rollback fail-closed.
- Preserve backend/encoder/fallback truth across the boundary, including refusal
  when the required system backend is unavailable.
- Verify large/batch/mixed workloads, source identity, resource counts, actual
  cancellation, injected versus real failures, neutral metadata preservation and
  applicable R2/R3 regressions on the new real production path.
- Review the new executable, build, deployment and packaging footprint. If public
  Contract/ABI or authority changes prove necessary, obtain a separate decision
  before implementation of that change.

## Impact and decision boundary

This would add a product executable and change process/cancellation/deployment
behavior. Startup and IPC have overhead; the roadmap adds no new performance SLA.
The proposal is broader than adding a missing Release and cannot be implemented
as an unnoticed local test-tool fix. The user's Bootstrap 0.2 requires a decision
for runtime/packaging dependencies and execution/security boundaries.

No installed system component needs to be changed to evaluate this approach.
Updating one development machine would not establish general-user correctness.
If this proposal is not authorized, preserve the reproduced external-path risk as
an open R4 blocker; do not call R4 PASS or silently relax the roadmap.
