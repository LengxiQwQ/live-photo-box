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

    internal static NativeTargetPairProof Observe(nint context, nint plan)
    {
        var attempt = TryObserve(context, plan);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Proof!;
    }

    internal sealed record ObservationAttempt(NativeTargetPairProof? Proof, Exception? Failure);

    internal static unsafe ObservationAttempt TryObserve(nint context, nint plan)
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
            return new(null, new TargetValidationException("Native did not issue a validated complete target pair."));
        return new(new NativeTargetPairProof(outputs[0], outputs[1]), null);
    }

    internal void RequireMatches(WindowsSplitOutputPublisher.TargetPublicationStage image,
        WindowsSplitOutputPublisher.TargetPublicationStage video)
    {
        if(MatchFailure(image,video) is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    internal Exception? MatchFailure(WindowsSplitOutputPublisher.TargetPublicationStage image,
        WindowsSplitOutputPublisher.TargetPublicationStage video)
    {
        var imageAttempt = image.TryOpenRead();
        if (imageAttempt.Failure is { } imageFailure) return imageFailure;
        using var imageReader = imageAttempt.Reader!;
        var videoAttempt = video.TryOpenRead();
        if (videoAttempt.Failure is { } videoFailure) return videoFailure;
        using var videoReader = videoAttempt.Reader!;
        if ((ulong)image.ByteLength != _imageLength || (ulong)video.ByteLength != _videoLength ||
            !string.Equals(image.Sha256, _imageHash, StringComparison.Ordinal) ||
            !string.Equals(video.Sha256, _videoHash, StringComparison.Ordinal))
            return new TargetValidationException("Transported pair differs from the complete Native-validated pair.");
        return null;
    }
}

internal static class DualFileTargetValidator
{
    internal static Task ValidateAsync(WindowsSplitOutputPublisher.TargetPublicationStage image,
        WindowsSplitOutputPublisher.TargetPublicationStage video,NativeTargetPairProof proof,CancellationToken token)
    {
        if(ValidationFailure(image,video,proof,token) is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return Task.CompletedTask;
    }
    internal static Exception? ValidationFailure(WindowsSplitOutputPublisher.TargetPublicationStage image,
        WindowsSplitOutputPublisher.TargetPublicationStage video,NativeTargetPairProof proof,CancellationToken token)
    {
        if(token.IsCancellationRequested)return new OperationCanceledException(token);
        var failure=proof.MatchFailure(image,video);
        return failure ?? (token.IsCancellationRequested ? new OperationCanceledException(token) : null);
    }
}
