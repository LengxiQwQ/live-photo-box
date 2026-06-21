using System.IO;

namespace LivePhotoBox.Helpers
{
    public static class FileNameFormatter
    {
        /// <summary>Truncate a long filename to fit in the task list column.</summary>
        public static string Truncate(string fileName, int maxNameLength = 30, int truncateAt = 19)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (nameWithoutExt.Length <= maxNameLength) return fileName;
            return $"{nameWithoutExt.Substring(0, truncateAt)}...{nameWithoutExt.Substring(nameWithoutExt.Length - 8)}{ext}";
        }
    }
}
