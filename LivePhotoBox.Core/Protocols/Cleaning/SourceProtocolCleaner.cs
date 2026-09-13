using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Protocols.Cleaning;

/// <summary>
/// Control plane service that orchestrates the 10-step state machine for Source Protocol Cleaner Reliability:
/// Preflight -> Verify P2 Artifacts -> Load Authorization -> Build Plan -> Stage Clean ->
/// Preservation Diff -> Media Validation -> Source Inspector Post-clean -> Commit -> Emit Evidence.
/// </summary>
public sealed class SourceProtocolCleaner : ISourceProtocolCleaner
{
    private readonly ISourceInspector _inspector;
    private readonly ITargetedPostCleanVerifier _postCleanVerifier;
    private readonly Func<
        SourceMediaFacts,
        IReadOnlyList<PlannedCleanupAction>,
        IReadOnlyList<PlannedArtifactTarget>?,
        string,
        string?,
        string?,
        PlannedArtifactTarget?,
        string,
        string?,
        CancellationToken,
        Task<IReadOnlyList<RemovedProtocolFact>>>? _cleanInvoker;

    /// <summary>
    /// Production clean invoker (P3): takes the claimed Native cleanup-plan
    /// attempt and input/output paths; Native derives all facts, actions and
    /// targets from the plan record itself.  Null when a fake clean invoker
    /// is injected (tests), in which case <see cref="_cleanInvoker"/> is used.
    /// </summary>
    private readonly Func<
        CleanupPlanAttempt,
        string,
        string?,
        string?,
        string,
        string?,
        CancellationToken,
        Task<IReadOnlyList<RemovedProtocolFact>>>? _cleanPlanInvoker;

    /// <summary>
    /// Test seam for deterministic fault injection and mid-operation cancellation in tests.
    /// Only active when set by test fixtures; in production this is null.
    /// </summary>
    internal Func<CleanerFailureStage, string?, Task>? FaultInjectionHook { get; set; }

    /// <summary>
    /// Event fired right as staging clean starts, allowing deterministic in-flight cancellation testing.
    /// </summary>
    internal event Action? OnStagingStarted;

    public SourceProtocolCleaner(ISourceInspector? inspector = null)
        : this(inspector, (ITargetedPostCleanVerifier?)null)
    {
    }

    internal SourceProtocolCleaner(ISourceInspector? inspector, ITargetedPostCleanVerifier? postCleanVerifier)
    {
        _inspector = inspector ?? new SourceInspector();
        _postCleanVerifier = postCleanVerifier ?? new TargetedPostCleanVerifier(_inspector);
        _cleanPlanInvoker = NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync;
    }

    internal SourceProtocolCleaner(
        Func<SourceMediaFacts, IReadOnlyList<PlannedCleanupAction>, string, string?, string?, string?, CancellationToken, Task<IReadOnlyList<RemovedProtocolFact>>> cleanInvoker,
        ISourceInspector? inspector = null,
        ITargetedPostCleanVerifier? postCleanVerifier = null)
    {
        _inspector = inspector ?? new SourceInspector();
        _postCleanVerifier = postCleanVerifier ?? new TargetedPostCleanVerifier(_inspector);
        _cleanInvoker = (facts, actions, targets, inImg, inVid, cleanupSource, cleanupSourceTarget, outImg, outVid, ct) =>
            cleanInvoker(facts, actions, inImg, inVid, outImg, outVid, ct);
    }

    internal SourceProtocolCleaner(
        ISourceInspector inspector,
        Func<SourceMediaFacts, IReadOnlyList<PlannedCleanupAction>, IReadOnlyList<PlannedArtifactTarget>?, string, string?, string?, string?, CancellationToken, Task<IReadOnlyList<RemovedProtocolFact>>> cleanInvoker,
        ITargetedPostCleanVerifier? postCleanVerifier = null)
    {
        _inspector = inspector ?? new SourceInspector();
        _postCleanVerifier = postCleanVerifier ?? new TargetedPostCleanVerifier(_inspector);
        if (cleanInvoker == null)
        {
            _cleanPlanInvoker = NativeCleanService.CleanSourceProtocolWithCleanupPlanAsync;
        }
        else
        {
            _cleanInvoker = (facts, actions, targets, inImg, inVid, cleanupSource, cleanupSourceTarget, outImg, outVid, ct) =>
                cleanInvoker(facts, actions, targets, inImg, inVid, outImg, outVid, ct);
        }
    }

    /// <summary>
    /// Test seam that replaces the plan-authorized Native invoker.  The
    /// replacement receives the <see cref="CleanupPlanAttempt"/> the cleaner
    /// already claimed and must hand it to the Native call — it must never
    /// claim the same plan a second time.
    /// </summary>
    internal SourceProtocolCleaner(
        Func<CleanupPlanAttempt, string, string?, string?, string, string?, CancellationToken, Task<IReadOnlyList<RemovedProtocolFact>>> cleanPlanInvoker,
        ISourceInspector? inspector = null,
        ITargetedPostCleanVerifier? postCleanVerifier = null)
    {
        _inspector = inspector ?? new SourceInspector();
        _postCleanVerifier = postCleanVerifier ?? new TargetedPostCleanVerifier(_inspector);
        _cleanPlanInvoker = cleanPlanInvoker;
    }

    public async Task<ProtocolCleanResult> CleanAsync(
        ProtocolCleanRequest request,
        IMediaWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(workspace);

        cancellationToken.ThrowIfCancellationRequested();

        var sw = Stopwatch.StartNew();
        var journal = new CleanerTransactionJournal();
        var currentProtocol = SourceProtocol.Unknown;
        CleanupPlan? cleanupAuthority = null;
        bool disposeAuthorityAfterClean = false;

        try
        {
            // -------------------------------------------------------------
            // Step 1: Preflight & Bundle Provenance
            // -------------------------------------------------------------
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Preflight, null).ConfigureAwait(false);

            var bundle = request.ExtractedBundle
                ?? throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.Preflight,
                    SourceProtocol.Unknown,
                    "ExtractedMediaBundle is required.");

            var facts = bundle.SourceFacts
                ?? throw new CleanerException(
                    CleanerFailureCategory.FactsNotConfirmed,
                    CleanerFailureStage.Preflight,
                    SourceProtocol.Unknown,
                    "SourceMediaFacts is missing from ExtractedMediaBundle.");

            currentProtocol = facts.Protocol;

            if (facts.Protocol == SourceProtocol.Unknown)
            {
                throw new CleanerException(
                    CleanerFailureCategory.UnsupportedProtocol,
                    CleanerFailureStage.Preflight,
                    SourceProtocol.Unknown,
                    "Cannot clean source with Unknown protocol.");
            }

            if (bundle.PrimaryImage == null || !File.Exists(bundle.PrimaryImage.Path))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.Preflight,
                    facts.Protocol,
                    $"Primary image artifact is missing: '{bundle.PrimaryImage?.Path}'.",
                    MediaArtifactKind.PrimaryImage);
            }

            if (bundle.MotionVideo != null && !File.Exists(bundle.MotionVideo.Path))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.Preflight,
                    facts.Protocol,
                    $"Declared motion video artifact is missing: '{bundle.MotionVideo.Path}'.",
                    MediaArtifactKind.MotionVideo);
            }

            if (bundle.GainMap != null && !File.Exists(bundle.GainMap.Path))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.Preflight,
                    facts.Protocol,
                    $"Declared GainMap artifact is missing: '{bundle.GainMap.Path}'.",
                    MediaArtifactKind.GainMap);
            }

            bool requiresCleanupSource = facts.Protocol == SourceProtocol.SamsungMotionPhotoJpeg &&
                facts.PreservationCarriers.Any(carrier =>
                    carrier.Kind == PreservationCarrierKind.SamsungSef && carrier.SourceIndex == 0);
            if (requiresCleanupSource && bundle.CleanupSource == null)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.Preflight,
                    facts.Protocol,
                    "Samsung SEF preservation requires a separate full source-container cleanup artifact.",
                    MediaArtifactKind.SourceContainer);
            }

            if (bundle.CleanupSource != null)
            {
                if (!requiresCleanupSource || bundle.CleanupSource.Kind != MediaArtifactKind.SourceContainer ||
                    bundle.CleanupSource.SourceOffset != 0 ||
                    string.Equals(bundle.CleanupSource.Path, bundle.PrimaryImage.Path, StringComparison.OrdinalIgnoreCase) ||
                    (bundle.MotionVideo != null &&
                     string.Equals(bundle.CleanupSource.Path, bundle.MotionVideo.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ArtifactFactMismatch,
                        CleanerFailureStage.Preflight,
                        facts.Protocol,
                        "CleanupSource is not a distinct, supported source-container artifact.",
                        MediaArtifactKind.SourceContainer);
                }

                if (!IsValidSha256(facts.PrimarySha256) ||
                    !string.Equals(bundle.CleanupSource.Sha256, facts.PrimarySha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ArtifactChangedSinceExtraction,
                        CleanerFailureStage.Preflight,
                        facts.Protocol,
                        "CleanupSource does not match the Inspector-confirmed primary source identity.",
                        MediaArtifactKind.SourceContainer);
                }
            }

            // -------------------------------------------------------------
            // Step 2: Verify P2 Artifact Identity
            // -------------------------------------------------------------
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.ArtifactVerification, null).ConfigureAwait(false);

            await VerifyArtifactIntegrityAsync(bundle.PrimaryImage, "PrimaryImage", facts.Protocol, cancellationToken).ConfigureAwait(false);
            if (bundle.MotionVideo != null)
            {
                await VerifyArtifactIntegrityAsync(bundle.MotionVideo, "MotionVideo", facts.Protocol, cancellationToken).ConfigureAwait(false);
            }
            if (bundle.GainMap != null)
            {
                await VerifyArtifactIntegrityAsync(bundle.GainMap, "GainMap", facts.Protocol, cancellationToken).ConfigureAwait(false);
            }
            if (bundle.CleanupSource != null)
            {
                await VerifyArtifactIntegrityAsync(bundle.CleanupSource, "CleanupSource", facts.Protocol, cancellationToken, requireFileIdentity: true).ConfigureAwait(false);
            }
            await VerifyAuxiliaryArtifactIntegrityAsync(bundle, facts.Protocol, cancellationToken).ConfigureAwait(false);

            // -------------------------------------------------------------
            // Step 3: Resolve Native Cleanup Authority (P3) & Handle NonLive
            // -------------------------------------------------------------
            // Destructive authority can only come from a Native cleanup plan:
            // either carried by the request (issued from the P2 extraction
            // record, or harness-issued in tests) or issued here from a
            // trusted Inspector-created extraction plan.  A Managed DTO alone
            // can never authorize cleaning.
            if (request.CleanupPlan != null)
            {
                cleanupAuthority = request.CleanupPlan;
            }
            else if (request.ExtractedBundle.CleanupPlan != null)
            {
                // Bundle produced by the Native extractor: the plan was issued
                // from the P2 extraction record before commit.
                cleanupAuthority = request.ExtractedBundle.CleanupPlan;
            }
            else if (request.ExtractionPlan != null)
            {
                cleanupAuthority = CleanupPlan.IssueFrom(request.ExtractionPlan, cancellationToken);
                disposeAuthorityAfterClean = true;
            }
            else
            {
                throw new CleanerException(
                    CleanerFailureCategory.CleanupAuthorizationMissing,
                    CleanerFailureStage.Authorization,
                    facts.Protocol,
                    "No Native cleanup-plan authority was provided. Destructive cleaning requires a CleanupPlan, or an ExtractionPlan from which one can be issued.");
            }

            if (facts.Protocol == SourceProtocol.NonLive)
            {
                return await ExecuteNonLiveNoOpAsync(
                    bundle,
                    workspace,
                    journal,
                    sw,
                    FaultInjectionHook,
                    cancellationToken).ConfigureAwait(false);
            }

            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Authorization, null).ConfigureAwait(false);

            var authorizations = facts.ConfirmedResidues;
            if (authorizations == null || authorizations.Count == 0)
            {
                throw new CleanerException(
                    CleanerFailureCategory.CleanupAuthorizationMissing,
                    CleanerFailureStage.Authorization,
                    facts.Protocol,
                    $"No cleanup authorizations available for declared protocol {facts.Protocol}.");
            }

            // -------------------------------------------------------------
            // Step 4: Build Immutable Cleanup Plan
            // -------------------------------------------------------------
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Planning, null).ConfigureAwait(false);

            var planActions = new List<PlannedCleanupAction>();
            var plannedResidueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var residue in authorizations)
            {
                if (residue.OwnerProtocol != facts.Protocol)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.CleanupAuthorizationMissing,
                        CleanerFailureStage.Planning,
                        facts.Protocol,
                        $"Residue '{residue.Id}' has mismatched OwnerProtocol: expected '{facts.Protocol}', actual '{residue.OwnerProtocol}'.");
                }

                if (residue.RequiredAfterExtraction && string.IsNullOrEmpty(residue.ExpectedFingerprint))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.StructureChanged,
                        CleanerFailureStage.Planning,
                        facts.Protocol,
                        $"Mandatory destructive residue '{residue.Id}' is missing ExpectedFingerprint.");
                }

                if (!plannedResidueIds.Add(residue.Id))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.AuthorizedResidueAmbiguous,
                        CleanerFailureStage.Planning,
                        facts.Protocol,
                        $"Duplicate authorization for ResidueId='{residue.Id}'. Each authorized mutation must be unique.");
                }

                planActions.Add(new PlannedCleanupAction
                {
                    ResidueId = residue.Id,
                    OwnerProtocol = residue.OwnerProtocol,
                    ArtifactRole = residue.ArtifactRole,
                    StructureKind = residue.StructureKind,
                    Selector = residue.Selector,
                    ExpectedSemantic = residue.ExpectedSemantic,
                    CoordinateSpace = residue.CoordinateSpace,
                    RemovalMode = residue.RemovalMode,
                    ExpectedFingerprint = residue.ExpectedFingerprint,
                    IsMandatory = residue.RequiredAfterExtraction
                });
            }

            if (!IsValidSha256(bundle.PrimaryImage.Sha256))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactChangedSinceExtraction,
                    CleanerFailureStage.Planning,
                    facts.Protocol,
                    $"Primary image artifact must have a valid non-zero SHA-256 (was '{bundle.PrimaryImage.Sha256}').");
            }

            var targets = new List<PlannedArtifactTarget>
            {
                new PlannedArtifactTarget
                {
                    Role = MediaArtifactKind.PrimaryImage,
                    ExpectedByteLength = bundle.PrimaryImage.ByteLength,
                    ExpectedSha256 = bundle.PrimaryImage.Sha256!
                }
            };
            if (bundle.MotionVideo != null)
            {
                if (!IsValidSha256(bundle.MotionVideo.Sha256))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ArtifactChangedSinceExtraction,
                        CleanerFailureStage.Planning,
                        facts.Protocol,
                        $"Motion video artifact must have a valid non-zero SHA-256 (was '{bundle.MotionVideo.Sha256}').");
                }
                targets.Add(new PlannedArtifactTarget
                {
                    Role = MediaArtifactKind.MotionVideo,
                    ExpectedByteLength = bundle.MotionVideo.ByteLength,
                    ExpectedSha256 = bundle.MotionVideo.Sha256!
                });
            }

            PlannedArtifactTarget? cleanupSourceTarget = null;
            if (bundle.CleanupSource != null)
            {
                cleanupSourceTarget = new PlannedArtifactTarget
                {
                    Role = MediaArtifactKind.SourceContainer,
                    ExpectedByteLength = bundle.CleanupSource.ByteLength,
                    ExpectedSha256 = bundle.CleanupSource.Sha256!
                };
            }

            var cleanupPlan = new ProtocolCleanupPlan
            {
                Protocol = facts.Protocol,
                Actions = planActions,
                ArtifactTargets = targets,
                CleanupSourceTarget = cleanupSourceTarget
            };

            // Capture frozen preservation baseline before destructive execution
            var preservationBaseline = await MetadataPreservationVerifier.CaptureBaselineAsync(bundle, cancellationToken).ConfigureAwait(false);

            // -------------------------------------------------------------
            // Step 5: Stage Clean (Isolated Workspace)
            // -------------------------------------------------------------
            cancellationToken.ThrowIfCancellationRequested();
            journal.SetState(CleanerTransactionState.Staging);
            OnStagingStarted?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();

            string stagingDir = Path.Combine(workspace.RootDirectory, "staging_" + Guid.NewGuid().ToString("N"));
            journal.StagingDir = stagingDir;
            Directory.CreateDirectory(stagingDir);

            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Staging, "BeforeNative").ConfigureAwait(false);

            string imgExt = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".heic" : ".jpg";
            string stagedImgPath = Path.Combine(stagingDir, "stage-img" + imgExt);

            string? stagedVidPath = null;
            if (bundle.MotionVideo != null)
            {
                string vidExt = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
                stagedVidPath = Path.Combine(stagingDir, "stage-vid" + vidExt);
            }

            IReadOnlyList<RemovedProtocolFact> removedFacts;
            nint cleanContextHandle = nint.Zero;
            bool cleanUseHarness = false;
            try
            {
                using CleanupPlanAttempt planAttempt = cleanupAuthority.BeginCleanupAttempt(cancellationToken);
                cleanContextHandle = planAttempt.ContextLease.Handle;
                cleanUseHarness = planAttempt.UseHarnessLibrary;
                if (_cleanPlanInvoker != null)
                {
                    removedFacts = await _cleanPlanInvoker(
                        planAttempt,
                        bundle.PrimaryImage.Path,
                        bundle.MotionVideo?.Path,
                        bundle.CleanupSource?.Path,
                        stagedImgPath,
                        stagedVidPath,
                        cancellationToken).ConfigureAwait(false);

                    // The Native cleaner registered every staged output it
                    // created from the creating handle.  Only objects on that
                    // registry are owned by this transaction; a file that
                    // merely exists at the path is foreign until Native proves
                    // it created it.  A missing registry entry is fail-closed.
                    ApplyNativeStagedOwnership(
                        planAttempt,
                        journal,
                        stagedImgPath,
                        stagedVidPath,
                        FaultInjectionHook,
                        facts.Protocol);
                }
                else
                {
                    removedFacts = await _cleanInvoker!(
                        facts,
                        cleanupPlan.Actions,
                        cleanupPlan.ArtifactTargets,
                        bundle.PrimaryImage.Path,
                        bundle.MotionVideo?.Path,
                        bundle.CleanupSource?.Path,
                        cleanupPlan.CleanupSourceTarget,
                        stagedImgPath,
                        stagedVidPath,
                        cancellationToken).ConfigureAwait(false);

                    // Legacy raw-DTO invoker has NO Native ownership registry:
                    // there is no creating-handle proof for any file it wrote.
                    // Such files are therefore never claimed as transaction
                    // owned; rollback/commit will not touch them (fail closed).
                }
            }
            catch (CleanerException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                // The invoker may have partially written staged outputs before
                // cancelling.  Ownership comes ONLY from the Native ownership
                // registry (identities recorded from the creating handles).  A
                // file that merely exists at a path this transaction allocated
                // is foreign until Native proves it created it: it is never
                // claimed, so rollback can never delete an object this
                // transaction did not prove it created (fail closed).
                CaptureNativeStagedOutputs(
                    cleanContextHandle,
                    cleanUseHarness,
                    journal,
                    FaultInjectionHook,
                    facts.Protocol);
                throw;
            }
            catch (Exception ex)
            {
                CaptureNativeStagedOutputs(
                    cleanContextHandle,
                    cleanUseHarness,
                    journal,
                    FaultInjectionHook,
                    facts.Protocol);
                throw new CleanerException(
                    CleanerFailureCategory.StructureChanged,
                    CleanerFailureStage.Staging,
                    facts.Protocol,
                    $"Native media operation failed: {ex.Message}",
                    innerException: ex);
            }

            if (!File.Exists(stagedImgPath))
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    facts.Protocol,
                    "Cleaned staged image was not generated.");
            }
            if (stagedVidPath != null && !File.Exists(stagedVidPath))
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    facts.Protocol,
                    "Cleaned staged video was not generated.");
            }
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Staging, "ImageStaged").ConfigureAwait(false);
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Staging, "VideoStaged").ConfigureAwait(false);

            // -------------------------------------------------------------
            // Step 5.5: Destructive Authority Reconciliation Gate
            // -------------------------------------------------------------
            var authorizedActionsMap = cleanupPlan.Actions.ToDictionary(a => a.ResidueId, StringComparer.Ordinal);
            var seenResidueIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in removedFacts)
            {
                if (string.IsNullOrEmpty(fact.ResidueId) || !authorizedActionsMap.TryGetValue(fact.ResidueId, out var action))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.RemovalWouldTouchUnknownData,
                        CleanerFailureStage.Staging,
                        facts.Protocol,
                        $"Native cleaner performed unauthorized removal: ResidueId='{fact.ResidueId}', Component='{fact.Component}', Desc='{fact.Description}'. All mutations must be authorized by CleanupPlan.");
                }

                if (fact.ArtifactRole != action.ArtifactRole || fact.StructureKind != action.StructureKind)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.RemovalWouldTouchUnknownData,
                        CleanerFailureStage.Staging,
                        facts.Protocol,
                        $"Native cleaner reported fact with mismatched identity for ResidueId='{fact.ResidueId}': expected (Role={action.ArtifactRole}, Kind={action.StructureKind}), actual (Role={fact.ArtifactRole}, Kind={fact.StructureKind}).");
                }

                if (!seenResidueIds.Add(fact.ResidueId))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.AuthorizedResidueAmbiguous,
                        CleanerFailureStage.Staging,
                        facts.Protocol,
                        $"Duplicate removal fact reported for ResidueId='{fact.ResidueId}'. Each authorized mutation must be unique.");
                }

                // Enforcement of ExpectedFingerprint: fail closed if expected is non-empty
                if (!string.IsNullOrEmpty(action.ExpectedFingerprint))
                {
                    if (string.IsNullOrEmpty(fact.BeforeFingerprint))
                    {
                        throw new CleanerException(
                            CleanerFailureCategory.StructureChanged,
                            CleanerFailureStage.Staging,
                            facts.Protocol,
                            $"Fingerprint missing from removal fact for ResidueId='{fact.ResidueId}': expected='{action.ExpectedFingerprint}', actual BeforeFingerprint was not provided.");
                    }

                    if (!string.Equals(action.ExpectedFingerprint, fact.BeforeFingerprint, StringComparison.Ordinal))
                    {
                        throw new CleanerException(
                            CleanerFailureCategory.StructureChanged,
                            CleanerFailureStage.Staging,
                            facts.Protocol,
                            $"Fingerprint mismatch for ResidueId='{fact.ResidueId}': expected='{action.ExpectedFingerprint}', actual='{fact.BeforeFingerprint}'.");
                    }
                }
            }

            foreach (var action in cleanupPlan.Actions)
            {
                if (action.IsMandatory && !seenResidueIds.Contains(action.ResidueId))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.AuthorizedResidueNotFound,
                        CleanerFailureStage.Staging,
                        facts.Protocol,
                        $"Mandatory authorized residue was not removed by native cleaner: ResidueId='{action.ResidueId}'.");
                }
            }

            // -------------------------------------------------------------
            // Step 6: Preservation Diff
            // -------------------------------------------------------------
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.PreservationDiff, null).ConfigureAwait(false);

            var preservationReport = await MetadataPreservationVerifier.VerifyAgainstBaselineAsync(
                preservationBaseline, stagedImgPath, stagedVidPath,
                stagedGainMapPath: bundle.GainMap?.Path,
                cancellationToken).ConfigureAwait(false);

            if (preservationReport.OverallOutcome != PreservationOutcome.Preserved)
            {
                throw new CleanerException(
                    CleanerFailureCategory.UnexpectedMetadataChange,
                    CleanerFailureStage.PreservationDiff,
                    facts.Protocol,
                    $"Preservation verification failed ({preservationReport.OverallOutcome}): {preservationReport.Summary}");
            }

            // -------------------------------------------------------------
            // Step 7: Structural & Media Validation
            // -------------------------------------------------------------
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.MediaValidation, null).ConfigureAwait(false);

            long stagedImgLen = new FileInfo(stagedImgPath).Length;
            if (stagedImgLen == 0)
            {
                throw new CleanerException(
                    CleanerFailureCategory.MediaInvalid,
                    CleanerFailureStage.MediaValidation,
                    facts.Protocol,
                    "Cleaned staged image is empty.");
            }

            if (stagedVidPath != null)
            {
                long stagedVidLen = new FileInfo(stagedVidPath).Length;
                if (stagedVidLen == 0)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.MediaInvalid,
                        CleanerFailureStage.MediaValidation,
                        facts.Protocol,
                        "Cleaned staged video is empty.");
                }
            }

            // -------------------------------------------------------------
            // Step 8: Targeted Post-clean Gate
            // -------------------------------------------------------------
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.PostCleanInspection, "BeforeInspect").ConfigureAwait(false);

            await _postCleanVerifier.VerifyPostCleanAsync(
                facts, cleanupPlan, stagedImgPath, stagedVidPath, cancellationToken).ConfigureAwait(false);

            // -------------------------------------------------------------
            // Step 9: Bundle Transaction Commit
            // -------------------------------------------------------------
            journal.SetState(CleanerTransactionState.Validated);
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "BeforePublish").ConfigureAwait(false);

            string cleanImgPath = workspace.AllocateFilePath("clean-img", imgExt);
            string? cleanVidPath = null;
            if (stagedVidPath != null)
            {
                string vidExt = Path.GetExtension(stagedVidPath);
                cleanVidPath = workspace.AllocateFilePath("clean-vid", vidExt);
            }

            // The final commit is handle-bound.  Ownership comes from the
            // Native staged-output registry captured at staging time and was
            // immediately converted to a retained, identity-verified handle
            // before preservation/validation/post-clean inspection.  That same
            // handle now supplies the final re-verification, evidence and
            // no-overwrite rename.  There is no verify -> close handle ->
            // pathname re-select window: a foreign object that takes over the
            // staged pathname can never be selected as the publish source, and
            // a foreign object at the destination can never be overwritten (the
            // kernel rename decides atomically).
            PublishedOwnedFile? imgPublished = null;
            PublishedOwnedFile? vidPublished = null;
            CleanerTransactionJournal.TransactionOwnedObject? imgRecord = null;
            CleanerTransactionJournal.TransactionOwnedObject? vidRecord = null;

            journal.SetState(CleanerTransactionState.Committing);
            SafeFileHandle? stagedImgHandle = null;
            SafeFileHandle? stagedVidHandle = null;
            try
            {
                imgRecord = RequireRegisteredStagedObject(journal, stagedImgPath);
                stagedImgHandle = imgRecord.RetainedHandle;
                if (stagedImgHandle is null || stagedImgHandle.IsInvalid)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.OutputCreateFailed,
                        CleanerFailureStage.Commit,
                        facts.Protocol,
                        $"Staged image '{stagedImgPath}' has no retained exact ownership handle; refusing pathname-only publish.",
                        MediaArtifactKind.PrimaryImage);
                }

                // Deterministic adversarial seam: the staging object has been
                // verified from the retained handle, the handle is still open,
                // and the handle rename has NOT yet executed.
                if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "AfterImageIdentityVerifiedBeforeRename").ConfigureAwait(false);

                // Register the destination before the handle-based rename.  If
                // the rename throws, the record still points at the staged path;
                // if it succeeds, MarkPublished transitions the same object to
                // the destination without duplicating ownership records.
                journal.RegisterPublishedRecord(imgRecord, cleanImgPath);
                imgPublished = WindowsOwnedFilePublisher.PublishOwnedHandle(
                    stagedImgHandle, cleanImgPath, imgRecord.Identity, cancellationToken);
                journal.MarkPublished(imgRecord);

                // The published object's exact handle is deliberately KEPT
                // OPEN until the whole bundle transaction commits
                // (SetState(Committed) below).  While the transaction is still
                // active, the retained handle is the strongest ownership
                // authority: if a foreign actor renames the published object
                // away from cleanImgPath mid-transaction, the handle still
                // refers to it, so a later failure can delete it exactly
                // (FileDispositionInfo through the handle) no matter what
                // pathname it now occupies.  Closing it here would let
                // rollback's pathname lookup miss a renamed-away object and
                // falsely report RolledBack.

                if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "ImagePublished").ConfigureAwait(false);

                if (stagedVidPath != null && cleanVidPath != null)
                {
                    vidRecord = RequireRegisteredStagedObject(journal, stagedVidPath);
                    stagedVidHandle = vidRecord.RetainedHandle;
                    if (stagedVidHandle is null || stagedVidHandle.IsInvalid)
                    {
                        throw new CleanerException(
                            CleanerFailureCategory.OutputCreateFailed,
                            CleanerFailureStage.Commit,
                            facts.Protocol,
                            $"Staged video '{stagedVidPath}' has no retained exact ownership handle; refusing pathname-only publish.",
                            MediaArtifactKind.MotionVideo);
                    }

                    // Deterministic adversarial seam (video counterpart).
                    if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "AfterVideoIdentityVerifiedBeforeRename").ConfigureAwait(false);

                    journal.RegisterPublishedRecord(vidRecord, cleanVidPath);
                    vidPublished = WindowsOwnedFilePublisher.PublishOwnedHandle(
                        stagedVidHandle, cleanVidPath, vidRecord.Identity, cancellationToken);
                    journal.MarkPublished(vidRecord);
                    // stagedVidHandle stays open too, for the same reason.
                }

                if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "BeforeBundleCommit").ConfigureAwait(false);
                journal.SetState(CleanerTransactionState.Committed);
            }
            catch (OperationCanceledException)
            {
                // Fail closed on cancellation: every artifact whose publish
                // already succeeded is deleted through its retained exact
                // handle before the cancellation propagates, so a foreign
                // pathname rename can never hide a transaction-owned object
                // from rollback.
                if (imgPublished != null && imgRecord != null)
                {
                    journal.CleanupRetainedObjectOrRecordFailure(imgRecord);
                }
                if (vidPublished != null && vidRecord != null)
                {
                    journal.CleanupRetainedObjectOrRecordFailure(vidRecord);
                }
                throw;
            }
            catch (Exception ex)
            {
                // Fail closed on any post-publish failure: delete every
                // published artifact through its retained exact handle (the
                // strongest authority) and drop the journal record only when
                // the deletion is proven; otherwise keep the record and force
                // a rollback failure so a false "RolledBack" can never be
                // reported while a transaction-owned object may still exist.
                if (imgPublished != null && imgRecord != null)
                {
                    journal.CleanupRetainedObjectOrRecordFailure(imgRecord);
                }
                if (vidPublished != null && vidRecord != null)
                {
                    journal.CleanupRetainedObjectOrRecordFailure(vidRecord);
                }
                throw new CleanerException(
                    CleanerFailureCategory.PublishFailed,
                    CleanerFailureStage.Commit,
                    facts.Protocol,
                    $"Failed to publish cleaned bundle to destination paths: {ex.Message}",
                    innerException: ex);
            }
            finally
            {
                // Retained handles belong to the transaction journal and are
                // released by the outer finally only after the transaction is
                // terminal.  In particular, rollback still needs staged
                // handles when validation or preservation fails before commit.
                if (journal.State == CleanerTransactionState.Committed)
                {
                    TryDeleteDirectory(stagingDir);
                    journal.StagingDir = null;
                }
            }

            // -------------------------------------------------------------
            // Step 10: Emit Evidence
            // -------------------------------------------------------------
            // The final MediaArtifact ownership fields (FileIdentity, length,
            // SHA-256) come from the same-handle publication evidence only —
            // never from a pathname re-lookup after the handle was closed, so
            // a foreign object that later occupies the destination can never
            // be re-claimed as this transaction's output.
            var cleanImgArtifact = new MediaArtifact
            {
                Path = imgPublished!.FinalPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = bundle.PrimaryImage.MimeType,
                ImageContainer = bundle.PrimaryImage.ImageContainer,
                ImageCodec = bundle.PrimaryImage.ImageCodec,
                ByteLength = imgPublished.ByteLength,
                Sha256 = imgPublished.Sha256,
                FileIdentity = imgPublished.FileIdentity
            };

            MediaArtifact? cleanVidArtifact = null;
            if (vidPublished != null && cleanVidPath != null)
            {
                cleanVidArtifact = new MediaArtifact
                {
                    Path = vidPublished.FinalPath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = bundle.MotionVideo!.MimeType,
                    VideoContainer = bundle.MotionVideo.VideoContainer,
                    VideoCodec = bundle.MotionVideo.VideoCodec,
                    ByteLength = vidPublished.ByteLength,
                    Sha256 = vidPublished.Sha256,
                    FileIdentity = vidPublished.FileIdentity
                };
            }

            sw.Stop();

            return new ProtocolCleanResult
            {
                Success = true,
                CleanedImage = cleanImgArtifact,
                CleanedVideo = cleanVidArtifact,
                CleanedGainMap = bundle.GainMap,
                AuxiliaryMedia = bundle.AuxiliaryMedia,
                PreservationCarriers = bundle.PreservationCarriers,
                RemovedFacts = removedFacts,
                PreservationOutcome = preservationReport.OverallOutcome,
                PreservationReport = preservationReport,
                GainMapExpectedSha256 = preservationBaseline.GainMapExpected
                    ? preservationBaseline.GainMapSha256
                    : null,
                CleanupPlan = cleanupPlan,
                TransactionState = journal.State,
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            try
            {
                journal.Rollback(FaultInjectionHook, currentProtocol);
            }
            catch (CleanerException rbEx)
            {
                throw new CleanerException(
                    CleanerFailureCategory.RollbackFailed,
                    CleanerFailureStage.Rollback,
                    currentProtocol,
                    $"Cancellation was requested but rollback failed: {rbEx.Message}",
                    innerException: rbEx);
            }
            throw;
        }
        catch (CleanerException ex)
        {
            sw.Stop();
            try
            {
                journal.Rollback(FaultInjectionHook, currentProtocol);
            }
            catch (CleanerException rbEx)
            {
                return new ProtocolCleanResult
                {
                    Success = false,
                    ErrorMessage = $"Original error ({ex.Category}): {ex.Message}. Critical rollback failure: {rbEx.Message}",
                    FailureCategory = CleanerFailureCategory.RollbackFailed,
                    FailureStage = CleanerFailureStage.Rollback,
                    TransactionState = CleanerTransactionState.RollbackFailed,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Duration = sw.Elapsed
                };
            }
            return new ProtocolCleanResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                FailureCategory = ex.Category,
                FailureStage = ex.Stage,
                TransactionState = journal.State,
                PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            try
            {
                journal.Rollback(FaultInjectionHook, currentProtocol);
            }
            catch (CleanerException rbEx)
            {
                return new ProtocolCleanResult
                {
                    Success = false,
                    ErrorMessage = $"Original error: {ex.Message}. Critical rollback failure: {rbEx.Message}",
                    FailureCategory = CleanerFailureCategory.RollbackFailed,
                    FailureStage = CleanerFailureStage.Rollback,
                    TransactionState = CleanerTransactionState.RollbackFailed,
                    PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                    Duration = sw.Elapsed
                };
            }
            return new ProtocolCleanResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                FailureCategory = CleanerFailureCategory.None,
                FailureStage = CleanerFailureStage.Preflight,
                TransactionState = journal.State,
                PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                Duration = sw.Elapsed
            };
        }
        finally
        {
            // Plans issued inside CleanAsync are released here; caller-supplied
            // plans remain owned by the caller.  Retained transaction handles
            // are closed only after the transaction has reached Committed,
            // RolledBack, or RollbackFailed and all exact cleanup attempts have
            // completed.
            journal.DisposeOwnedHandles();
            if (disposeAuthorityAfterClean && cleanupAuthority != null)
            {
                cleanupAuthority.Dispose();
            }
        }
    }

    /// <summary>
    /// After a staging invoker throws (exception or cancellation), the Native
    /// side may have partially written staged outputs.  Ownership comes ONLY
    /// from the Native staged-output registry (identities captured from the
    /// creating handle at publish time).  This failure path reads that
    /// registry and records exactly the objects the Native side proved it
    /// created.  An expected staging pathname (stagedImgPath / stagedVidPath)
    /// or any file that merely appears inside the staging directory never
    /// constitutes ownership by itself: rollback can never delete an object
    /// this transaction did not prove it created.
    /// </summary>
    private static void CaptureNativeStagedOutputs(
        nint contextHandle,
        bool useHarnessLibrary,
        CleanerTransactionJournal journal,
        Func<CleanerFailureStage, string?, Task>? faultHook,
        SourceProtocol protocol)
    {
        if (contextHandle == nint.Zero)
        {
            return;
        }

        IReadOnlyList<NativeCleanService.CleanStagedOutputRecord> native;
        try
        {
            native = NativeCleanService.QueryStagedOutputs(contextHandle, useHarnessLibrary);
        }
        catch (Exception ex)
        {
            // A failure-path registry query is itself part of the ownership
            // proof.  Without it we cannot know whether Native created an
            // object that was subsequently renamed away, so never allow the
            // transaction to claim RolledBack on pathname absence alone.
            journal.AddRollbackFailure(
                $"Unable to recover Native staged-output ownership registry after a staging failure; rollback ownership is unproven: {ex.Message}");
            return;
        }

        foreach (var rec in native)
        {
            AddNativeStagedRecord(journal, rec);
        }

        // Convert every registry identity to a retained, identity-verified
        // Managed handle before the failure escapes the staging boundary.  A
        // failure-path query may observe a pathname that was already renamed or
        // replaced; retain the record as unproven and let rollback fail closed
        // rather than treating a missing pathname as proof of cleanup.
        journal.AcquireRetainedHandles(faultHook, protocol, failOnAcquisitionError: false);
    }

    /// <summary>
    /// The Native cleaner must report ownership of every staged output it was
    /// asked to produce.  A staged file that is not on the Native registry is
    /// fail-closed: this transaction refuses to claim a filesystem object the
    /// Native side did not prove it created.
    /// </summary>
    private static void ApplyNativeStagedOwnership(
        CleanupPlanAttempt planAttempt,
        CleanerTransactionJournal journal,
        string stagedImgPath,
        string? stagedVidPath,
        Func<CleanerFailureStage, string?, Task>? faultHook,
        SourceProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(planAttempt);
        IReadOnlyList<NativeCleanService.CleanStagedOutputRecord> native =
            NativeCleanService.QueryStagedOutputs(planAttempt.ContextLease.Handle, planAttempt.UseHarnessLibrary);

        var img = native.FirstOrDefault(r => PathEquals(r.FinalPath, stagedImgPath));
        if (img.FinalPath is null)
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Staging,
                SourceProtocol.Unknown,
                $"Native cleaner did not report ownership of staged image '{stagedImgPath}'; refusing to claim a file this transaction did not prove it created.");
        }
        AddNativeStagedRecord(journal, img);

        if (stagedVidPath != null)
        {
            var vid = native.FirstOrDefault(r => PathEquals(r.FinalPath, stagedVidPath));
            if (vid.FinalPath is null)
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    SourceProtocol.Unknown,
                    $"Native cleaner did not report ownership of staged video '{stagedVidPath}'; refusing to claim a file this transaction did not prove it created.");
            }
            AddNativeStagedRecord(journal, vid);
        }

        // Register any auxiliary outputs (e.g. GainMap) Native created too.
        foreach (var rec in native)
        {
            AddNativeStagedRecord(journal, rec);
        }

        // This is intentionally part of the immediate post-Native capture
        // boundary.  Preservation, validation, post-clean inspection and final
        // commit must all use these same retained exact handles.
        journal.AcquireRetainedHandles(faultHook, protocol, failOnAcquisitionError: true);
    }

    private static CleanerTransactionJournal.TransactionOwnedObject AddNativeStagedRecord(
        CleanerTransactionJournal journal,
        NativeCleanService.CleanStagedOutputRecord rec)
    {
        if (string.IsNullOrEmpty(rec.FinalPath))
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Staging,
                SourceProtocol.Unknown,
                "Native cleaner reported an owned staged output without a path.");
        }
        CleanerTransactionJournal.TransactionOwnedObject? existing =
            journal.StagedPaths.FirstOrDefault(r => PathEquals(r.StagedPath, rec.FinalPath));
        if (existing != null)
        {
            return existing;
        }
        var identity = new WindowsFileIdentity
        {
            VolumeSerialNumber = rec.VolumeSerial,
            FileIndex = rec.FileIndex,
            LinkCount = rec.LinkCount,
            FileAttributes = 0
        };
        return journal.AddStagedRecord(rec.FinalPath, (MediaArtifactKind)rec.ArtifactRole, identity);
    }

    private static bool PathEquals(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static CleanerTransactionJournal.TransactionOwnedObject RequireRegisteredStagedObject(
        CleanerTransactionJournal journal,
        string path)
    {
        CleanerTransactionJournal.TransactionOwnedObject? rec =
            journal.StagedPaths.FirstOrDefault(r => PathEquals(r.StagedPath, path));
        if (rec is null)
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"No native-registered ownership for staged '{path}'; refusing to publish an object this transaction did not prove it created.");
        }
        if (rec.RetainedHandle is null || rec.RetainedHandle.IsInvalid)
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Native-registered staged object '{path}' has no retained exact ownership handle; refusing pathname-only publish.");
        }
        return rec;
    }

    private static bool IsValidSha256(string? sha)
    {
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64) return false;
        bool nonZero = false;
        for (int i = 0; i < 64; i++)
        {
            char c = sha[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
            {
                return false;
            }
            if (c != '0') nonZero = true;
        }
        return nonZero;
    }

    private static async Task VerifyArtifactIntegrityAsync(
        MediaArtifact artifact,
        string roleName,
        SourceProtocol protocol,
        CancellationToken cancellationToken,
        bool requireFileIdentity = false)
    {
        if (!File.Exists(artifact.Path))
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactFactMismatch,
                CleanerFailureStage.ArtifactVerification,
                protocol,
                $"{roleName} artifact file does not exist at '{artifact.Path}'.");
        }

        var fi = new FileInfo(artifact.Path);
        if (artifact.ByteLength > 0 && fi.Length != artifact.ByteLength)
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.ArtifactVerification,
                protocol,
                $"{roleName} artifact length changed since extraction: declared {artifact.ByteLength} bytes, found {fi.Length} bytes.");
        }

        if (!IsValidSha256(artifact.Sha256))
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.ArtifactVerification,
                protocol,
                $"{roleName} artifact SHA-256 is missing, malformed, or all-zeroes: '{artifact.Sha256}'.");
        }

        if (requireFileIdentity && artifact.FileIdentity == null)
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.ArtifactVerification,
                protocol,
                $"{roleName} artifact is missing the Windows file identity captured at extraction.",
                artifact.Kind);
        }

        if (artifact.FileIdentity != null)
        {
            WindowsFileIdentity currentIdentity;
            try
            {
                currentIdentity = WindowsFileIdentity.Capture(artifact.Path);
            }
            catch (Exception ex)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactChangedSinceExtraction,
                    CleanerFailureStage.ArtifactVerification,
                    protocol,
                    $"Unable to revalidate the Windows file identity for {roleName} artifact '{artifact.Path}'.",
                    artifact.Kind,
                    ex);
            }

            if (currentIdentity.IsReparsePoint ||
                !currentIdentity.Matches(artifact.FileIdentity) ||
                currentIdentity.LinkCount != 1)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactChangedSinceExtraction,
                    CleanerFailureStage.ArtifactVerification,
                    protocol,
                    $"{roleName} artifact is not the same Windows file object captured at extraction.",
                    artifact.Kind);
            }
        }

        using var fs = new FileStream(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sha = SHA256.Create();
        byte[] hash = await sha.ComputeHashAsync(fs, cancellationToken).ConfigureAwait(false);
        string actualSha = Convert.ToHexString(hash);

        if (!string.Equals(actualSha, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.ArtifactVerification,
                protocol,
                $"{roleName} artifact SHA-256 changed since extraction: declared '{artifact.Sha256}', found '{actualSha}'.");
        }
    }

    private static async Task VerifyAuxiliaryArtifactIntegrityAsync(
        ExtractedMediaBundle bundle,
        SourceProtocol protocol,
        CancellationToken cancellationToken)
    {
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (AuxiliaryMediaDescriptor descriptor in bundle.AuxiliaryMedia)
        {
            if (string.IsNullOrWhiteSpace(descriptor.StableIdentity) ||
                !identities.Add(descriptor.StableIdentity))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.ArtifactVerification,
                    protocol,
                    $"Auxiliary descriptor identity is missing or duplicated: '{descriptor.StableIdentity}'.",
                    MediaArtifactKind.AuxiliaryItem);
            }

            if (descriptor.Representation == AuxiliaryRepresentation.Materialized &&
                descriptor.MaterializedArtifact == null)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.ArtifactVerification,
                    protocol,
                    $"Materialized auxiliary '{descriptor.StableIdentity}' has no materialized artifact.",
                    descriptor.ArtifactRole);
            }

            MediaArtifact? artifact = descriptor.MaterializedArtifact;
            if (artifact == null)
            {
                continue;
            }

            MediaArtifactKind expectedKind = descriptor.ArtifactRole == MediaArtifactKind.GainMap
                ? MediaArtifactKind.GainMap
                : MediaArtifactKind.AuxiliaryItem;
            if (artifact.Kind != expectedKind)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactFactMismatch,
                    CleanerFailureStage.ArtifactVerification,
                    protocol,
                    $"Auxiliary '{descriptor.StableIdentity}' has mismatched artifact role {artifact.Kind}; expected {expectedKind}.",
                    descriptor.ArtifactRole);
            }

            await VerifyArtifactIntegrityAsync(
                artifact,
                $"Auxiliary:{descriptor.StableIdentity}",
                protocol,
                cancellationToken).ConfigureAwait(false);

            if (!IsValidSha256(descriptor.SourceSha256) ||
                !string.Equals(artifact.Sha256, descriptor.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
                (descriptor.SourceLength > 0 && artifact.ByteLength != descriptor.SourceLength))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactChangedSinceExtraction,
                    CleanerFailureStage.ArtifactVerification,
                    protocol,
                    $"Auxiliary '{descriptor.StableIdentity}' no longer matches its Inspector-confirmed source identity.",
                    descriptor.ArtifactRole);
            }
        }
    }

    private static async Task<ProtocolCleanResult> ExecuteNonLiveNoOpAsync(
        ExtractedMediaBundle bundle,
        IMediaWorkspace workspace,
        CleanerTransactionJournal journal,
        Stopwatch sw,
        Func<CleanerFailureStage, string?, Task>? faultHook,
        CancellationToken cancellationToken)
    {
        journal.SetState(CleanerTransactionState.Staging);

        string imgExt = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".heic" : ".jpg";
        string cleanImgPath = workspace.AllocateFilePath("clean-img", imgExt);

        // A no-op copy is still a transaction-owned output.  Record and retain
        // an exact handle before any later SHA/cancellation/failure point, so a
        // pathname that goes missing cannot be mistaken for proof of cleanup.
        CleanerTransactionJournal.TransactionOwnedObject imgRecord =
            CopyNonLiveArtifact(
                bundle.PrimaryImage,
                cleanImgPath,
                journal,
                MediaArtifactKind.PrimaryImage,
                faultHook,
                cancellationToken);

        string? cleanVidPath = null;
        CleanerTransactionJournal.TransactionOwnedObject? vidRecord = null;
        if (bundle.MotionVideo != null)
        {
            string vidExt = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
            cleanVidPath = workspace.AllocateFilePath("clean-vid", vidExt);
            vidRecord = CopyNonLiveArtifact(
                bundle.MotionVideo,
                cleanVidPath,
                journal,
                MediaArtifactKind.MotionVideo,
                faultHook,
                cancellationToken);
        }

        var cleanImgArtifact = bundle.PrimaryImage with
        {
            Path = cleanImgPath,
            ByteLength = new FileInfo(imgRecord.CurrentPath).Length,
            Sha256 = await workspace.ComputeFileSha256Async(cleanImgPath, cancellationToken).ConfigureAwait(false),
            FileIdentity = imgRecord.Identity
        };

        MediaArtifact? cleanVidArtifact = null;
        if (vidRecord != null && cleanVidPath != null && bundle.MotionVideo != null)
        {
            cleanVidArtifact = bundle.MotionVideo with
            {
                Path = cleanVidPath,
                ByteLength = new FileInfo(vidRecord.CurrentPath).Length,
                Sha256 = await workspace.ComputeFileSha256Async(cleanVidPath, cancellationToken).ConfigureAwait(false),
                FileIdentity = vidRecord.Identity
            };
        }

        bool imgMatch = string.Equals(bundle.PrimaryImage.Sha256, cleanImgArtifact.Sha256, StringComparison.OrdinalIgnoreCase);
        bool vidMatch = bundle.MotionVideo == null || (cleanVidArtifact != null && string.Equals(bundle.MotionVideo.Sha256, cleanVidArtifact.Sha256, StringComparison.OrdinalIgnoreCase));

        if (!imgMatch || !vidMatch)
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.PostCleanInspection,
                SourceProtocol.NonLive,
                $"NonLive verbatim copy failed SHA verification (image match: {imgMatch}, video match: {vidMatch}).");
        }

        sw.Stop();

        var nonLivePreservationItems = new List<PreservationReportItem>
        {
            new PreservationReportItem
            {
                Name = "NonLiveNoOp",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = $"NonLive source verified 0 modification (Primary image SHA-256 match: {bundle.PrimaryImage.Sha256})."
            }
        };
        foreach (PreservationCarrier carrier in bundle.PreservationCarriers)
        {
            nonLivePreservationItems.Add(new PreservationReportItem
            {
                Name = $"Carrier:{carrier.StableIdentity}",
                Status = PreservationCheckStatus.VerifiedPreserved,
                Details = $"Carrier range [{carrier.SourceOffset},{carrier.SourceOffset + carrier.SourceLength}) is covered by the verified verbatim primary artifact copy."
            });
        }

        var report = new PreservationReport
        {
            OverallOutcome = PreservationOutcome.Preserved,
            Items = nonLivePreservationItems,
            Summary = "Source is NonLive; artifacts carried through verbatim with verified identical SHA-256."
        };

        if (faultHook != null)
        {
            await faultHook(CleanerFailureStage.Commit, "BeforeBundleCommit").ConfigureAwait(false);
        }
        journal.SetState(CleanerTransactionState.Committed);

        return new ProtocolCleanResult
        {
            Success = true,
            CleanedImage = cleanImgArtifact,
            CleanedVideo = cleanVidArtifact,
            CleanedGainMap = bundle.GainMap,
            AuxiliaryMedia = bundle.AuxiliaryMedia,
            PreservationCarriers = bundle.PreservationCarriers,
            RemovedFacts = [],
            PreservationOutcome = PreservationOutcome.Preserved,
            PreservationReport = report,
            GainMapExpectedSha256 = bundle.GainMap?.Sha256,
            CleanupPlan = new ProtocolCleanupPlan
            {
                Protocol = SourceProtocol.NonLive,
                Actions = [],
                ArtifactTargets = []
            },
            TransactionState = CleanerTransactionState.Committed,
            Duration = sw.Elapsed
        };
    }

    private static CleanerTransactionJournal.TransactionOwnedObject CopyNonLiveArtifact(
        MediaArtifact source,
        string destinationPath,
        CleanerTransactionJournal journal,
        MediaArtifactKind artifactRole,
        Func<CleanerFailureStage, string?, Task>? faultHook,
        CancellationToken cancellationToken)
    {
        FileStream? output = null;
        CleanerTransactionJournal.TransactionOwnedObject? record = null;
        try
        {
            // The creator is deliberately closed before we acquire the P3
            // retained handle.  The retained handle shares READ|DELETE but
            // never WRITE; keeping a FileAccess.Write creator open would make
            // that authoritative no-WRITE lease incompatible on Windows.
            // Register its identity before closing it, so even a rename in the
            // tiny close-to-acquire interval is tracked and fails closed.
            output = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read | FileShare.Delete);
            using (FileStream input = File.OpenRead(source.Path))
            {
                input.CopyTo(output);
            }
            cancellationToken.ThrowIfCancellationRequested();
            output.Flush(flushToDisk: true);
            WindowsFileIdentity identity = WindowsFileIdentity.Capture(output.SafeFileHandle);
            record = journal.AddPublishedRecord(destinationPath, artifactRole, identity);
            output.Dispose();
            output = null;
            journal.AcquireRetainedHandle(record, faultHook, SourceProtocol.NonLive, failOnAcquisitionError: true);
            return record;
        }
        catch
        {
            // A copy can fail after CREATE_NEW has produced a partial output.
            // Capture ownership from the still-open creator handle and retain
            // it whenever possible; otherwise the journal keeps the path-only
            // identity record and rollback fails closed on NOT_FOUND/mismatch.
            if (record == null && output != null && !output.SafeFileHandle.IsInvalid)
            {
                try
                {
                    WindowsFileIdentity identity = WindowsFileIdentity.Capture(output.SafeFileHandle);
                    record = journal.AddPublishedRecord(destinationPath, artifactRole, identity);
                    output.Dispose();
                    output = null;
                    journal.AcquireRetainedHandle(record, faultHook, SourceProtocol.NonLive, failOnAcquisitionError: false);
                }
                catch
                {
                    // The original copy/cancellation error remains primary;
                    // the identity record, if already added, is still handled
                    // by the outer transaction rollback.
                }
            }
            throw;
        }
        finally
        {
            output?.Dispose();
        }
    }

    private sealed class CleanerTransactionJournal
    {
        public CleanerTransactionState State { get; private set; } = CleanerTransactionState.Initial;
        public string? StagingDir { get; set; }
        public List<TransactionOwnedObject> PublishedPaths { get; } = [];
        public List<TransactionOwnedObject> StagedPaths { get; } = [];
        public List<Exception> RollbackExceptions { get; } = [];

        /// <summary>
        /// One identity-owned filesystem object tracked by this transaction.
        /// The proof state is intentionally explicit: only exact deletion or
        /// an observed zero link count can complete cleanup.
        /// </summary>
        public sealed class TransactionOwnedObject
        {
            public enum CleanupProofState
            {
                Active,
                ExactDeleted,
                ProvenUnlinked,
                CleanupUnproven
            }

            public TransactionOwnedObject(
                string stagedPath,
                MediaArtifactKind artifactRole,
                WindowsFileIdentity identity,
                bool published)
            {
                StagedPath = stagedPath;
                ArtifactRole = artifactRole;
                Identity = identity;
                IsPublished = published;
                PublishedPath = published ? stagedPath : null;
            }

            public MediaArtifactKind ArtifactRole { get; }
            public string StagedPath { get; }
            public string? PublishedPath { get; private set; }
            public bool IsPublished { get; private set; }
            public WindowsFileIdentity Identity { get; }
            public SafeFileHandle? RetainedHandle { get; private set; }
            public bool HandleAcquisitionAttempted { get; private set; }
            public CleanupProofState ProofState { get; set; } = CleanupProofState.Active;
            public string CurrentPath => IsPublished && PublishedPath != null ? PublishedPath : StagedPath;

            public void SetPublishedPath(string path) => PublishedPath = path;
            public void MarkPublished() => IsPublished = true;
            public void MarkHandleAcquisitionAttempted() => HandleAcquisitionAttempted = true;
            public void RetainHandle(SafeFileHandle handle) => RetainedHandle = handle;
            public void ReleaseHandle()
            {
                RetainedHandle?.Dispose();
                RetainedHandle = null;
            }
        }

        // Failures recorded BEFORE Rollback() runs (e.g. a retained
        // exact-handle cleanup that could not prove deletion).  They are
        // permanent evidence: Rollback() clears its own transient working
        // list but seeds it with these entries first, so they can never be
        // wiped and always force RollbackFailed.
        private readonly List<Exception> _preRollbackFailures = [];

        public void SetState(CleanerTransactionState state) => State = state;

        public TransactionOwnedObject AddStagedRecord(
            string path,
            MediaArtifactKind artifactRole,
            WindowsFileIdentity identity)
        {
            TransactionOwnedObject record = new(path, artifactRole, identity, published: false);
            StagedPaths.Add(record);
            return record;
        }

        public TransactionOwnedObject AddPublishedRecord(
            string path,
            MediaArtifactKind artifactRole,
            WindowsFileIdentity identity)
        {
            TransactionOwnedObject record = new(path, artifactRole, identity, published: true);
            PublishedPaths.Add(record);
            return record;
        }

        public void RegisterPublishedRecord(TransactionOwnedObject record, string finalPath)
        {
            ArgumentNullException.ThrowIfNull(record);
            record.SetPublishedPath(finalPath);
            if (!PublishedPaths.Contains(record))
            {
                PublishedPaths.Add(record);
            }
        }

        public void MarkPublished(TransactionOwnedObject record)
        {
            ArgumentNullException.ThrowIfNull(record);
            record.MarkPublished();
            StagedPaths.Remove(record);
        }

        /// <summary>
        /// Acquires retained exact handles immediately after Native registry
        /// capture.  The normal path fails when acquisition is impossible; a
        /// failure-path capture records the proof gap and lets rollback surface
        /// it as CleanupUnproven instead of claiming a missing pathname is gone.
        /// </summary>
        public void AcquireRetainedHandles(
            Func<CleanerFailureStage, string?, Task>? faultHook,
            SourceProtocol protocol,
            bool failOnAcquisitionError)
        {
            foreach (TransactionOwnedObject record in StagedPaths.ToArray())
            {
                AcquireRetainedHandle(record, faultHook, protocol, failOnAcquisitionError);
            }
        }

        public void AcquireRetainedHandle(
            TransactionOwnedObject record,
            Func<CleanerFailureStage, string?, Task>? faultHook,
            SourceProtocol protocol,
            bool failOnAcquisitionError)
        {
            ArgumentNullException.ThrowIfNull(record);
            if (record.RetainedHandle is not null && !record.RetainedHandle.IsInvalid)
            {
                return;
            }

            // This seam runs before the path is converted to a retained handle,
            // allowing deterministic rename-away / injected-failure proofs.
            // Recovery after a failed first acquisition must not invoke the
            // seam a second time; the record carries that attempt state while
            // the original path/identity remains the only cleanup authority.
            bool firstAcquisitionAttempt = !record.HandleAcquisitionAttempted;
            record.MarkHandleAcquisitionAttempted();
            if (firstAcquisitionAttempt)
            {
                faultHook?.Invoke(
                    CleanerFailureStage.Staging,
                    HandleAcquisitionDetail(record, after: false)).GetAwaiter().GetResult();
            }

            SafeFileHandle handle;
            try
            {
                handle = WindowsOwnedFilePublisher.OpenOwnedStagedFile(record.StagedPath, record.Identity);
            }
            catch (Exception ex)
            {
                record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                if (!failOnAcquisitionError)
                {
                    return;
                }

                if (ex is CleanerException)
                {
                    throw;
                }

                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    $"Unable to retain exact ownership of staged {record.ArtifactRole} '{record.StagedPath}': {ex.Message}",
                    record.ArtifactRole,
                    ex);
            }

            // Assign before the after-acquisition seam.  If the seam renames
            // this object away and injects a failure, rollback still has exact
            // handle authority and never falls back to the pathname.
            record.RetainHandle(handle);
            faultHook?.Invoke(
                CleanerFailureStage.Staging,
                HandleAcquisitionDetail(record, after: true)).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Cleans a published object through its retained exact handle.  A
        /// failed disposition is successful only when that handle proves zero
        /// links; otherwise CleanupUnproven is permanent rollback evidence.
        /// </summary>
        public void CleanupRetainedObjectOrRecordFailure(TransactionOwnedObject record)
        {
            if (record.ProofState is TransactionOwnedObject.CleanupProofState.ExactDeleted or
                TransactionOwnedObject.CleanupProofState.ProvenUnlinked)
            {
                return;
            }

            SafeFileHandle? handle = record.RetainedHandle;
            if (handle is null || handle.IsInvalid)
            {
                record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                AddRollbackFailure(
                    $"Unable to delete transaction-owned {record.ArtifactRole} '{record.CurrentPath}' through its retained handle because no valid handle remains.");
                return;
            }

            try
            {
                if (WindowsOwnedFilePublisher.DeleteOwnedObject(handle))
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ExactDeleted;
                    // FileDispositionInfo marks the object delete-pending;
                    // release the final retained lease now so the exact object
                    // is unlinked before we inspect/remove its staging
                    // directory.  Exact deletion is already proven, so this
                    // does not weaken rollback authority.
                    record.ReleaseHandle();
                }
                else if (WindowsOwnedFilePublisher.IsObjectUnlinked(handle))
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                    record.ReleaseHandle();
                }
                else
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    AddRollbackFailure(
                        $"Unable to delete transaction-owned {record.ArtifactRole} '{record.CurrentPath}' through its retained handle; the object may still exist after a pathname takeover.");
                }
            }
            catch (Exception ex)
            {
                record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                AddRollbackFailure(
                    $"Unable to delete transaction-owned {record.ArtifactRole} '{record.CurrentPath}' through its retained handle: {ex.Message}");
            }
        }

        /// <summary>
        /// Records a rollback failure from outside the <see cref="Rollback"/>
        /// loop (e.g. an exact-handle cleanup that could not prove deletion of
        /// a transaction-owned object).  These entries are PERMANENT evidence:
        /// <see cref="Rollback"/> clears its own transient working list but
        /// seeds it with these pre-rollback failures first, so a retained
        /// exact-handle cleanup failure can never be wiped and always forces
        /// RollbackFailed — a false "RolledBack" can never be reported while
        /// a transaction-owned object may still exist.
        /// </summary>
        public void AddRollbackFailure(string message)
        {
            _preRollbackFailures.Add(new IOException(message));
        }

        public void Rollback(Func<CleanerFailureStage, string?, Task>? faultHook, SourceProtocol protocol)
        {
            State = CleanerTransactionState.RollingBack;
            RollbackExceptions.Clear();
            // Seed with permanent pre-rollback failures FIRST: entries added
            // by AddRollbackFailure (e.g. a retained exact-handle cleanup
            // that could not prove deletion of a transaction-owned object)
            // must survive this reset.  Otherwise the pathname fallback below
            // could observe NOT_FOUND at every recorded path and falsely
            // report RolledBack while the transaction-owned object is still
            // alive under another pathname.
            RollbackExceptions.AddRange(_preRollbackFailures);

            // A pre-rename publication record and its staged record may refer
            // to the same object. Process each object once, so a cleanup seam
            // has one observable hit and disposition is never duplicated.
            var records = new List<TransactionOwnedObject>();
            foreach (TransactionOwnedObject record in PublishedPaths.Concat(StagedPaths))
            {
                if (!records.Contains(record))
                {
                    records.Add(record);
                }
            }

            foreach (TransactionOwnedObject record in records)
            {
                if (record.ProofState is TransactionOwnedObject.CleanupProofState.ExactDeleted or
                    TransactionOwnedObject.CleanupProofState.ProvenUnlinked)
                {
                    continue;
                }

                try
                {
                    faultHook?.Invoke(CleanerFailureStage.Rollback, record.CurrentPath).GetAwaiter().GetResult();
                    DeleteExactObjectOrRecordFailure(record, RollbackExceptions);
                }
                catch (Exception ex)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    RollbackExceptions.Add(new IOException(
                        $"Failed to rollback transaction-owned {record.ArtifactRole} '{record.CurrentPath}': {ex.Message}", ex));
                }
            }

            // 3. Remove the staging directory only when it holds nothing but
            //    our own leftovers; foreign children are never deleted.
            if (!string.IsNullOrEmpty(StagingDir))
            {
                try
                {
                    faultHook?.Invoke(CleanerFailureStage.Rollback, StagingDir).GetAwaiter().GetResult();
                    RemoveStagingDirectoryIfOwned(StagingDir, RollbackExceptions);
                }
                catch (Exception ex)
                {
                    RollbackExceptions.Add(new IOException($"Failed to rollback staging directory '{StagingDir}': {ex.Message}", ex));
                }
            }

            if (RollbackExceptions.Count > 0)
            {
                State = CleanerTransactionState.RollbackFailed;
                throw new CleanerException(
                    CleanerFailureCategory.RollbackFailed,
                    CleanerFailureStage.Rollback,
                    protocol,
                    $"Rollback failed to clean up transient artifacts: {string.Join("; ", RollbackExceptions.Select(e => e.Message))}",
                    innerException: new AggregateException(RollbackExceptions));
            }

            State = CleanerTransactionState.RolledBack;
        }

        /// <summary>
        /// Deletes one owned object. A retained handle is always preferred.
        /// For a path-only record, NOT_FOUND is CleanupUnproven — there is no
        /// prior proof that the object was unlinked, so it must force
        /// RollbackFailed. Identity mismatch and all open/disposition errors
        /// are fail-closed and leave foreign objects untouched.
        /// </summary>
        private static void DeleteExactObjectOrRecordFailure(
            TransactionOwnedObject record,
            List<Exception> exceptions)
        {
            if (record.ProofState is TransactionOwnedObject.CleanupProofState.ExactDeleted or
                TransactionOwnedObject.CleanupProofState.ProvenUnlinked)
            {
                return;
            }

            SafeFileHandle? retainedHandle = record.RetainedHandle;
            if (retainedHandle is not null && !retainedHandle.IsInvalid)
            {
                try
                {
                    if (WindowsOwnedFilePublisher.DeleteOwnedObject(retainedHandle))
                    {
                        record.ProofState = TransactionOwnedObject.CleanupProofState.ExactDeleted;
                        // Make the proven delete visible to the staging-dir
                        // emptiness check.  Until this final lease closes,
                        // Windows may still enumerate the delete-pending name.
                        record.ReleaseHandle();
                    }
                    else if (WindowsOwnedFilePublisher.IsObjectUnlinked(retainedHandle))
                    {
                        record.ProofState = TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                        record.ReleaseHandle();
                    }
                    else
                    {
                        record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                        exceptions.Add(new IOException(
                            $"Unable to delete exact transaction-owned {record.ArtifactRole} '{record.CurrentPath}' through its retained handle; LinkCount remains positive or could not be verified."));
                    }
                }
                catch (Exception ex)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    exceptions.Add(new IOException(
                        $"Unable to delete exact transaction-owned {record.ArtifactRole} '{record.CurrentPath}' through its retained handle: {ex.Message}", ex));
                }
                return;
            }

            string path = record.CurrentPath;
            try
            {
                using SafeFileHandle handle = OpenForExactDelete(path);
                if (handle.IsInvalid)
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    exceptions.Add(new IOException(
                        $"Unable to prove cleanup of transaction-owned {record.ArtifactRole} '{path}' (Win32 error {win32Error}; including NOT_FOUND/PATH_NOT_FOUND). A missing pathname without prior proof is CleanupUnproven."));
                    return;
                }

                WindowsFileIdentity current = WindowsFileIdentity.Capture(handle);
                if (current.IsReparsePoint ||
                    current.VolumeSerialNumber != record.Identity.VolumeSerialNumber ||
                    current.FileIndex != record.Identity.FileIndex ||
                    current.LinkCount != 1)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    exceptions.Add(new IOException(
                        $"Refusing to delete '{path}': the filesystem object no longer matches the identity this transaction captured (foreign-object protection)."));
                    return;
                }

                var disposition = new FileDispositionInfo { DeleteFile = 1 };
                if (SetFileInformationByHandle(
                        handle,
                        FileDispositionInfoClass,
                        ref disposition,
                        (uint)Marshal.SizeOf<FileDispositionInfo>()))
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ExactDeleted;
                }
                else if (WindowsFileIdentity.Capture(handle).LinkCount == 0)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                }
                else
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    exceptions.Add(new IOException(
                        $"Unable to delete exact transaction-owned {record.ArtifactRole} '{path}': Win32 error {Marshal.GetLastWin32Error()} and LinkCount remains positive."));
                }
            }
            catch (Exception ex)
            {
                record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                exceptions.Add(new IOException($"Refusing to delete '{path}': {ex.Message}", ex));
            }
        }

        public void DisposeOwnedHandles()
        {
            var records = new List<TransactionOwnedObject>();
            foreach (TransactionOwnedObject record in PublishedPaths.Concat(StagedPaths))
            {
                if (!records.Contains(record))
                {
                    records.Add(record);
                }
            }

            foreach (TransactionOwnedObject record in records)
            {
                record.ReleaseHandle();
            }
        }

        private static string HandleAcquisitionDetail(TransactionOwnedObject record, bool after)
        {
            if (record.ArtifactRole == MediaArtifactKind.PrimaryImage)
            {
                return after ? "ImageHandleAcquired" : "BeforeImageHandleAcquisition";
            }

            if (record.ArtifactRole == MediaArtifactKind.MotionVideo)
            {
                return after ? "VideoHandleAcquired" : "BeforeVideoHandleAcquisition";
            }

            return after ? "AuxiliaryHandleAcquired" : "BeforeAuxiliaryHandleAcquisition";
        }

        private static SafeFileHandle OpenForExactDelete(string path)
        {
            return CreateFileForDelete(
                path,
                DeleteAccess,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                OpenReparsePoint,
                IntPtr.Zero);
        }

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileForDelete(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            SafeFileHandle hFile,
            int fileInformationClass,
            ref FileDispositionInfo fileInformation,
            uint bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInfo
        {
            public byte DeleteFile;
        }

        private const uint DeleteAccess = 0x00010000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint FileShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint OpenReparsePoint = 0x00200000;
        private const int FileDispositionInfoClass = 4;

        /// <summary>
        /// Removes the staging directory only if it is empty.  Any remaining
        /// entry is treated as foreign and left in place (recorded), so a
        /// recursive delete can never destroy an injected foreign child.
        /// </summary>
        private static void RemoveStagingDirectoryIfOwned(string dir, List<Exception> exceptions)
        {
            if (!Directory.Exists(dir))
            {
                return;
            }

            try
            {
                string[] leftover = Directory.EnumerateFileSystemEntries(dir).ToArray();
                if (leftover.Length > 0)
                {
                    exceptions.Add(new IOException(
                        $"Staging directory '{dir}' still contains entries not owned by this transaction; leaving them in place. Entries: {string.Join(", ", leftover.Select(e => Path.GetFileName(e)))}"));
                    return;
                }

                Directory.Delete(dir, recursive: false);
            }
            catch (Exception ex)
            {
                exceptions.Add(new IOException($"Unable to remove staging directory '{dir}': {ex.Message}", ex));
            }
        }
    }

    private static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (Directory.Exists(dir))
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    System.Diagnostics.Debug.WriteLine($"Staging directory '{dir}' left in place: it still contains entries not owned by this transaction.");
                    return;
                }
                Directory.Delete(dir, recursive: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete staging directory '{dir}': {ex.Message}");
        }
    }
}
