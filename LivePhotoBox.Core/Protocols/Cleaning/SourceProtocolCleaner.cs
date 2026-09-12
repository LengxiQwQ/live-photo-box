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
                return await ExecuteNonLiveNoOpAsync(bundle, workspace, journal, sw, cancellationToken).ConfigureAwait(false);
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
                    ApplyNativeStagedOwnership(planAttempt, journal, stagedImgPath, stagedVidPath);
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
                CaptureNativeStagedOutputs(cleanContextHandle, cleanUseHarness, journal);
                throw;
            }
            catch (Exception ex)
            {
                CaptureNativeStagedOutputs(cleanContextHandle, cleanUseHarness, journal);
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

            journal.SetState(CleanerTransactionState.Committing);
            try
            {
                // No-overwrite publish: a destination that already exists is a
                // foreign object and the publish must fail closed.  The exact
                // object identity of every file we move is captured BEFORE the
                // move, from the staging object this transaction created.
                // Moving it (same-volume rename) preserves the exact filesystem
                // object, so rollback verifies against that pre-move identity
                // and never re-derives ownership from the destination pathname
                // after the move (no Move -> Capture window).
                // Ownership comes from the Native registry captured at staging
                // time.  A pre-move re-capture double-checks the object at the
                // path is still exactly that object (same File ID) before the
                // move; ownership is never re-derived from the destination
                // pathname after the move.
                WindowsFileIdentity stagedImgIdentity = RequireRegisteredStagedIdentity(journal, stagedImgPath);
                VerifyExactObjectAtPath(stagedImgPath, stagedImgIdentity);
                File.Move(stagedImgPath, cleanImgPath);
                journal.PublishedPaths.Add(new PublishRecord(cleanImgPath, stagedImgIdentity));

                if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "ImagePublished").ConfigureAwait(false);

                if (stagedVidPath != null && cleanVidPath != null)
                {
                    WindowsFileIdentity stagedVidIdentity = RequireRegisteredStagedIdentity(journal, stagedVidPath);
                    VerifyExactObjectAtPath(stagedVidPath, stagedVidIdentity);
                    File.Move(stagedVidPath, cleanVidPath);
                    journal.PublishedPaths.Add(new PublishRecord(cleanVidPath, stagedVidIdentity));
                }

                journal.SetState(CleanerTransactionState.Committed);
            }
            catch (Exception ex)
            {
                throw new CleanerException(
                    CleanerFailureCategory.PublishFailed,
                    CleanerFailureStage.Commit,
                    facts.Protocol,
                    $"Failed to publish cleaned bundle to destination paths: {ex.Message}",
                    innerException: ex);
            }
            finally
            {
                if (journal.State == CleanerTransactionState.Committed)
                {
                    TryDeleteDirectory(stagingDir);
                    journal.StagingDir = null;
                }
            }

            // -------------------------------------------------------------
            // Step 10: Emit Evidence
            // -------------------------------------------------------------
            var cleanImgArtifact = new MediaArtifact
            {
                Path = cleanImgPath,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = bundle.PrimaryImage.MimeType,
                ImageContainer = bundle.PrimaryImage.ImageContainer,
                ImageCodec = bundle.PrimaryImage.ImageCodec,
                ByteLength = new FileInfo(cleanImgPath).Length,
                Sha256 = await workspace.ComputeFileSha256Async(cleanImgPath, cancellationToken).ConfigureAwait(false),
                FileIdentity = WindowsFileIdentity.Capture(cleanImgPath)
            };

            MediaArtifact? cleanVidArtifact = null;
            if (cleanVidPath != null && File.Exists(cleanVidPath))
            {
                cleanVidArtifact = new MediaArtifact
                {
                    Path = cleanVidPath,
                    Kind = MediaArtifactKind.MotionVideo,
                    MimeType = bundle.MotionVideo!.MimeType,
                    VideoContainer = bundle.MotionVideo.VideoContainer,
                    VideoCodec = bundle.MotionVideo.VideoCodec,
                    ByteLength = new FileInfo(cleanVidPath).Length,
                    Sha256 = await workspace.ComputeFileSha256Async(cleanVidPath, cancellationToken).ConfigureAwait(false),
                    FileIdentity = WindowsFileIdentity.Capture(cleanVidPath)
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
            // plans remain owned by the caller.
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
        CleanerTransactionJournal journal)
    {
        if (contextHandle == nint.Zero)
        {
            return;
        }

        try
        {
            IReadOnlyList<NativeCleanService.CleanStagedOutputRecord> native =
                NativeCleanService.QueryStagedOutputs(contextHandle, useHarnessLibrary);
            foreach (var rec in native)
            {
                AddNativeStagedRecord(journal, rec);
            }
        }
        catch
        {
            // Ownership registry is best-effort on the failure path; the
            // registry entries that were already read remain authoritative.
        }
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
        string? stagedVidPath)
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
    }

    private static void AddNativeStagedRecord(CleanerTransactionJournal journal, NativeCleanService.CleanStagedOutputRecord rec)
    {
        if (string.IsNullOrEmpty(rec.FinalPath))
        {
            return;
        }
        if (journal.StagedPaths.Any(r => PathEquals(r.Path, rec.FinalPath)))
        {
            return;
        }
        var identity = new WindowsFileIdentity
        {
            VolumeSerialNumber = rec.VolumeSerial,
            FileIndex = rec.FileIndex,
            LinkCount = rec.LinkCount,
            FileAttributes = 0
        };
        journal.StagedPaths.Add(new StagedRecord(rec.FinalPath, identity));
    }

    private static bool PathEquals(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static WindowsFileIdentity RequireRegisteredStagedIdentity(CleanerTransactionJournal journal, string path)
    {
        StagedRecord? rec = journal.StagedPaths.FirstOrDefault(r => PathEquals(r.Path, path));
        if (rec is null)
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"No native-registered ownership for staged '{path}'; refusing to publish an object this transaction did not prove it created.");
        }
        return rec.Identity;
    }

    private static void VerifyExactObjectAtPath(string path, WindowsFileIdentity expected)
    {
        WindowsFileIdentity current = WindowsFileIdentity.Capture(path);
        if (current.IsReparsePoint ||
            current.VolumeSerialNumber != expected.VolumeSerialNumber ||
            current.FileIndex != expected.FileIndex)
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Staged object at '{path}' no longer matches the identity this transaction registered; refusing to publish a foreign object.");
        }
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
        CancellationToken cancellationToken)
    {
        journal.SetState(CleanerTransactionState.Staging);

        string imgExt = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".heic" : ".jpg";
        string cleanImgPath = workspace.AllocateFilePath("clean-img", imgExt);

        // Create the published copy with the handle held open so identity is
        // captured from the object we just wrote, not re-guessed from the
        // pathname afterwards.  FileMode.CreateNew keeps the publish
        // no-overwrite: a pre-existing destination fails closed.
        WindowsFileIdentity cleanImgIdentity;
        using (FileStream imgStream = new FileStream(cleanImgPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using (FileStream srcStream = File.OpenRead(bundle.PrimaryImage.Path))
            {
                srcStream.CopyTo(imgStream);
            }
            imgStream.Flush(flushToDisk: true);
            cleanImgIdentity = WindowsFileIdentity.Capture(imgStream.SafeFileHandle);
        }
        journal.PublishedPaths.Add(new PublishRecord(cleanImgPath, cleanImgIdentity));

        string? cleanVidPath = null;
        WindowsFileIdentity? cleanVidIdentity = null;
        if (bundle.MotionVideo != null)
        {
            string vidExt = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
            cleanVidPath = workspace.AllocateFilePath("clean-vid", vidExt);
            using (FileStream vidStream = new FileStream(cleanVidPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (FileStream srcStream = File.OpenRead(bundle.MotionVideo.Path))
                {
                    srcStream.CopyTo(vidStream);
                }
                vidStream.Flush(flushToDisk: true);
                cleanVidIdentity = WindowsFileIdentity.Capture(vidStream.SafeFileHandle);
            }
            journal.PublishedPaths.Add(new PublishRecord(cleanVidPath, cleanVidIdentity));
        }

        var cleanImgArtifact = bundle.PrimaryImage with
        {
            Path = cleanImgPath,
            ByteLength = new FileInfo(cleanImgPath).Length,
            Sha256 = await workspace.ComputeFileSha256Async(cleanImgPath, cancellationToken).ConfigureAwait(false),
            FileIdentity = cleanImgIdentity
        };

        MediaArtifact? cleanVidArtifact = null;
        if (cleanVidPath != null && bundle.MotionVideo != null)
        {
            cleanVidArtifact = bundle.MotionVideo with
            {
                Path = cleanVidPath,
                ByteLength = new FileInfo(cleanVidPath).Length,
                Sha256 = await workspace.ComputeFileSha256Async(cleanVidPath, cancellationToken).ConfigureAwait(false),
                FileIdentity = cleanVidIdentity
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

    private sealed class CleanerTransactionJournal
    {
        public CleanerTransactionState State { get; private set; } = CleanerTransactionState.Initial;
        public string? StagingDir { get; set; }
        public List<PublishRecord> PublishedPaths { get; } = [];
        public List<StagedRecord> StagedPaths { get; } = [];
        public List<Exception> RollbackExceptions { get; } = [];

        public void SetState(CleanerTransactionState state) => State = state;

        public void Rollback(Func<CleanerFailureStage, string?, Task>? faultHook, SourceProtocol protocol)
        {
            State = CleanerTransactionState.RollingBack;
            RollbackExceptions.Clear();

            // 1. Delete published artifacts - but only the exact object we published.
            foreach (var rec in PublishedPaths)
            {
                try
                {
                    faultHook?.Invoke(CleanerFailureStage.Rollback, rec.Path).GetAwaiter().GetResult();
                    DeleteExactObjectOrRecordFailure(rec.Path, rec.Identity, RollbackExceptions);
                }
                catch (Exception ex)
                {
                    RollbackExceptions.Add(new IOException($"Failed to rollback published artifact '{rec.Path}': {ex.Message}", ex));
                }
            }

            // 2. Delete staged files we created - never foreign children.
            foreach (var rec in StagedPaths)
            {
                try
                {
                    faultHook?.Invoke(CleanerFailureStage.Rollback, rec.Path).GetAwaiter().GetResult();
                    DeleteExactObjectOrRecordFailure(rec.Path, rec.Identity, RollbackExceptions);
                }
                catch (Exception ex)
                {
                    RollbackExceptions.Add(new IOException($"Failed to rollback staged artifact '{rec.Path}': {ex.Message}", ex));
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
        /// Deletes <paramref name="path"/> only when the current filesystem
        /// object is exactly the one captured in <paramref name="expected"/>
        /// (volume serial + file index, single link, no reparse point).  A
        /// replaced / swapped / hard-linked / reparse object is left untouched
        /// and the failure is recorded instead.
        /// </summary>
        private static void DeleteExactObjectOrRecordFailure(
            string path,
            WindowsFileIdentity expected,
            List<Exception> exceptions)
        {
            try
            {
                // Open the object WITHOUT resolving a possible reparse point, keep
                // the handle, verify the identity on that same handle, and delete
                // through SetFileInformationByHandle(FileDispositionInfo).  There
                // is no check-then-close-then-delete-by-pathname window in which
                // a foreign object could take over the path: what we delete is
                // exactly the object we verified.
                using SafeFileHandle handle = OpenForExactDelete(path);
                if (handle.IsInvalid)
                {
                    // Already gone or unreachable.  Whatever now occupies the path
                    // (if anything) is not the object we verified, so it must stay.
                    return;
                }

                WindowsFileIdentity current = WindowsFileIdentity.Capture(handle);
                if (current.IsReparsePoint ||
                    current.VolumeSerialNumber != expected.VolumeSerialNumber ||
                    current.FileIndex != expected.FileIndex ||
                    current.LinkCount != 1)
                {
                    exceptions.Add(new IOException(
                        $"Refusing to delete '{path}': the filesystem object no longer matches the identity this transaction captured (foreign-object protection)."));
                    return;
                }

                var disposition = new FileDispositionInfo { DeleteFile = 1 };
                if (!SetFileInformationByHandle(
                        handle,
                        FileDispositionInfoClass,
                        ref disposition,
                        (uint)Marshal.SizeOf<FileDispositionInfo>()))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    exceptions.Add(new IOException(
                        $"Unable to delete exact object '{path}': Win32 error {win32Error}."));
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(new IOException($"Refusing to delete '{path}': {ex.Message}", ex));
            }
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

    /// <summary>Published (committed) destination object and its captured identity.</summary>
    private sealed record PublishRecord(string Path, WindowsFileIdentity Identity);

    /// <summary>Staged file created by this transaction and its captured identity.</summary>
    private sealed record StagedRecord(string Path, WindowsFileIdentity Identity);

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
