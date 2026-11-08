using System;
using System.Threading;

namespace LivePhotoBox.Services;

/// <summary>Pre-publication guard until a verified Native metadata transfer exists.</summary>
internal static class EditExportMetadataPolicy
{
    internal static void ValidateRequest(bool copyExif, CancellationToken token = default)
    {
        if (RequestFailure(copyExif, token) is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static Exception? RequestFailure(bool copyExif, CancellationToken token = default)
        => token.IsCancellationRequested ? new OperationCanceledException(token) : copyExif
            ? new NotSupportedException(ResourceService.GetString("EditPage_ExportExifUnavailable")) : null;

    internal static Exception? IncompleteFailure(int expected, int succeeded, int failed)
        => failed != 0 || succeeded != expected
            ? new InvalidOperationException($"Frame export incomplete: {succeeded}/{expected} succeeded, {failed} failed.") : null;

    internal static void EnsureAllFramesSucceeded(int expected, int succeeded, int failed)
    {
        if (IncompleteFailure(expected, succeeded, failed) is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
