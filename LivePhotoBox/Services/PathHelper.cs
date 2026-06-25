using System;
using System.IO;

// =======================================================================================
// PathHelper — 文件路径辅助工具
// =======================================================================================
// 提供以下功能：
//   - GetPairingKey：根据输入目录生成用于文件配对的唯一 key（含子文件夹路径）
//   - GetUniqueFilePath：在输出目录中原子性地获取不冲突的文件路径
//   - TryReservePath：使用 FileMode.CreateNew 做操作系统级别的原子路径预留
// =======================================================================================

namespace LivePhotoBox.Services
{
    // 文件路径辅助工具
    internal static class PathHelper
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

        // 在输出目录中获取一个不冲突的文件路径，并原子性预留该路径。
        // 如果文件名已存在，自动追加 (2)、(3) 等后缀（与 Windows 资源管理器行为一致）。
        // 使用 FileMode.CreateNew 做原子性预留：
        // - 多线程同时请求同一个路径时，只有一个能成功创建，其余会自动找下一个可用名字
        // - 创建的 0 字节占位文件会被后续的实际写入（FileMode.Create / Delete+Move）覆盖或替换
        // - 无需 lock，文件系统级别的原子操作
        public static string GetUniqueFilePath(string directory, string fileName)
        {
            string path = Path.Combine(directory, fileName);
            if (TryReservePath(path))
                return path;

            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);

            for (int i = 2; i < 999; i++)
            {
                path = Path.Combine(directory, $"{nameWithoutExt} ({i}){ext}");
                if (TryReservePath(path))
                    return path;
            }

            // 极端情况：999 个同名文件都用完了，追加 GUID
            return Path.Combine(directory, $"{nameWithoutExt} ({Guid.NewGuid():N}){ext}");
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
