using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Services;

/// <summary>
/// P8 orchestration boundary for the existing production writers. P9 replaces their
/// structural implementations here, without changing GUI/CLI or Neutral preparation.
/// Source provenance and backend identity never select the target protocol.
/// </summary>
internal static class ExistingTargetWriterAdapter
{
    internal static string CreateOutputFileName(string baseName, OutputProfile profile,
        string imagePath, int namingRuleIndex, string? customPattern, int? taskIndex) =>
        LivePhotoMergeService.CreateOutputFileName(baseName,
            ProductSelectionAdapter.WriterProtocol(profile.TargetProtocol), imagePath,
            ProductSelectionAdapter.WriterFormat(profile.Format), namingRuleIndex, customPattern, taskIndex);

    internal static Task WriteAsync(NeutralMediaBundle bundle, OutputProfile profile,
        string targetPath, CancellationToken token, long presentationTimestampUs, bool overwriteExisting)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(profile);
        var requirement = profile.MediaRequirement;
        if (bundle.MotionVideo is null ||
            bundle.PrimaryImage.ImageContainer != requirement.ImageContainer ||
            bundle.MotionVideo.VideoContainer != requirement.VideoContainer ||
            (requirement.VideoCodec != VideoCodec.Copy && bundle.MotionVideo.VideoCodec != requirement.VideoCodec))
            throw new InvalidDataException("Prepared neutral media does not satisfy the selected output profile.");

        return LivePhotoMergeService.WriteLivePhotoRebuiltAsync(bundle.PrimaryImage.Path,
            bundle.MotionVideo.Path, targetPath, ProductSelectionAdapter.WriterProtocol(profile.TargetProtocol),
            token, presentationTimestampUs, ProductSelectionAdapter.WriterFormat(profile.Format),
            overwriteExisting, testHook: null);
    }
}
