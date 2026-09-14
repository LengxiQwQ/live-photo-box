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
        var journal = new CleanerTransactionJournal(workspace.RootDirectory);
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
            var stagingOwnership = new CleanerTransactionJournal.StagingDirectoryOwnership(stagingDir);
            // The staging directory is a transaction-owned object.  It is
            // created and claimed in ONE atomic kernel transition
            // (NtCreateFile + FILE_CREATE + FILE_DIRECTORY_FILE): the exact
            // identity is captured from the CREATING handle, so there is no
            // create -> reopen(pathname) window in which a foreign directory
            // could take over the pathname and be claimed by identity capture
            // (same pathname != same object).  The same handle is then
            // retained until the directory object is exact-cleaned, so the
            // transaction always holds object authority, never pathname
            // authority, over its staging directory.
            try
            {
                SafeFileHandle creationDirectoryHandle = WindowsOwnedFilePublisher.CreateOwnedDirectory(stagingDir);
                stagingOwnership.SetIdentity(WindowsFileIdentity.Capture(creationDirectoryHandle));
                stagingOwnership.RetainHandle(creationDirectoryHandle);
            }
            catch (Exception ex)
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    facts.Protocol,
                    $"Staging directory '{stagingDir}' could not be atomically created and claimed as a transaction-owned object: {ex.Message}",
                    innerException: ex);
            }
            journal.StagingDirectory = stagingOwnership;

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
                        stagingDir,
                        stagedImgPath,
                        stagedVidPath,
                        FaultInjectionHook,
                        facts.Protocol);

                    // Native staging is complete and every required staged
                    // artifact now has an exact retained handle.  Establish
                    // the second, strict directory lease before ANY
                    // preservation/media/post-clean validator opens a staged
                    // pathname.  The creator/ownership handle must keep
                    // sharing WRITE for Native staging; this lease does not,
                    // so a foreign writer cannot replace the namespace entry
                    // that the pathname-based validators will resolve.
                    journal.AcquireValidationNamespaceLease(facts.Protocol);
                    if (FaultInjectionHook != null)
                    {
                        await FaultInjectionHook(
                            CleanerFailureStage.Staging,
                            "ValidationNamespaceLeaseAcquired").ConfigureAwait(false);
                    }
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
                    stagingDir,
                    stagedImgPath,
                    stagedVidPath,
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
                    stagingDir,
                    stagedImgPath,
                    stagedVidPath,
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

            // The staging-directory handle is retained from atomic creation
            // through this whole commit and is NOT released mid-transaction:
            // the transaction never drops its strongest ownership authority
            // over the directory object, so a commit-window adversarial seam
            // stays observable for the same reasons a real external actor's
            // file-level operations do (the directory lease allows files to
            // move OUT, which is exactly what publish does).
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

                    // Deterministic adversarial seam (video counterpart): the
                    // published video object's handle is still open and the
                    // bundle is not yet committed.
                    if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "VideoPublished").ConfigureAwait(false);
                }

                // The staging directory is a transaction-owned object, so its
                // cleanup must be PROVEN before the transaction may report
                // Committed.  While the directory object still exists anywhere
                // (including renamed-away), SetState(Committed) would be a
                // false success: PathMissing != DirectoryGone.  The directory
                // handle retained since atomic creation deletes the exact
                // directory object and fails closed when the directory is
                // non-empty (foreign children are never deleted) or the
                // disposition cannot be armed.  If the cleanup cannot be
                // proven, the commit fails and rollback still holds the
                // exact image/video handles, so it can safely roll back.
                // The namespace must be mutable again before the exact
                // directory-delete proof is attempted. Publishing moved the
                // owned files out; retaining this no-WRITE lease would make
                // directory cleanup itself sharing-sensitive.
                journal.ReleaseValidationNamespaceLease();
                if (!journal.TryDeleteOwnedDirectory(out Exception? directoryFailure))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.PublishFailed,
                        CleanerFailureStage.Commit,
                        facts.Protocol,
                        $"Staging directory cleanup could not be proven before commit: {directoryFailure?.Message}",
                        innerException: directoryFailure);
                }
                journal.StagingDirectory = null;

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
                journal.ReleaseValidationNamespaceLease();
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
                journal.ReleaseValidationNamespaceLease();
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
                // The staging directory cleanup is a hard pre-commit gate
                // (above), so there is deliberately no best-effort
                // TryDeleteDirectory here: a directory whose cleanup could
                // not be proven before commit already failed the commit and
                // flows through rollback, which fails closed.
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
            journal.ReleaseValidationNamespaceLease();
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
            journal.ReleaseValidationNamespaceLease();
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
            journal.ReleaseValidationNamespaceLease();
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
        string stagingDir,
        string stagedImgPath,
        string? stagedVidPath,
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

        try
        {
            ValidateNativeStagedRegistry(
                native,
                stagingDir,
                stagedImgPath,
                stagedVidPath,
                protocol,
                requireExpectedOutputs: false);
        }
        catch (CleanerException ex)
        {
            // A malformed or ambiguous failure-path registry is itself a
            // proof gap.  Never let the subsequent directory cleanup observe
            // an empty namespace and declare the transaction RolledBack.
            journal.AddRollbackFailure(
                $"Native staged-output ownership registry could not be reconciled after the staging failure; rollback ownership is unproven: {ex.Message}");
            return;
        }

        foreach (var rec in native)
        {
            // Recovery can observe the same Native registry entry that was
            // already admitted before the staging invoker cancelled.  That
            // exact replay is reconciliation, not a second ownership claim;
            // only the failure path may reconcile it, and only when the
            // path/role/FileId identity is byte-for-byte the same.  Normal
            // admission below keeps duplicate records fail-closed.
            AddNativeStagedRecord(journal, rec, allowExactReconcile: true);
        }

        // Convert every registry identity to a retained, identity-verified
        // Managed handle before the failure escapes the staging boundary.  A
        // failure-path query may observe a pathname that was already renamed or
        // replaced; retain the record as unproven and let rollback fail closed
        // rather than treating a missing pathname as proof of cleanup.
        journal.AcquireRetainedHandles(faultHook, protocol, failOnAcquisitionError: false);

        // The staging-directory handle was already retained at atomic
        // creation; nothing further to acquire on the failure path.
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
        string stagingDir,
        string stagedImgPath,
        string? stagedVidPath,
        Func<CleanerFailureStage, string?, Task>? faultHook,
        SourceProtocol protocol)
    {
        ArgumentNullException.ThrowIfNull(planAttempt);
        IReadOnlyList<NativeCleanService.CleanStagedOutputRecord> native;
        try
        {
            native = NativeCleanService.QueryStagedOutputs(
                planAttempt.ContextLease.Handle,
                planAttempt.UseHarnessLibrary);
        }
        catch (Exception ex)
        {
            // A registry read failure leaves Native ownership unknown.  Keep
            // this evidence permanently attached to the transaction so a
            // later empty staging directory cannot turn an untracked,
            // renamed-away Native object into a false RolledBack result.
            journal.AddRollbackFailure(
                $"Unable to read Native staged-output ownership registry after a successful staging operation; rollback ownership is unproven: {ex.Message}");
            throw;
        }

        // The registry is a security boundary, not a best-effort list.  It
        // must contain exactly the output role/path pairs this invocation
        // requested.  In particular, duplicate records, shadow paths,
        // unexpected roles, and records outside this transaction's staging
        // directory are rejected before any record is turned into cleanup or
        // publication authority.
        try
        {
            ValidateNativeStagedRegistry(
                native,
                stagingDir,
                stagedImgPath,
                stagedVidPath,
                protocol,
                requireExpectedOutputs: true);
        }
        catch (CleanerException ex)
        {
            // Missing, malformed, shadowed, or otherwise ambiguous registry
            // data cannot be treated as proof that no owned output exists.
            // Preserve the failure across Rollback()'s transient exception
            // reset, even if a foreign actor has already moved the output
            // away and left the staging directory empty.
            journal.AddRollbackFailure(
                $"Native staged-output ownership registry was not admissible; rollback ownership is unproven: {ex.Message}");
            throw;
        }

        foreach (var rec in native)
        {
            AddNativeStagedRecord(journal, rec);
        }

        // This is intentionally part of the immediate post-Native capture
        // boundary.  Preservation, validation, post-clean inspection and final
        // commit must all use these same retained exact handles.
        journal.AcquireRetainedHandles(faultHook, protocol, failOnAcquisitionError: true);

        // The staging directory handle was already retained at atomic
        // creation (identity captured from the creating handle); it stays
        // retained until the directory object is exact-cleaned before commit.
    }

    private static CleanerTransactionJournal.TransactionOwnedObject AddNativeStagedRecord(
        CleanerTransactionJournal journal,
        NativeCleanService.CleanStagedOutputRecord rec,
        bool allowExactReconcile = false)
    {
        if (string.IsNullOrEmpty(rec.FinalPath))
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Staging,
                SourceProtocol.Unknown,
                "Native cleaner reported an owned staged output without a path.");
        }

        if (rec.ArtifactRole is not (MediaArtifactKind.PrimaryImage or MediaArtifactKind.MotionVideo) ||
            rec.AuxiliaryIndex != uint.MaxValue ||
            rec.VolumeSerial == 0 ||
            rec.FileIndex == 0 ||
            rec.LinkCount == 0)
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Staging,
                SourceProtocol.Unknown,
                $"Native cleaner reported malformed staged-output ownership for '{rec.FinalPath}'; refusing to claim it.");
        }

        CleanerTransactionJournal.TransactionOwnedObject? existing =
            journal.StagedPaths.FirstOrDefault(r => PathEquals(r.StagedPath, rec.FinalPath));
        if (existing != null)
        {
            if (allowExactReconcile &&
                existing.ArtifactRole == rec.ArtifactRole &&
                existing.Identity.VolumeSerialNumber == rec.VolumeSerial &&
                existing.Identity.FileIndex == rec.FileIndex &&
                existing.Identity.LinkCount == rec.LinkCount)
            {
                // A failure-path registry query is allowed to replay the
                // exact record admitted immediately before cancellation.  Do
                // not create a second journal object: rollback must retain a
                // single cleanup authority and a single acquisition state.
                return existing;
            }

            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Staging,
                SourceProtocol.Unknown,
                $"Native cleaner reported duplicate staged-output ownership for '{rec.FinalPath}'; refusing ambiguous cleanup authority.");
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

    /// <summary>
    /// Validates the Native staged-output registry before any record can enter
    /// the transaction journal.  The registry is expected to contain only the
    /// primary-image and optional motion-video outputs of this invocation;
    /// auxiliary/shadow records have no corresponding publication authority in
    /// this cleaner call and therefore fail closed.  Paths are normalized and
    /// required to be exact children of the atomically-created staging
    /// namespace, not merely strings that happen to share a prefix.
    /// </summary>
    internal static void ValidateNativeStagedRegistry(
        IReadOnlyList<NativeCleanService.CleanStagedOutputRecord> native,
        string stagingDir,
        string stagedImgPath,
        string? stagedVidPath,
        SourceProtocol protocol,
        bool requireExpectedOutputs)
    {
        ArgumentNullException.ThrowIfNull(native);

        string normalizedStagingDir;
        var expected = new Dictionary<string, MediaArtifactKind>(StringComparer.OrdinalIgnoreCase);
        try
        {
            normalizedStagingDir = Path.GetFullPath(stagingDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            expected.Add(Path.GetFullPath(stagedImgPath), MediaArtifactKind.PrimaryImage);
            if (stagedVidPath != null)
            {
                expected.Add(Path.GetFullPath(stagedVidPath), MediaArtifactKind.MotionVideo);
            }
        }
        catch (Exception ex)
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Staging,
                protocol,
                "Unable to normalize the Native staged-output namespace; refusing to trust registry paths.",
                innerException: ex);
        }

        string namespacePrefix = normalizedStagingDir + Path.DirectorySeparatorChar;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenRoles = new HashSet<MediaArtifactKind>();

        foreach (NativeCleanService.CleanStagedOutputRecord rec in native)
        {
            if (string.IsNullOrWhiteSpace(rec.FinalPath))
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    "Native cleaner reported a staged-output registry record without a path.");
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(rec.FinalPath);
            }
            catch (Exception ex)
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    $"Native cleaner reported an invalid staged-output path '{rec.FinalPath}'; refusing registry authority.",
                    innerException: ex);
            }

            if (!normalizedPath.StartsWith(namespacePrefix, StringComparison.OrdinalIgnoreCase) ||
                !seenPaths.Add(normalizedPath))
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    $"Native cleaner reported a staged-output path outside the exact staging namespace or a duplicate path: '{rec.FinalPath}'.");
            }

            if (rec.ArtifactRole is not (MediaArtifactKind.PrimaryImage or MediaArtifactKind.MotionVideo) ||
                rec.AuxiliaryIndex != uint.MaxValue ||
                rec.VolumeSerial == 0 ||
                rec.FileIndex == 0 ||
                rec.LinkCount == 0)
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    $"Native cleaner reported malformed staged-output identity/role metadata for '{rec.FinalPath}'; refusing registry authority.");
            }

            if (!expected.TryGetValue(normalizedPath, out MediaArtifactKind expectedRole) ||
                expectedRole != rec.ArtifactRole ||
                !seenRoles.Add(rec.ArtifactRole))
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    $"Native cleaner staged-output registry does not match the expected role/path binding for '{rec.FinalPath}'; refusing shadow or ambiguous authority.");
            }
        }

        if (requireExpectedOutputs)
        {
            foreach (KeyValuePair<string, MediaArtifactKind> pair in expected)
            {
                if (!seenPaths.Contains(pair.Key))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.OutputCreateFailed,
                        CleanerFailureStage.Staging,
                        protocol,
                        $"Native cleaner did not report ownership of expected staged {pair.Value} '{pair.Key}'; refusing pathname-only ownership.");
                }
            }
        }
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
        string stagingDir = Path.Combine(workspace.RootDirectory, "staging_" + Guid.NewGuid().ToString("N"));
        var stagingOwnership = new CleanerTransactionJournal.StagingDirectoryOwnership(stagingDir);

        // NonLive is still a transaction, not an untracked copy shortcut.  The
        // staging directory is atomically created and claimed from its creating
        // handle, exactly like the destructive Native path.  This gives the
        // no-op path the same directory ownership and cleanup proof contract.
        try
        {
            SafeFileHandle creationDirectoryHandle = WindowsOwnedFilePublisher.CreateOwnedDirectory(stagingDir);
            stagingOwnership.SetIdentity(WindowsFileIdentity.Capture(creationDirectoryHandle));
            stagingOwnership.RetainHandle(creationDirectoryHandle);
        }
        catch (Exception ex)
        {
            throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.Staging,
                SourceProtocol.NonLive,
                $"NonLive staging directory '{stagingDir}' could not be atomically created and claimed: {ex.Message}",
                innerException: ex);
        }
        journal.StagingDirectory = stagingOwnership;

        string stagedImgPath = Path.Combine(stagingDir, "stage-img" + imgExt);

        // A no-op copy is still a transaction-owned output.  Record and retain
        // an exact handle before any later validation/cancellation/failure
        // point, so a pathname that goes missing cannot be mistaken for proof
        // of cleanup.
        CleanerTransactionJournal.TransactionOwnedObject imgRecord =
            CopyNonLiveArtifact(
                bundle.PrimaryImage,
                stagedImgPath,
                journal,
                MediaArtifactKind.PrimaryImage,
                faultHook,
                cancellationToken);

        string? stagedVidPath = null;
        CleanerTransactionJournal.TransactionOwnedObject? vidRecord = null;
        if (bundle.MotionVideo != null)
        {
            string vidExt = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
            stagedVidPath = Path.Combine(stagingDir, "stage-vid" + vidExt);
            vidRecord = CopyNonLiveArtifact(
                bundle.MotionVideo,
                stagedVidPath,
                journal,
                MediaArtifactKind.MotionVideo,
                faultHook,
                cancellationToken);
        }

        // The copy phase has finished and both exact file handles are held.
        // Acquire the second directory handle only now.  It denies
        // FILE_SHARE_WRITE, freezing the namespace for the validation-to-
        // publication window.  Any path-based test seam or future validator
        // in this NonLive path is therefore tied to the same namespace whose
        // exact objects the retained handles represent.
        journal.AcquireValidationNamespaceLease(SourceProtocol.NonLive);
        if (faultHook != null)
        {
            await faultHook(
                CleanerFailureStage.Staging,
                "ValidationNamespaceLeaseAcquired").ConfigureAwait(false);
        }

        // This path has no protocol mutation to inspect, but it still needs a
        // real validation/evidence boundary.  The evidence below is captured
        // from the retained exact handles, never from a pathname re-open.
        if (faultHook != null) await faultHook(CleanerFailureStage.PreservationDiff, null).ConfigureAwait(false);
        if (faultHook != null) await faultHook(CleanerFailureStage.MediaValidation, null).ConfigureAwait(false);
        if (faultHook != null) await faultHook(CleanerFailureStage.PostCleanInspection, "BeforeInspect").ConfigureAwait(false);

        SafeFileHandle imgHandle = imgRecord.RetainedHandle
            ?? throw new CleanerException(
                CleanerFailureCategory.OutputCreateFailed,
                CleanerFailureStage.PostCleanInspection,
                SourceProtocol.NonLive,
                "NonLive staged image has no retained exact ownership handle; refusing pathname-only validation/publish.",
                MediaArtifactKind.PrimaryImage);
        PublishedOwnedFile imgEvidence = WindowsOwnedFilePublisher.CaptureOwnedHandleEvidence(
            imgHandle,
            stagedImgPath,
            imgRecord.Identity,
            cancellationToken);

        PublishedOwnedFile? vidEvidence = null;
        if (vidRecord != null && stagedVidPath != null)
        {
            SafeFileHandle vidHandle = vidRecord.RetainedHandle
                ?? throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.PostCleanInspection,
                    SourceProtocol.NonLive,
                    "NonLive staged video has no retained exact ownership handle; refusing pathname-only validation/publish.",
                    MediaArtifactKind.MotionVideo);
            vidEvidence = WindowsOwnedFilePublisher.CaptureOwnedHandleEvidence(
                vidHandle,
                stagedVidPath,
                vidRecord.Identity,
                cancellationToken);
        }

        bool imgLengthMatch = bundle.PrimaryImage.ByteLength <= 0 ||
            imgEvidence.ByteLength == bundle.PrimaryImage.ByteLength;
        bool imgMatch = imgLengthMatch &&
            string.Equals(bundle.PrimaryImage.Sha256, imgEvidence.Sha256, StringComparison.OrdinalIgnoreCase);
        bool vidLengthMatch = bundle.MotionVideo == null ||
            bundle.MotionVideo.ByteLength <= 0 ||
            (vidEvidence != null && vidEvidence.ByteLength == bundle.MotionVideo.ByteLength);
        bool vidMatch = bundle.MotionVideo == null ||
            (vidEvidence != null &&
             vidLengthMatch &&
             string.Equals(bundle.MotionVideo.Sha256, vidEvidence.Sha256, StringComparison.OrdinalIgnoreCase));

        if (!imgMatch || !vidMatch)
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.PostCleanInspection,
                SourceProtocol.NonLive,
                $"NonLive verbatim copy failed same-handle evidence verification (image match: {imgMatch}, video match: {vidMatch}).");
        }

        journal.SetState(CleanerTransactionState.Validated);
        if (faultHook != null)
        {
            await faultHook(CleanerFailureStage.Commit, "BeforePublish").ConfigureAwait(false);
        }

        string cleanImgPath = workspace.AllocateFilePath("clean-img", imgExt);
        string? cleanVidPath = null;
        if (stagedVidPath != null)
        {
            cleanVidPath = workspace.AllocateFilePath("clean-vid", Path.GetExtension(stagedVidPath));
        }

        PublishedOwnedFile? imgPublished = null;
        PublishedOwnedFile? vidPublished = null;
        journal.SetState(CleanerTransactionState.Committing);
        try
        {
            // Publication is the same retained object that supplied the
            // validation evidence.  The rename is kernel-atomic and refuses
            // to overwrite a destination that a foreign actor created.
            journal.RegisterPublishedRecord(imgRecord, cleanImgPath);
            imgPublished = WindowsOwnedFilePublisher.PublishOwnedHandle(
                imgHandle,
                cleanImgPath,
                imgRecord.Identity,
                cancellationToken);
            journal.MarkPublished(imgRecord);
            if (faultHook != null) await faultHook(CleanerFailureStage.Commit, "ImagePublished").ConfigureAwait(false);

            if (vidRecord != null && vidEvidence != null && cleanVidPath != null)
            {
                SafeFileHandle vidHandle = vidRecord.RetainedHandle
                    ?? throw new CleanerException(
                        CleanerFailureCategory.OutputCreateFailed,
                        CleanerFailureStage.Commit,
                        SourceProtocol.NonLive,
                        "NonLive staged video lost its retained exact ownership handle before publish.",
                        MediaArtifactKind.MotionVideo);
                journal.RegisterPublishedRecord(vidRecord, cleanVidPath);
                vidPublished = WindowsOwnedFilePublisher.PublishOwnedHandle(
                    vidHandle,
                    cleanVidPath,
                    vidRecord.Identity,
                    cancellationToken);
                journal.MarkPublished(vidRecord);
                if (faultHook != null) await faultHook(CleanerFailureStage.Commit, "VideoPublished").ConfigureAwait(false);
            }

            // Release the strict validation lease only after both exact
            // handles have published.  Directory cleanup then uses the same
            // object-based proof primitive as file cleanup and must be proven
            // before the terminal Committed state.
            journal.ReleaseValidationNamespaceLease();
            if (!journal.TryDeleteOwnedDirectory(out Exception? directoryFailure))
            {
                throw new CleanerException(
                    CleanerFailureCategory.PublishFailed,
                    CleanerFailureStage.Commit,
                    SourceProtocol.NonLive,
                    $"NonLive staging directory cleanup could not be proven before commit: {directoryFailure?.Message}",
                    innerException: directoryFailure);
            }
            journal.StagingDirectory = null;

            if (faultHook != null) await faultHook(CleanerFailureStage.Commit, "BeforeBundleCommit").ConfigureAwait(false);
            journal.SetState(CleanerTransactionState.Committed);
        }
        catch (OperationCanceledException)
        {
            journal.ReleaseValidationNamespaceLease();
            if (imgPublished != null) journal.CleanupRetainedObjectOrRecordFailure(imgRecord);
            if (vidPublished != null && vidRecord != null) journal.CleanupRetainedObjectOrRecordFailure(vidRecord);
            throw;
        }
        catch (Exception ex)
        {
            journal.ReleaseValidationNamespaceLease();
            if (imgPublished != null) journal.CleanupRetainedObjectOrRecordFailure(imgRecord);
            if (vidPublished != null && vidRecord != null) journal.CleanupRetainedObjectOrRecordFailure(vidRecord);
            throw new CleanerException(
                CleanerFailureCategory.PublishFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.NonLive,
                $"Failed to publish NonLive verbatim bundle: {ex.Message}",
                innerException: ex);
        }

        var cleanImgArtifact = bundle.PrimaryImage with
        {
            Path = imgPublished!.FinalPath,
            ByteLength = imgPublished.ByteLength,
            Sha256 = imgPublished.Sha256,
            FileIdentity = imgPublished.FileIdentity
        };

        MediaArtifact? cleanVidArtifact = null;
        if (vidPublished != null && bundle.MotionVideo != null)
        {
            cleanVidArtifact = bundle.MotionVideo with
            {
                Path = vidPublished.FinalPath,
                ByteLength = vidPublished.ByteLength,
                Sha256 = vidPublished.Sha256,
                FileIdentity = vidPublished.FileIdentity
            };
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
            record = journal.AddStagedRecord(destinationPath, artifactRole, identity);
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
                    record = journal.AddStagedRecord(destinationPath, artifactRole, identity);
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
        // A pathname on the SAME VOLUME as every transaction-owned object
        // (workspace root; staging lives under it).  Used to resolve the
        // volume for the kernel-backed by-FileId gone-proof.  It must stay
        // resolvable for the whole transaction; the workspace root does.
        private readonly string _volumeProbePath;

        public CleanerTransactionJournal(string volumeProbePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(volumeProbePath);
            _volumeProbePath = volumeProbePath;
        }

        public CleanerTransactionState State { get; private set; } = CleanerTransactionState.Initial;
        public StagingDirectoryOwnership? StagingDirectory { get; set; }
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

        /// <summary>
        /// Ownership lease of the staging DIRECTORY itself.  The directory is a
        /// transaction-owned filesystem object like any staged file: a pathname
        /// that goes missing is NOT proof the directory object is gone (a
        /// foreign actor can rename it away while the object survives), and a
        /// foreign empty directory that later occupies the recorded pathname
        /// must never be deleted by pathname + emptiness alone.  The retained
        /// creator/ownership handle (BACKUP_SEMANTICS, identity captured from
        /// the creating handle, no reparse point) is the exact authority that
        /// survives rename-away.  It deliberately shares WRITE for Native
        /// staging; ValidationNamespaceLease is a second handle acquired
        /// after staging with the strict no-WRITE-share guarantee.
        /// </summary>
        public sealed class StagingDirectoryOwnership
        {
            public StagingDirectoryOwnership(string path)
            {
                Path = path;
            }

            public string Path { get; }
            public WindowsFileIdentity? Identity { get; private set; }
            public SafeFileHandle? RetainedHandle { get; private set; }
            public SafeFileHandle? ValidationNamespaceLease { get; private set; }
            public bool HandleAcquisitionAttempted { get; private set; }
            public TransactionOwnedObject.CleanupProofState ProofState { get; set; } = TransactionOwnedObject.CleanupProofState.Active;

            public void MarkHandleAcquisitionAttempted() => HandleAcquisitionAttempted = true;
            public void SetIdentity(WindowsFileIdentity identity) => Identity = identity;
            public void RetainHandle(SafeFileHandle handle) => RetainedHandle = handle;
            public void RetainValidationNamespaceLease(SafeFileHandle handle) => ValidationNamespaceLease = handle;
            public void ReleaseHandle()
            {
                RetainedHandle?.Dispose();
                RetainedHandle = null;
            }
            public void ReleaseValidationNamespaceLease()
            {
                ValidationNamespaceLease?.Dispose();
                ValidationNamespaceLease = null;
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
        /// Acquires the second directory handle that freezes the staging
        /// namespace for the validation-to-publish window.  The creator handle
        /// remains retained for directory ownership and continues to share
        /// WRITE for Native staging; this lease intentionally does not share
        /// WRITE.  Failure is fail-closed: validators must never run against a
        /// mutable namespace when publication is tied to retained handles.
        /// </summary>
        public void AcquireValidationNamespaceLease(SourceProtocol protocol)
        {
            StagingDirectoryOwnership ownership = StagingDirectory
                ?? throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    "Cannot establish the validation namespace lease because the transaction has no owned staging directory.");

            if (ownership.ValidationNamespaceLease is not null &&
                !ownership.ValidationNamespaceLease.IsInvalid)
            {
                return;
            }

            if (ownership.Identity is not { } expected)
            {
                throw new CleanerException(
                    CleanerFailureCategory.OutputCreateFailed,
                    CleanerFailureStage.Staging,
                    protocol,
                    "Cannot establish the validation namespace lease without an exact staging-directory identity.");
            }

            try
            {
                SafeFileHandle lease = WindowsOwnedFilePublisher.OpenValidationNamespaceLease(
                    ownership.Path,
                    expected);
                ownership.RetainValidationNamespaceLease(lease);
                VerifyRetainedStagedNamespace(ownership, protocol);
            }
            catch (CleanerException)
            {
                ownership.ReleaseValidationNamespaceLease();
                throw;
            }
            catch (Exception ex)
            {
                ownership.ReleaseValidationNamespaceLease();
                throw new CleanerException(
                    CleanerFailureCategory.LockedFile,
                    CleanerFailureStage.Staging,
                    protocol,
                    $"Unable to establish the strict validation namespace lease for '{ownership.Path}'; refusing best-effort validation: {ex.Message}",
                    innerException: ex);
            }
        }

        /// <summary>
        /// Closes the small acquisition-to-lease boundary before any
        /// path-based validator is allowed to run.  The retained handles are
        /// already the publication authority; this check only verifies that
        /// each staged pathname still resolves to that same object.  It is
        /// performed while the strict directory lease is held, so a foreign
        /// replacement cannot enter between the check and validation.  A
        /// mismatch or missing pathname is never treated as harmless.
        /// </summary>
        private void VerifyRetainedStagedNamespace(
            StagingDirectoryOwnership ownership,
            SourceProtocol protocol)
        {
            foreach (TransactionOwnedObject record in StagedPaths)
            {
                WindowsFileIdentity actual;
                try
                {
                    actual = WindowsFileIdentity.Capture(record.StagedPath);
                }
                catch (Exception ex)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ArtifactChangedSinceExtraction,
                        CleanerFailureStage.Staging,
                        protocol,
                        $"Retained staged {record.ArtifactRole} '{record.StagedPath}' no longer resolves during validation-namespace binding; refusing pathname validation.",
                        record.ArtifactRole,
                        ex);
                }

                if (actual.IsReparsePoint ||
                    actual.VolumeSerialNumber != record.Identity.VolumeSerialNumber ||
                    actual.FileIndex != record.Identity.FileIndex ||
                    actual.LinkCount != record.Identity.LinkCount)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ArtifactChangedSinceExtraction,
                        CleanerFailureStage.Staging,
                        protocol,
                        $"Retained staged {record.ArtifactRole} '{record.StagedPath}' is not the exact object held by this transaction; refusing pathname validation.",
                        record.ArtifactRole);
                }
            }
        }

        /// <summary>
        /// Releases only the strict validation lease.  The exact directory
        /// ownership handle remains until directory cleanup proof completes.
        /// </summary>
        public void ReleaseValidationNamespaceLease()
            => StagingDirectory?.ReleaseValidationNamespaceLease();

        /// <summary>
        /// Deletes the staging directory object with the strongest available
        /// authority.  File and directory cleanup share the same proof
        /// primitive: a successful FileDispositionInfo request is only an
        /// armed delete, never ObjectGone.  The directory is considered
        /// cleaned only after a retained exact handle is disposed and a
        /// definite-not-found exact-FileId probe succeeds.  Foreign children
        /// are never recursively deleted; a non-empty directory fails closed.
        /// </summary>
        public bool TryDeleteOwnedDirectory(out Exception? failure)
        {
            failure = null;
            StagingDirectoryOwnership? ownership = StagingDirectory;
            if (ownership is null)
            {
                return true;
            }
            if (ownership.ProofState is TransactionOwnedObject.CleanupProofState.ExactDeleted or
                TransactionOwnedObject.CleanupProofState.ProvenUnlinked)
            {
                return true;
            }

            SafeFileHandle? handle = ownership.RetainedHandle;
            if (handle is not null && !handle.IsInvalid)
            {
                try
                {
                    OwnedObjectCleanupProof proof =
                        WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                            handle,
                            ownership.Identity ?? throw new IOException(
                                $"Staging directory '{ownership.Path}' has no exact identity."),
                            _volumeProbePath,
                            out _);
                    ownership.ReleaseHandle();
                    if (proof is OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked)
                    {
                        ownership.ProofState = proof == OwnedObjectCleanupProof.Gone
                            ? TransactionOwnedObject.CleanupProofState.ExactDeleted
                            : TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                        return true;
                    }

                    // Non-empty (foreign child present), ambiguous identity
                    // probe, or access denied: disposition success is not a
                    // gone-proof and the directory is left in place.
                    ownership.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    failure = new IOException(
                        $"Staging directory '{ownership.Path}' cleanup proof was {proof}; the exact directory may still exist and foreign children are left in place.");
                    return false;
                }
                catch (Exception ex)
                {
                    ownership.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    failure = new IOException($"Unable to delete staging directory '{ownership.Path}' through its retained handle: {ex.Message}", ex);
                    ownership.ReleaseHandle();
                    return false;
                }
            }

            // No retained directory handle: identity-verified pathname fallback.
            if (!Directory.Exists(ownership.Path))
            {
                failure = new IOException(
                    $"Staging directory '{ownership.Path}' is missing at its recorded pathname; without an exact directory authority its cleanup cannot be proven (PathMissing != DirectoryGone).");
                return false;
            }

            try
            {
                using SafeFileHandle fallbackHandle = WindowsOwnedFilePublisher.OpenOwnedDirectory(ownership.Path);
                WindowsFileIdentity current = WindowsFileIdentity.Capture(fallbackHandle);
                if (ownership.Identity is not { } expected ||
                    current.IsReparsePoint ||
                    (current.FileAttributes & 0x00000010u) == 0 ||
                    current.VolumeSerialNumber != expected.VolumeSerialNumber ||
                    current.FileIndex != expected.FileIndex ||
                    current.LinkCount != expected.LinkCount)
                {
                    failure = new IOException(
                        $"Refusing to delete '{ownership.Path}': the directory at this pathname is not the exact directory this transaction created (foreign-object protection).");
                    return false;
                }
                if (Directory.EnumerateFileSystemEntries(ownership.Path).Any())
                {
                    failure = new IOException(
                        $"Staging directory '{ownership.Path}' still contains entries not owned by this transaction; leaving them in place.");
                    return false;
                }
                OwnedObjectCleanupProof proof =
                    WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                        fallbackHandle,
                        expected,
                        _volumeProbePath,
                        out _);
                if (proof is OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked)
                {
                    ownership.ProofState = proof == OwnedObjectCleanupProof.Gone
                        ? TransactionOwnedObject.CleanupProofState.ExactDeleted
                        : TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                    return true;
                }
                ownership.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                failure = new IOException(
                    $"Staging directory '{ownership.Path}' cleanup proof was {proof}; disposition success is not ObjectGone proof.");
                return false;
            }
            catch (Exception ex)
            {
                failure = new IOException($"Refusing to delete '{ownership.Path}': {ex.Message}", ex);
                return false;
            }
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
                // Delete through the retained exact handle and obtain an
                // OBJECT-based cleanup proof.  Gone (single-link + armed + no
                // resolvable FileId) or Unlinked (LinkCount == 0) are the only
                // proofs that allow the object to be considered cleaned;
                // AliasAlive and Unknown must fail closed (RollbackFailed) —
                // never report RolledBack while the owned object may still
                // exist under a foreign alias.
                OwnedObjectCleanupProof proof =
                    WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                        handle, record.Identity, _volumeProbePath, out _);
                record.ReleaseHandle();
                if (proof == OwnedObjectCleanupProof.Gone)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ExactDeleted;
                }
                else if (proof == OwnedObjectCleanupProof.Unlinked)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                }
                else if (proof == OwnedObjectCleanupProof.AliasAlive)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    AddRollbackFailure(
                        $"Disposition was armed on transaction-owned {record.ArtifactRole} but the exact object (FileId {record.Identity.FileIndex:X}) still exists under an external hard-link alias; cleanup is unproven.");
                }
                else
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    AddRollbackFailure(
                        $"Unable to prove cleanup of transaction-owned {record.ArtifactRole} '{record.CurrentPath}' through its retained handle; the object may still exist after a pathname takeover.");
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
            //    our own leftovers; foreign children are never deleted.  The
            //    directory is an owned object: a missing pathname is NOT proof
            //    it is gone, and a foreign directory occupying the pathname is
            //    never deleted (identity is verified from an exact handle).
            if (StagingDirectory is not null)
            {
                try
                {
                    faultHook?.Invoke(CleanerFailureStage.Rollback, StagingDirectory.Path).GetAwaiter().GetResult();
                    RemoveStagingDirectoryIfOwned(RollbackExceptions);
                }
                catch (Exception ex)
                {
                    RollbackExceptions.Add(new IOException($"Failed to rollback staging directory '{StagingDirectory.Path}': {ex.Message}", ex));
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
        ///
        /// A disposition SUCCESS is not by itself "object gone": after the
        /// final lease closes, the exact FileId is queried through the kernel
        /// (FILE_OPEN_BY_FILE_ID).  If the object is still resolvable it is
        /// alive under a foreign hard-link alias and cleanup is unproven —
        /// the transaction must report RollbackFailed instead of a false
        /// RolledBack while the owned object still exists.
        /// </summary>
        private void DeleteExactObjectOrRecordFailure(
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
                    OwnedObjectCleanupProof proof =
                        WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                            retainedHandle, record.Identity, _volumeProbePath, out _);
                    // Make the proven delete visible to the staging-dir
                    // emptiness check.  Until this final lease closes,
                    // Windows may still enumerate the delete-pending name.
                    record.ReleaseHandle();
                    if (proof == OwnedObjectCleanupProof.Gone)
                    {
                        record.ProofState = TransactionOwnedObject.CleanupProofState.ExactDeleted;
                    }
                    else if (proof == OwnedObjectCleanupProof.Unlinked)
                    {
                        record.ProofState = TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                    }
                    else if (proof == OwnedObjectCleanupProof.AliasAlive)
                    {
                        record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                        exceptions.Add(new IOException(
                            $"Disposition was armed on transaction-owned {record.ArtifactRole} but the exact object (FileId {record.Identity.FileIndex:X}) still exists under an external hard-link alias; cleanup is unproven."));
                    }
                    else
                    {
                        record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                        exceptions.Add(new IOException(
                            $"Unable to prove cleanup of exact transaction-owned {record.ArtifactRole} '{record.CurrentPath}' through its retained handle; the object may still exist after a pathname takeover."));
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

                // The pathname re-opened the EXACT owned object; delete it and
                // obtain the same object-based proof.  The pathname only helped
                // re-discover the object — it never becomes the ownership
                // authority, and a pathname that no longer resolves stays
                // CleanupUnproven (handled above by the IsInvalid branch).
                OwnedObjectCleanupProof proof =
                    WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                        handle, record.Identity, _volumeProbePath, out _);
                handle.Dispose();
                if (proof == OwnedObjectCleanupProof.Gone)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ExactDeleted;
                }
                else if (proof == OwnedObjectCleanupProof.Unlinked)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.ProvenUnlinked;
                }
                else if (proof == OwnedObjectCleanupProof.AliasAlive)
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    exceptions.Add(new IOException(
                        $"Disposition was armed on transaction-owned {record.ArtifactRole} '{path}' but the exact object (FileId {record.Identity.FileIndex:X}) still exists under an external hard-link alias; cleanup is unproven."));
                }
                else
                {
                    record.ProofState = TransactionOwnedObject.CleanupProofState.CleanupUnproven;
                    exceptions.Add(new IOException(
                        $"Unable to prove cleanup of exact transaction-owned {record.ArtifactRole} '{path}': the object may still exist under a foreign alias or could not be deleted."));
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

            StagingDirectory?.ReleaseValidationNamespaceLease();
            StagingDirectory?.ReleaseHandle();
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
        /// Removes the staging directory during rollback.  A missing pathname
        /// is NOT proof the directory object is gone (rename-away keeps the
        /// object alive), and a foreign directory occupying the recorded
        /// pathname is never deleted (identity is verified from an exact
        /// handle).  The directory is deleted only when it is empty; any
        /// remaining entry is foreign and left in place (recorded).  Failure
        /// is recorded so the transaction ends RollbackFailed, never a false
        /// RolledBack while a transaction-created directory may still exist.
        /// </summary>
        private void RemoveStagingDirectoryIfOwned(List<Exception> exceptions)
        {
            if (TryDeleteOwnedDirectory(out Exception? failure))
            {
                return;
            }
            if (failure is not null)
            {
                exceptions.Add(failure);
            }
        }
    }
}
