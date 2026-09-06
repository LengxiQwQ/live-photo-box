using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
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
    private readonly Func<SourceMediaFacts, IReadOnlyList<PlannedCleanupAction>, string, string?, string?, string?, CancellationToken, Task<IReadOnlyList<RemovedProtocolFact>>> _cleanInvoker;

    /// <summary>
    /// Test seam for deterministic fault injection and mid-operation cancellation in tests.
    /// Only active when set by test fixtures; in production this is null.
    /// </summary>
    public Func<CleanerFailureStage, string?, Task>? FaultInjectionHook { get; set; }

    public SourceProtocolCleaner(
        ISourceInspector? inspector = null,
        Func<SourceMediaFacts, IReadOnlyList<PlannedCleanupAction>, string, string?, string?, string?, CancellationToken, Task<IReadOnlyList<RemovedProtocolFact>>>? cleanInvoker = null)
    {
        _inspector = inspector ?? new SourceInspector();
        _cleanInvoker = cleanInvoker ?? NativeCleanService.CleanSourceProtocolAsync;
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

            // -------------------------------------------------------------
            // Step 3: Load P1 Cleanup Authorization & Handle NonLive
            // -------------------------------------------------------------
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
                    ArtifactRole = residue.ArtifactRole,
                    StructureKind = residue.StructureKind,
                    Selector = residue.Selector,
                    ExpectedSemantic = residue.ExpectedSemantic,
                    RemovalMode = residue.RemovalMode,
                    ExpectedFingerprint = residue.ExpectedFingerprint,
                    IsMandatory = residue.RequiredAfterExtraction
                });
            }

            var cleanupPlan = new ProtocolCleanupPlan
            {
                Protocol = facts.Protocol,
                Actions = planActions
            };

            // -------------------------------------------------------------
            // Step 5: Stage Clean (Isolated Workspace)
            // -------------------------------------------------------------
            cancellationToken.ThrowIfCancellationRequested();
            journal.SetState(CleanerTransactionState.Staging);

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
            try
            {
                removedFacts = await _cleanInvoker(
                    facts,
                    cleanupPlan.Actions,
                    bundle.PrimaryImage.Path,
                    bundle.MotionVideo?.Path,
                    stagedImgPath,
                    stagedVidPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
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
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Staging, "ImageStaged").ConfigureAwait(false);

            if (stagedVidPath != null)
            {
                if (!File.Exists(stagedVidPath))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.OutputCreateFailed,
                        CleanerFailureStage.Staging,
                        facts.Protocol,
                        "Cleaned staged video was not generated.");
                }
                if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Staging, "VideoStaged").ConfigureAwait(false);
            }

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

            var preservationReport = await MetadataPreservationVerifier.VerifyAsync(
                bundle, stagedImgPath, stagedVidPath, cancellationToken).ConfigureAwait(false);

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
            // Step 8: Source Inspector Post-clean Gate
            // -------------------------------------------------------------
            if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.PostCleanInspection, "BeforeInspect").ConfigureAwait(false);

            bool isDualSource = facts.MotionVideo is { IsPresent: true, SourceIndex: 1 };

            // 8.1 Inspect cleaned image individually
            var imgRecheckFacts = await _inspector.InspectAsync(stagedImgPath, null, cancellationToken).ConfigureAwait(false);
            if (imgRecheckFacts.Protocol != SourceProtocol.NonLive ||
                imgRecheckFacts.MotionVideo != null ||
                imgRecheckFacts.ProtocolTailLength != 0 ||
                imgRecheckFacts.PairingIdentifier != null)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ProtocolStillDetected,
                    CleanerFailureStage.PostCleanInspection,
                    facts.Protocol,
                    $"Post-clean inspection failed: image artifact still recognized as {imgRecheckFacts.Protocol} (PairingId='{imgRecheckFacts.PairingIdentifier}').",
                    MediaArtifactKind.PrimaryImage);
            }

            // 8.2 Inspect cleaned video individually if present
            if (stagedVidPath != null)
            {
                var vidRecheckFacts = await _inspector.InspectAsync(stagedVidPath, null, cancellationToken).ConfigureAwait(false);
                if (vidRecheckFacts.Protocol != SourceProtocol.NonLive ||
                    vidRecheckFacts.ProtocolTailLength != 0 ||
                    (isDualSource && vidRecheckFacts.PairingIdentifier != null) ||
                    (vidRecheckFacts.PairingIdentifier != null && vidRecheckFacts.PairingIdentifier == imgRecheckFacts.PairingIdentifier))
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ProtocolStillDetected,
                        CleanerFailureStage.PostCleanInspection,
                        facts.Protocol,
                        $"Post-clean inspection failed: video artifact still recognized as {vidRecheckFacts.Protocol} (PairingId='{vidRecheckFacts.PairingIdentifier}').",
                        MediaArtifactKind.MotionVideo);
                }
            }

            // 8.3 Inspect combined pair if dual source
            if (isDualSource && stagedVidPath != null)
            {
                var pairRecheckFacts = await _inspector.InspectAsync(stagedImgPath, stagedVidPath, cancellationToken).ConfigureAwait(false);
                if (pairRecheckFacts.Protocol != SourceProtocol.NonLive ||
                    pairRecheckFacts.PairingIdentifier != null)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ProtocolStillDetected,
                        CleanerFailureStage.PostCleanInspection,
                        facts.Protocol,
                        $"Post-clean bundle inspection failed: pair still recognized as {pairRecheckFacts.Protocol} (PairingId='{pairRecheckFacts.PairingIdentifier}').");
                }
            }

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
                File.Move(stagedImgPath, cleanImgPath, overwrite: true);
                journal.PublishedPaths.Add(cleanImgPath);

                if (FaultInjectionHook != null) await FaultInjectionHook(CleanerFailureStage.Commit, "ImagePublished").ConfigureAwait(false);

                if (stagedVidPath != null && cleanVidPath != null)
                {
                    File.Move(stagedVidPath, cleanVidPath, overwrite: true);
                    journal.PublishedPaths.Add(cleanVidPath);
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
                Sha256 = await workspace.ComputeFileSha256Async(cleanImgPath, cancellationToken).ConfigureAwait(false)
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
                    Sha256 = await workspace.ComputeFileSha256Async(cleanVidPath, cancellationToken).ConfigureAwait(false)
                };
            }

            sw.Stop();

            return new ProtocolCleanResult
            {
                Success = true,
                CleanedImage = cleanImgArtifact,
                CleanedVideo = cleanVidArtifact,
                CleanedGainMap = bundle.GainMap,
                RemovedFacts = removedFacts,
                PreservationOutcome = preservationReport.OverallOutcome,
                PreservationReport = preservationReport,
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
    }

    private static async Task VerifyArtifactIntegrityAsync(
        MediaArtifact artifact,
        string roleName,
        SourceProtocol protocol,
        CancellationToken cancellationToken)
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

        if (!string.IsNullOrEmpty(artifact.Sha256))
        {
            using var fs = File.OpenRead(artifact.Path);
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
        File.Copy(bundle.PrimaryImage.Path, cleanImgPath, overwrite: true);
        journal.PublishedPaths.Add(cleanImgPath);

        string? cleanVidPath = null;
        if (bundle.MotionVideo != null)
        {
            string vidExt = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
            cleanVidPath = workspace.AllocateFilePath("clean-vid", vidExt);
            File.Copy(bundle.MotionVideo.Path, cleanVidPath, overwrite: true);
            journal.PublishedPaths.Add(cleanVidPath);
        }

        var cleanImgArtifact = bundle.PrimaryImage with
        {
            Path = cleanImgPath,
            ByteLength = new FileInfo(cleanImgPath).Length,
            Sha256 = await workspace.ComputeFileSha256Async(cleanImgPath, cancellationToken).ConfigureAwait(false)
        };

        MediaArtifact? cleanVidArtifact = null;
        if (cleanVidPath != null && bundle.MotionVideo != null)
        {
            cleanVidArtifact = bundle.MotionVideo with
            {
                Path = cleanVidPath,
                ByteLength = new FileInfo(cleanVidPath).Length,
                Sha256 = await workspace.ComputeFileSha256Async(cleanVidPath, cancellationToken).ConfigureAwait(false)
            };
        }

        sw.Stop();

        var report = new PreservationReport
        {
            OverallOutcome = PreservationOutcome.Preserved,
            Items =
            [
                new PreservationReportItem
                {
                    Name = "NonLiveNoOp",
                    Status = PreservationCheckStatus.VerifiedPreserved,
                    Details = "NonLive source bypassed cleaning without mutations."
                }
            ],
            Summary = "Source is NonLive; artifacts carried through verbatim."
        };

        journal.SetState(CleanerTransactionState.Committed);

        return new ProtocolCleanResult
        {
            Success = true,
            CleanedImage = cleanImgArtifact,
            CleanedVideo = cleanVidArtifact,
            CleanedGainMap = bundle.GainMap,
            RemovedFacts = [],
            PreservationOutcome = PreservationOutcome.Preserved,
            PreservationReport = report,
            CleanupPlan = new ProtocolCleanupPlan
            {
                Protocol = SourceProtocol.NonLive,
                Actions = []
            },
            TransactionState = CleanerTransactionState.Committed,
            Duration = sw.Elapsed
        };
    }

    private sealed class CleanerTransactionJournal
    {
        public CleanerTransactionState State { get; private set; } = CleanerTransactionState.Initial;
        public string? StagingDir { get; set; }
        public List<string> PublishedPaths { get; } = [];
        public List<Exception> RollbackExceptions { get; } = [];

        public void SetState(CleanerTransactionState state) => State = state;

        public void Rollback(Func<CleanerFailureStage, string?, Task>? faultHook, SourceProtocol protocol)
        {
            State = CleanerTransactionState.RollingBack;
            RollbackExceptions.Clear();

            // 1. Delete published artifacts
            foreach (var path in PublishedPaths)
            {
                try
                {
                    faultHook?.Invoke(CleanerFailureStage.Rollback, path).GetAwaiter().GetResult();
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex)
                {
                    RollbackExceptions.Add(new IOException($"Failed to rollback published artifact '{path}': {ex.Message}", ex));
                }
            }

            // 2. Delete staging directory
            if (!string.IsNullOrEmpty(StagingDir))
            {
                try
                {
                    faultHook?.Invoke(CleanerFailureStage.Rollback, StagingDir).GetAwaiter().GetResult();
                    if (Directory.Exists(StagingDir))
                    {
                        Directory.Delete(StagingDir, recursive: true);
                    }
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
    }

    private static void TryDeleteDirectory(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return;
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to delete staging directory '{dir}': {ex.Message}");
        }
    }
}
