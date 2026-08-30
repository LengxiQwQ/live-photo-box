using System;
using System.IO;

namespace LivePhotoBox.Services.Protocols
{
    public static class AppleLivePhotoMebxWriter
    {
        public static bool TryAppendStillImageTrack(string movPath, double coverSeconds, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(movPath);
                if (LivePhotoBox.Interop.NativeAppleMebxWriter.TryAppendStillImageTrack(data, coverSeconds, out byte[]? output, out error))
                {
                    if (output != null)
                    {
                        File.WriteAllBytes(movPath, output);
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
