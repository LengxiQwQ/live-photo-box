using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Protocols.Cleaning;

public interface ITargetedPostCleanVerifier
{
    Task VerifyPostCleanAsync(
        SourceMediaFacts originalFacts,
        ProtocolCleanupPlan cleanupPlan,
        string stagedImgPath,
        string? stagedVidPath,
        CancellationToken cancellationToken = default);
}

public sealed class TargetedPostCleanVerifier : ITargetedPostCleanVerifier
{
    private readonly ISourceInspector _inspector;

    public TargetedPostCleanVerifier(ISourceInspector? inspector = null)
    {
        _inspector = inspector ?? new SourceInspector();
    }

    public async Task VerifyPostCleanAsync(
        SourceMediaFacts originalFacts,
        ProtocolCleanupPlan cleanupPlan,
        string stagedImgPath,
        string? stagedVidPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(originalFacts);
        ArgumentNullException.ThrowIfNull(cleanupPlan);
        ArgumentNullException.ThrowIfNull(stagedImgPath);

        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(stagedImgPath) || new FileInfo(stagedImgPath).Length == 0)
        {
            throw new CleanerException(
                CleanerFailureCategory.MediaInvalid,
                CleanerFailureStage.PostCleanInspection,
                originalFacts.Protocol,
                "Cleaned image artifact is missing or empty.",
                MediaArtifactKind.PrimaryImage);
        }

        if (stagedVidPath != null && (!File.Exists(stagedVidPath) || new FileInfo(stagedVidPath).Length == 0))
        {
            throw new CleanerException(
                CleanerFailureCategory.MediaInvalid,
                CleanerFailureStage.PostCleanInspection,
                originalFacts.Protocol,
                "Cleaned video artifact is missing or empty.",
                MediaArtifactKind.MotionVideo);
        }

        await VerifyPostCleanWithInspectorAsync(originalFacts, stagedImgPath, stagedVidPath, cancellationToken, cleanupPlan.CompoundPrimary).ConfigureAwait(false);
    }

    private async Task VerifyPostCleanWithInspectorAsync(
        SourceMediaFacts facts,
        string stagedImgPath,
        string? stagedVidPath,
        CancellationToken cancellationToken, LivePhotoBox.Interop.NativeCompoundPrimary? compound)
    {
        bool isDualSource = facts.MotionVideo is { IsPresent: true, SourceIndex: 1 };

        var imgRecheck = compound is null
            ? await InspectStagedAsync(facts, stagedImgPath, null, MediaArtifactKind.PrimaryImage, cancellationToken).ConfigureAwait(false)
            : compound.Inspect(stagedImgPath, cancellationToken).Facts;
        if (imgRecheck.Protocol != SourceProtocol.NonLive ||
            imgRecheck.MotionVideo != null ||
            imgRecheck.ProtocolTailLength != 0 ||
            imgRecheck.PairingIdentifier != null)
        {
            throw new CleanerException(
                CleanerFailureCategory.ProtocolStillDetected,
                CleanerFailureStage.PostCleanInspection,
                facts.Protocol,
                $"Post-clean inspection failed: image artifact still recognized as {imgRecheck.Protocol} (PairingId='{imgRecheck.PairingIdentifier}').",
                MediaArtifactKind.PrimaryImage);
        }

        if (stagedVidPath != null)
        {
            var vidRecheck = await InspectStagedAsync(
                facts, stagedVidPath, null, MediaArtifactKind.MotionVideo, cancellationToken).ConfigureAwait(false);
            if (vidRecheck.Protocol != SourceProtocol.NonLive ||
                vidRecheck.ProtocolTailLength != 0 ||
                (isDualSource && vidRecheck.PairingIdentifier != null) ||
                (vidRecheck.PairingIdentifier != null && vidRecheck.PairingIdentifier == imgRecheck.PairingIdentifier))
            {
                throw new CleanerException(
                    CleanerFailureCategory.ProtocolStillDetected,
                    CleanerFailureStage.PostCleanInspection,
                    facts.Protocol,
                    $"Post-clean inspection failed: video artifact still recognized as {vidRecheck.Protocol} (PairingId='{vidRecheck.PairingIdentifier}').",
                    MediaArtifactKind.MotionVideo);
            }
        }

        if (isDualSource && stagedVidPath != null)
        {
            var pairRecheck = await InspectStagedAsync(
                facts, stagedImgPath, stagedVidPath, null, cancellationToken).ConfigureAwait(false);
            if (pairRecheck.Protocol != SourceProtocol.NonLive ||
                pairRecheck.PairingIdentifier != null)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ProtocolStillDetected,
                    CleanerFailureStage.PostCleanInspection,
                    facts.Protocol,
                    $"Post-clean bundle inspection failed: pair still recognized as {pairRecheck.Protocol} (PairingId='{pairRecheck.PairingIdentifier}').");
            }
        }
    }

    private async Task<SourceMediaFacts> InspectStagedAsync(
        SourceMediaFacts originalFacts,
        string primaryPath,
        string? secondaryPath,
        MediaArtifactKind? artifactRole,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _inspector.InspectAsync(primaryPath, secondaryPath, cancellationToken).ConfigureAwait(false);
        }
        catch (SourceInspectionException exception) when (
            exception.Category is SourceInspectionFailureCategory.Malformed or SourceInspectionFailureCategory.Ambiguous)
        {
            // The staged result must establish neutral correctness before publish.
            // Preserve the Inspector's rejection instead of reporting a generic
            // None/Preflight failure from the caller's catch-all handler.
            throw new CleanerException(
                CleanerFailureCategory.MediaInvalid,
                CleanerFailureStage.PostCleanInspection,
                originalFacts.Protocol,
                $"Post-clean inspection rejected staged media ({exception.Stage}/{exception.Category}): {exception.Reason}",
                artifactRole,
                exception);
        }
    }
}
