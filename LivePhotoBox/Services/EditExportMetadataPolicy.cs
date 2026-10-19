using System;
using System.Threading;

namespace LivePhotoBox.Services;

/// <summary>Pre-publication guard until a verified Native metadata transfer exists.</summary>
internal static class EditExportMetadataPolicy
{
    internal static void ValidateRequest(bool copyExif, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (copyExif)
            throw new NotSupportedException(ResourceService.GetString("EditPage_ExportExifUnavailable"));
    }

    internal static void EnsureAllFramesSucceeded(int expected, int succeeded, int failed)
    {
        if (failed != 0 || succeeded != expected)
            throw new InvalidOperationException($"Frame export incomplete: {succeeded}/{expected} succeeded, {failed} failed.");
    }
}
