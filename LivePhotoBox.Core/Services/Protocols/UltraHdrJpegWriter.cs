using System;
namespace LivePhotoBox.Services.Protocols;

internal static class UltraHdrJpegWriter
{
    public static void Write(string primaryJpegPath, string gainMapJpegPath, string outputPath, IsoGainMapMetadata metadata)
        => throw new NotSupportedException("[AuthorityViolation] Historical Ultra HDR mutation is unsupported; use the authorized media pipeline.");
}
