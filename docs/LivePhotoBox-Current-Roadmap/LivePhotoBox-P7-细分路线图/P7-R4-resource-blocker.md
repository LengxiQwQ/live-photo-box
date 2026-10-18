# P7-R4 resource blocker and recovery boundary

Status: **R4 NOT COMPLETE / FAIL_RESOURCE_RETENTION**. This record does not change
Roadmap acceptance, backend selection, capability, Contract/ABI or phase order.
The current user authorized LOCAL_OWNER execution through R4, without ChatGPT.
No R5/P8 work or Git publication is authorized.

## Observed campaign

| Evidence | Observed result | Acceptance meaning |
|---|---|---|
| `soak-v6` | 3 warmups + 76 measured epochs, 1811.0618183 measured seconds | Workload, source identity, cleanup and post-load cancellation assertions passed; resources did not pass |
| `soak-v11` | 3 warmups + 83 measured epochs, 1813.4899148 measured seconds | Same distinction; activation Shutdown did not remove resource growth |
| `targeted-v15` | 223/223 passed, zero skipped; exact historical driver-v11 Core/Native | Valid historical bounded regression result, not acceptance of later source or a resource PASS |
| `stage-isolation-v16`, `mf-with-audio-v17`, `mf-no-audio-v17` | MF SDR calls retain one additional Semaphore per call | Reproduces outside the full mixed workload; audio is not necessary |
| `mf-create-release-v23` | 24 URL-reader create/release probes return to baseline | Constructor-only ownership is insufficient to reproduce |
| `mf-object-identity-v25` | Reader-construction handle closes; a different first-decode handle survives | Corrects the earlier constructor attribution; counts alone did not identify the retained object |
| `mf-decode-only-v27`, `mf-decode-no-xvp-v28` | HEVC first-frame decode retains one per iteration, without encoder or XVP | Decoder/MF lifecycle path is sufficient; these intentionally stop with no product output |
| `mf-decoder-stack-v29` | Owned-child diagnostic IAT hook records CreateSemaphore calls inside `HEVCDECODER_STORE.dll` | Creation-stack evidence, not a modification to any installed package or proof of a unique private DLL defect |
| `mf-decoder-shutdown-v31` | END_STREAMING, FLUSH and native-media-type reset succeed; IMFShutdown QI returns E_NOINTERFACE; growth persists | No evidence that another ordinary cleanup call fixes the problem |
| `mf-decoder-final-release-v32` | 21 decoder Release observations return 0; 3 return 4; later DllCanUnloadNow returns S_OK | Exact diagnostic observations; Release return values do not prove all asynchronous destruction completed |
| `mf-unused-module-v33` | Two diagnostic CoFreeUnusedLibrariesEx(0) calls leave HEVC module loaded and Semaphore count unchanged | Ineffective, and deliberately excluded from production |
| `mf-decoder-quiet-v34` | 61 natural observations over 65.217 seconds: Semaphore stays 28; handles 449→384; threads 19→12 | The 24 extra HEVC Semaphores survive this quiet window after the public conversion returned |
| `mf-h264-product-control-v34` | Same diagnostic runtime: 24 H264 decode probes plateau at 13; after runtime, 61 observations over 65.339 seconds stay at 12 | No per-iteration growth in this control; input is an existing product output, not an original RealSample |

The installed dependency identity is Microsoft.HEVCVideoExtension 2.5.33.0,
HEVCDECODER_STORE.dll file version 10.0.29645.1000. Its exact path, byte length and
SHA256 are in the archived `hevc-extension-identity.json`. No installed codec was
updated, patched, replaced or uninstalled.

The supported conclusion is repeated resource retention in the installed
Microsoft HEVC decoder / Media Foundation decode lifecycle path. No outstanding
explicit application COM reference has been identified that explains the growth.
This is not proof of a unique internal vendor defect, nor permission to waive the
resource criterion. Two offline ffmpeg-generated H264 controls were rejected by
strict upstream profile validation and are preserved as rejected diagnostics;
they are not counted as execution of the MF control.

## Recovery and decisions

All temporary Native trace, decoder attributes, IAT hooks, decode-only early exits,
extra flush/shutdown probes and global COM-unload calls were removed. The source
and canonical Native artifacts were restored byte-for-byte to the verified
`native-v3-repair3.json` identity; diagnostic runtimes and sources remain separately
archived. Normal R4 repairs to paused cancellation, allocation-failure file handles,
activation shutdown and media-sink lifetime remain candidates, not R4 acceptance.

The recovery run is a create-only **FAIL_RESOURCE_RETENTION_RECOVERY_SNAPSHOT**,
not a success run. Its evidence index binds the historical runtimes, complete raw
results, source/probe identities, source restoration and this boundary. Successful
workload assertions cannot authorize the success-archive tool without a separate
explicit passing resource assessment.

An official-component update was initially proposed. The current user clarified
that the goal is general-user project correctness, not adapting the product to
one development computer; no system update was authorized or applied. A read-only
Store query found no matching OEM package in that source, which is not proof
that all update channels have no update. Package identity/hashes are not an
installable rollback package.

Subsequent standalone SDK diagnostics remove all project code: 24 complete HEVC
decodes retain Semaphore counts 3..26 and 60 quiet observations stay at 26; the
same executable's H264 control stays at 10. A RoInitialize/RoUninitialize variant
also retains 3..26. Fresh read-only review confirms no explicit ownership defect
in the base minimal program; the project code is not necessary to reproduce this
observed phenomenon. This does not certify every project path or every Windows
environment. The restored actual product candidate's new `targeted-v35` aggregate
passes 223/223 with zero skipped and no source drift; resource acceptance remains
separate and failed.

The next design decision concerns the general approach documented in
`P7-R4-MF-isolation-proposal.md`. Eight development harness processes each execute
one actual public product conversion, retire cleanly and leave no TEMP files;
parent handles are 144 initially, then 146 after every child and at the end. This
is limited feasibility evidence, not a production worker implementation or
proof of cross-process authority/cancellation/publication. Adding a product-owned
worker changes execution and packaging boundaries, so it requires an explicit
decision after reviewing the proposal. Do not silently select another decoder,
relax resource acceptance, or add machine/version-specific product branches.

This decision follows Bootstrap / AGENTS 0.2: changes to production dependencies,
public behavior or security/execution boundaries require the current user's
decision. Ordinary ownership repairs and diagnostics have already been performed;
the request is not approval for a routine engineering retry.

After a real repair, first prove the repeated MF isolation no longer grows, then
bind a fresh final candidate and complete the bounded aggregate, required large
and real-sample checks, full mixed soak, independent media/resource validation and
fresh read-only closure review in a new formal run. R4 remains open until then.
