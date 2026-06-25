using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Services.Protocols;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    public static class LivePhotoCompositionService
    {
        public static string CreateOutputFileName(string baseName, int selectedModeIndex)
        {
            return $"{baseName}.jpg";
        }

        public static async Task WriteLivePhotoAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            int selectedModeIndex,
            CancellationToken token)
        {
            // Delegate to the shared implementation — same logic, same protocol support.
            await LivePhotoMergeService.WriteLivePhotoAsync(
                sourceImg, sourceVid, targetPath, selectedModeIndex, token);
        }
    }
}
