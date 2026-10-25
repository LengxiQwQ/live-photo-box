using System;
using System.IO;
namespace LivePhotoBox.Services.Protocols;

internal static class HeifAuxImageWriter
{
    public static bool TryHasNclxColr(string heicPath, out bool hasNclx, out string? error)
    {
        hasNclx = false;
        error = null;
        try
        {
            byte[] data = File.ReadAllBytes(heicPath);
            if (!HeifBoxParser.TryFindBox(data, 0, data.Length, "meta", out int metaStart, out int metaSize, out int metaBodyStart))
            {
                error = "No meta box found.";
                return false;
            }

            int metaChildStart = metaBodyStart + 4;
            int metaChildEnd = metaStart + metaSize;
            int iprpStart = -1, iprpSize = 0, iprpBodyStart = -1;
            if (!HeifBoxParser.TryWalkBoxes(data, metaChildStart, metaChildEnd, (type, bodyStart, bodyLen) =>
            {
                if (type == "iprp")
                {
                    iprpStart = bodyStart - 8;
                    iprpSize = bodyLen + 8;
                    iprpBodyStart = bodyStart;
                }
            }))
            {
                error = "Meta box is malformed.";
                return false;
            }

            if (iprpStart < 0)
            {
                return true; // 无 iprp 视为无 nclx
            }

            int ipcoStart = -1, ipcoSize = 0;
            if (!HeifBoxParser.TryWalkBoxes(data, iprpBodyStart, iprpBodyStart + (iprpSize - 8), (type, bodyStart, bodyLen) =>
            {
                if (type == "ipco")
                {
                    ipcoStart = bodyStart - 8;
                    ipcoSize = bodyLen + 8;
                }
            }))
            {
                error = "iprp box is malformed.";
                return false;
            }

            if (ipcoStart < 0)
            {
                return true; // 无 ipco 视为无 nclx
            }

            int p = ipcoStart + 8;
            int ipcoEnd = ipcoStart + ipcoSize;
            while (p + 8 <= ipcoEnd)
            {
                long size = HeifBoxParser.Read32(data, p);
                int header = 8;
                if (size == 1)
                {
                    if (p + 16 > ipcoEnd) break;
                    size = (long)HeifBoxParser.Read64(data, p + 8);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = ipcoEnd - p;
                }
                if (size < header || p + size > ipcoEnd) break;

                if (HeifBoxParser.ReadFourCc(data, p + 4).Equals("colr", StringComparison.Ordinal)
                    && size >= header + 8
                    && HeifBoxParser.ReadFourCc(data, p + header).Equals("nclx", StringComparison.Ordinal))
                {
                    hasNclx = true;
                    return true;
                }
                p += (int)size;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryAddHdrGainMapAux(string heicPath, out string? error)
    {
        error = "[AuthorityViolation] Historical HEIF mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
    public static bool TryAddNclxColr(string heicPath, out string? error)
    {
        error = "[AuthorityViolation] Historical HEIF mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
    public static bool TryAddNclxColr(string heicPath, int primaries, int transfer, int matrix, int fullRange, out string? error)
    {
        error = "[AuthorityViolation] Historical HEIF mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
}
