using System;
using System.Globalization;

namespace LivePhotoBox.Models
{
    public sealed class CrashLogEntry
    {
        public string FileName { get; init; } = string.Empty;
        public string FilePath { get; init; } = string.Empty;
        public DateTimeOffset LastModified { get; init; }
        public long FileSizeBytes { get; init; }

        public string DisplaySummary => $"{LastModified.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)} · {FormatFileSize(FileSizeBytes)}";

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024 * 1024)
            {
                return $"{bytes / 1024.0:F1} KB";
            }

            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}
