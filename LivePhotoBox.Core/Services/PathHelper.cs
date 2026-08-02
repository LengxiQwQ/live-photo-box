using System;
using System.IO;

// =======================================================================================
// PathHelper — 文件路径辅助工具
// =======================================================================================
// 提供以下功能：
//   - GetPairingKey：根据输入目录生成用于文件配对的唯一 key（含子文件夹路径）
//   - GetUniqueFilePath：在输出目录中原子性地获取不冲突的文件路径
//   - GetRelativeSubDirectory：获取文件相对于输入目录的子文件夹路径
//   - TryReservePath：使用 FileMode.CreateNew 做操作系统级别的原子路径预留
// =======================================================================================

namespace LivePhotoBox.Services
{
    // 文件路径辅助工具
    public static class PathHelper
    {
        // 生成配对用的唯一 key，包含子文件夹路径以防止同名文件冲突。
        // 示例：输入目录 "C:\Photos"，文件 "C:\Photos\2024\IMG_001.jpg" → key "2024\IMG_001"
        // 根目录下的文件 key 保持纯文件名："IMG_001"
        public static string GetPairingKey(string inputDirectory, string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            string? dir = Path.GetDirectoryName(filePath);

            if (dir != null && dir.Length > inputDirectory.Length)
            {
                string sub = dir[inputDirectory.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (sub.Length > 0)
                    return $"{sub}\\{name}";
            }

            return name;
        }

        // 获取文件相对于输入目录的子文件夹路径（不含文件名）。
        // 示例：inputDir "C:\Photos", filePath "C:\Photos\2024\IMG.jpg" → "2024"
        // 根目录下的文件返回 null。
        public static string? GetRelativeSubDirectory(string inputDirectory, string filePath)
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (dir != null && dir.Length > inputDirectory.Length
                && dir.StartsWith(inputDirectory, StringComparison.OrdinalIgnoreCase))
            {
                // 确保命中在目录边界处（如 C:\Photos 不匹配 C:\PhotosExtra）
                char boundaryChar = dir[inputDirectory.Length];
                if (boundaryChar == Path.DirectorySeparatorChar || boundaryChar == Path.AltDirectorySeparatorChar)
                {
                    return dir[(inputDirectory.Length + 1)..];
                }
            }
            return null;
        }

        // 在输出目录中获取一个不冲突的文件路径，并原子性预留该路径。
        // 如果文件名已存在，自动追加 (2)、(3) 等后缀（与 Windows 资源管理器行为一致）。
        // relativeSubDirectory 为可选的子文件夹路径（如 "2024\vacation"），
        // 传入后会在输出目录下创建对应的子目录结构。
        public static string GetUniqueFilePath(string directory, string fileName, string? relativeSubDirectory = null)
        {
            string targetDir = directory;
            if (!string.IsNullOrEmpty(relativeSubDirectory))
            {
                targetDir = Path.Combine(directory, relativeSubDirectory);
                Directory.CreateDirectory(targetDir);
            }

            string path = Path.Combine(targetDir, fileName);
            if (TryReservePath(path))
                return path;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);

            for (int i = 2; i < 999; i++)
            {
                path = Path.Combine(targetDir, $"{nameWithoutExt} ({i}){ext}");
                if (TryReservePath(path))
                    return path;
            }

            // 极端情况：999 个同名文件都用完了，追加 GUID
            return Path.Combine(targetDir, $"{nameWithoutExt} ({Guid.NewGuid():N}){ext}");
        }

        // 原子性尝试预留一个文件路径。
        // FileMode.CreateNew 在文件已存在时抛出 IOException，不存在则创建空文件。
        // 这是操作系统级别的原子操作，不存在 TOCTOU 竞态。
        private static bool TryReservePath(string path)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                fs.SetLength(0); // 创建 0 字节占位文件
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
