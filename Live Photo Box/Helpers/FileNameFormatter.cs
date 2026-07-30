using System.IO;

// <summary>
// File: FileNameFormatter.cs
// 提供文件名格式化工具方法，用于在任务列表中截断过长的文件名，
// 使其在有限列宽内友好显示，同时保留文件扩展名。
// </summary>

namespace LivePhotoBox.Helpers
{
    // 文件名格式化工具类。提供文件名截断功能，
    // 将过长的文件名替换为"前缀...后缀"的缩写格式，保留扩展名。
    public static class FileNameFormatter
    {
        // 截断过长的文件名，使其适应任务列表列宽。
        // 格式：文件名前 truncateAt 个字符 + "..." + 文件名后 keepTail 个字符 + 扩展名。
        // fileName: 原始文件名（可含路径或仅文件名）。
        // maxNameLength: 不触发截断的最大名称长度，默认 24。
        // truncateAt: 截断处保留的前缀字符数，默认 19。
        // keepTail: 截断处保留的后缀字符数，默认 4。
        public static string Truncate(string fileName, int maxNameLength = 24, int truncateAt = 19, int keepTail = 4)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (nameWithoutExt.Length <= maxNameLength) return fileName;
            return $"{nameWithoutExt.Substring(0, truncateAt)}...{nameWithoutExt.Substring(nameWithoutExt.Length - keepTail)}{ext}";
        }
    }
}
