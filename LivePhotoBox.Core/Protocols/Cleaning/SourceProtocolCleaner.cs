using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    public SourceProtocolCleaner(ISourceInspector? inspector = null)
    {
        _inspector = inspector ?? new SourceInspector();
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
        string? stagingDir = null;
        var publishedPaths = new List<string>();

        try
        {
            // -------------------------------------------------------------
            // Step 1: Preflight & Bundle Provenance
            // -------------------------------------------------------------
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

            if (facts.Protocol == SourceProtocol.Unknown)
            {
                throw new CleanerException(
                    CleanerFailureCategory.UnsupportedProtocol,
                    CleanerFailureStage.Preflight,
                    SourceProtocol.Unknown,
                    "Cannot clean source with Unknown protocol.");
            }

            if (request.SourceFacts != null && !ReferenceEquals(request.SourceFacts, facts))
            {
                if (request.SourceFacts.Protocol != facts.Protocol ||
                    request.SourceFacts.PrimarySha256 != facts.PrimarySha256 ||
                    request.SourceFacts.SecondarySha256 != facts.SecondarySha256 ||
                    request.SourceFacts.PairingIdentifier != facts.PairingIdentifier)
                {
                    throw new CleanerException(
                        CleanerFailureCategory.ArtifactFactMismatch,
                        CleanerFailureStage.Preflight,
                        facts.Protocol,
                        "Supplied request.SourceFacts does not match ExtractedBundle.SourceFacts.");
                }
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
                return await ExecuteNonLiveNoOpAsync(bundle, workspace, sw, cancellationToken).ConfigureAwait(false);
            }

            var authorizations = facts.ConfirmedResidues;
            if (authorizations == null || authorizations.Count == 0)
            {
                authorizations = CleanupAuthorizationAuthority.ResolveAuthorizations(facts);
            }

            if (authorizations.Count == 0)
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
            var planActions = new List<PlannedCleanupAction>();
            foreach (var residue in authorizations)
            {
                planActions.Add(new PlannedCleanupAction
                {
                    ResidueId = residue.Id,
                    ArtifactRole = residue.ArtifactRole,
                    StructureKind = residue.StructureKind,
                    Selector = residue.Selector,
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

            stagingDir = Path.Combine(workspace.RootDirectory, "staging_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stagingDir);

            string imgExt = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".heic" : ".jpg";
            string stagedImgPath = Path.Combine(stagingDir, "stage-img" + imgExt);

            string? stagedVidPath = null;
            if (bundle.MotionVideo != null)
            {
                string vidExt = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
                stagedVidPath = Path.Combine(stagingDir, "stage-vid" + vidExt);
            }

            var removedFacts = await NativeCleanService.CleanSourceProtocolAsync(
                facts with { ConfirmedResidues = authorizations },
                bundle.PrimaryImage.Path,
                bundle.MotionVideo?.Path,
                stagedImgPath,
                stagedVidPath,
                cancellationToken).ConfigureAwait(false);

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

            // -------------------------------------------------------------
            // Step 6: Preservation Diff
            // -------------------------------------------------------------
            var preservationReport = await MetadataPreservationVerifier.VerifyAsync(
                bundle, stagedImgPath, stagedVidPath, cancellationToken).ConfigureAwait(false);

            if (request.PreservationPolicy == PreservationPolicy.Strict &&
                preservationReport.OverallOutcome != PreservationOutcome.Preserved)
            {
                throw new CleanerException(
                    CleanerFailureCategory.UnexpectedMetadataChange,
                    CleanerFailureStage.PreservationDiff,
                    facts.Protocol,
                    $"Strict preservation check failed: {preservationReport.Summary}");
            }

            // -------------------------------------------------------------
            // Step 7: Structural & Media Validation
            // -------------------------------------------------------------
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
            bool isDualSource = facts.MotionVideo is { IsPresent: true, SourceIndex: 1 };
            var recheckFacts = await _inspector.InspectAsync(
                stagedImgPath,
                isDualSource ? stagedVidPath : null,
                cancellationToken).ConfigureAwait(false);

            if (recheckFacts.Protocol != SourceProtocol.NonLive ||
                recheckFacts.MotionVideo != null ||
                recheckFacts.ProtocolTailLength != 0 ||
                recheckFacts.PairingIdentifier != null)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ProtocolStillDetected,
                    CleanerFailureStage.PostCleanInspection,
                    facts.Protocol,
                    $"Post-clean inspection failed: artifact still recognized as {recheckFacts.Protocol}.");
            }

            // -------------------------------------------------------------
            // Step 9: Bundle Transaction Commit
            // -------------------------------------------------------------
            // Short, non-interruptible commit zone
            string cleanImgPath = workspace.AllocateFilePath("clean-img", imgExt);
            string? cleanVidPath = null;
            if (stagedVidPath != null)
            {
                string vidExt = Path.GetExtension(stagedVidPath);
                cleanVidPath = workspace.AllocateFilePath("clean-vid", vidExt);
            }

            try
            {
                File.Move(stagedImgPath, cleanImgPath, overwrite: true);
                publishedPaths.Add(cleanImgPath);

                if (stagedVidPath != null && cleanVidPath != null)
                {
                    File.Move(stagedVidPath, cleanVidPath, overwrite: true);
                    publishedPaths.Add(cleanVidPath);
                }
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
                TryDeleteDirectory(stagingDir);
                stagingDir = null;
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
                Duration = sw.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            RollbackTransaction(stagingDir, publishedPaths);
            throw;
        }
        catch (CleanerException ex)
        {
            sw.Stop();
            RollbackTransaction(stagingDir, publishedPaths);
            return new ProtocolCleanResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                FailureCategory = ex.Category,
                FailureStage = ex.Stage,
                PreservationOutcome = PreservationOutcome.PartiallyPreserved,
                Duration = sw.Elapsed
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            RollbackTransaction(stagingDir, publishedPaths);
            return new ProtocolCleanResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                FailureCategory = CleanerFailureCategory.None,
                FailureStage = CleanerFailureStage.Preflight,
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
        Stopwatch sw,
        CancellationToken cancellationToken)
    {
        string imgExt = bundle.PrimaryImage.ImageContainer == ImageContainer.Heic ? ".heic" : ".jpg";
        string cleanImgPath = workspace.AllocateFilePath("clean-img", imgExt);
        File.Copy(bundle.PrimaryImage.Path, cleanImgPath, overwrite: true);

        string? cleanVidPath = null;
        if (bundle.MotionVideo != null)
        {
            string vidExt = bundle.MotionVideo.VideoContainer == VideoContainer.Mov ? ".mov" : ".mp4";
            cleanVidPath = workspace.AllocateFilePath("clean-vid", vidExt);
            File.Copy(bundle.MotionVideo.Path, cleanVidPath, overwrite: true);
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
            Duration = sw.Elapsed
        };
    }

    private static void RollbackTransaction(string? stagingDir, List<string> publishedPaths)
    {
        TryDeleteDirectory(stagingDir);

        foreach (var path in publishedPaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort rollback
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
                Directory.Delete(dir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }
}
