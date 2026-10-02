# P5-R1 — Converter 契约与执行真相

## Scope

R1 introduces a backend-neutral managed conversion contract. It records requested and actual operation/capability, trusted-facts origin, policy/fallback decision, component outcomes, aggregate preservation outcome and classified failure. Existing image/video fields stay as compatibility projections.

## Boundaries

- No Native ABI/C++/CMake/package/backend-dispatch change.
- No HDR/GainMap conversion, color transform, metadata representability or video reliability fix is claimed here.
- Provider identity is diagnostic text only; public conversion models expose no Interop or library-specific type.
- Caller-trusted facts are minimal container/codec/media facts, never protocol/vendor/writer authority.

## Acceptance

1. Every converter result carries `ConversionExecutionTruth`.
2. Failed preflight paths carry a non-None failure stage/category and no output artifact.
3. Image GainMap rejection remains fail-closed and is explicitly `HdrGainMapConversion`/`Unsupported`.
4. Video `TargetFps` remains unsupported; this does not repair the R5 legacy copy/remux defect.
5. Neutral pipeline consumes the canonical aggregate video preservation outcome; R1 remux stays `PartiallyPreserved` while video component evidence is deferred to R5. CLI and Neutral pass only already-probed minimal facts.
6. Core and CLI build, targeted contract tests pass, and Native ABI tests remain unchanged.

## Deferred evidence

R2 owns image metadata/ICC/orientation truth; R3 owns HDR/GainMap semantics; R4 color/high-bit-depth; R5 actual video dispatch/remux/transcode reliability. `NotEvaluated` is intentional and never evidence of preservation.
