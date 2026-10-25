using System;
using System.Threading;
using System.Threading.Tasks;
namespace LivePhotoBox.Services.Protocols;

public static class VivoDualFileMetadataWriter
{
    public static Task WritePairMetadataAsync(string imagePath, string videoPath, CancellationToken token)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled(token);
        return Task.FromException(new NotSupportedException("[AuthorityViolation] Historical pair mutation is unsupported; use the authorized media pipeline."));
    }
}
