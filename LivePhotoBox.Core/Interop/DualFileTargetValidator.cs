using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Interop;

/// <summary>Capability derived from a real Native Writer plan after both complete
/// pair members passed Native validation. Callers cannot provide a verdict or hashes.</summary>
internal sealed class NativeTargetPairProof
{
    private readonly ulong _imageLength, _videoLength;
    private readonly string _imageHash, _videoHash;
    private unsafe NativeTargetPairProof(NativeTargetWriterOutputV1 image, NativeTargetWriterOutputV1 video)
    {
        _imageLength = image.ByteLength; _videoLength = video.ByteLength;
        _imageHash = Convert.ToHexString(new ReadOnlySpan<byte>(image.Sha256, 32));
        _videoHash = Convert.ToHexString(new ReadOnlySpan<byte>(video.Sha256, 32));
    }

    internal static unsafe NativeTargetPairProof Observe(nint context, nint plan)
    {
        NativeTargetWriterOutputV1* outputs = stackalloc NativeTargetWriterOutputV1[2];
        for (int i = 0; i < 2; i++) outputs[i] = new()
            { StructSize = (uint)sizeof(NativeTargetWriterOutputV1), Version = 1 };
        var status = new NativeTargetWriterStatusV1 { StructSize = (uint)sizeof(NativeTargetWriterStatusV1), Version = 1 };
        if (NativeMethods.TargetWriterGetOutputs(context, plan, outputs, 2, out uint count, ref status) != NativeResult.Ok ||
            count != 2 || status.State != 2 || status.FailureCategory != 0 || status.OutputCount != 2 ||
            status.TargetProfile is not (7 or 8) || outputs[0].ArtifactRole != 0 || outputs[1].ArtifactRole != 1 ||
            outputs[0].Generation != status.Generation || outputs[1].Generation != status.Generation ||
            outputs[0].TargetProfile != status.TargetProfile || outputs[1].TargetProfile != status.TargetProfile)
            throw new TargetValidationException("Native did not issue a validated complete target pair.");
        return new NativeTargetPairProof(outputs[0], outputs[1]);
    }

    internal void RequireMatches(WindowsSplitOutputPublisher.TargetPublicationStage image,
        WindowsSplitOutputPublisher.TargetPublicationStage video)
    {
        using var imageReader = image.OpenRead();
        using var videoReader = video.OpenRead();
        if ((ulong)image.ByteLength != _imageLength || (ulong)video.ByteLength != _videoLength ||
            !string.Equals(image.Sha256, _imageHash, StringComparison.Ordinal) ||
            !string.Equals(video.Sha256, _videoHash, StringComparison.Ordinal))
            throw new TargetValidationException("Transported pair differs from the complete Native-validated pair.");
    }
}

internal static class DualFileTargetValidator
{
    internal static Task ValidateAsync(WindowsSplitOutputPublisher.TargetPublicationStage image,
        WindowsSplitOutputPublisher.TargetPublicationStage video, NativeTargetPairProof proof, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        proof.RequireMatches(image, video);
        return Task.CompletedTask;
    }
}
