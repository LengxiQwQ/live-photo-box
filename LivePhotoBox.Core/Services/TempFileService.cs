using System;
using System.IO;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 统一临时文件管理服务。
    /// GUI / CLI / Core 的所有中间临时文件都应通过本服务分配，
    /// 由本服务保证路径唯一（目录级 GUID 隔离 + 文件级 GUID 后缀），
    /// 从而避免并发任务互相覆盖或删除彼此的临时文件（历史竞态根因）。
    /// </summary>
    public static class TempFileService
    {
        /// <summary>
        /// 创建独立临时工作区，目录形如 {rootDir}/lpb_{scope}_{GUID:N}/。
        /// </summary>
        /// <param name="scope">用途标识，用于排障（如 "merge_task"）。</param>
        /// <param name="rootDir">工作区父目录；省略时使用系统临时目录。</param>
        /// <returns>可释放的临时工作区，使用完毕后应调用 <see cref="TempWorkspace.Dispose"/> 清理。</returns>
        public static TempWorkspace CreateWorkspace(string scope, string? rootDir = null)
        {
            string baseDir = string.IsNullOrEmpty(rootDir) ? Path.GetTempPath() : rootDir;
            string dir = Path.Combine(baseDir, $"lpb_{scope}_{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            return new TempWorkspace(dir);
        }

        /// <summary>
        /// 在指定目录内分配一个唯一临时文件路径，形如 {hint}_{GUID:N}.{ext}。
        /// </summary>
        /// <param name="directory">目标目录（须已存在）。</param>
        /// <param name="hint">文件名提示，仅用于排障（如 "heic"、"merge_trans"）。</param>
        /// <param name="extension">文件扩展名，可不含前导点。</param>
        /// <returns>唯一且尚不存在的临时文件路径。</returns>
        public static string AllocateTempPath(string directory, string hint, string extension)
        {
            string ext = extension.TrimStart('.');
            return Path.Combine(directory, $"{hint}_{Guid.NewGuid():N}.{ext}");
        }
    }

    /// <summary>
    /// 独立临时工作区：一个逻辑任务一个工作区，路径唯一且可整体清理。
    /// </summary>
    public sealed class TempWorkspace : IDisposable
    {
        /// <summary>工作区根目录（已创建）。</summary>
        public string RootPath { get; }

        internal TempWorkspace(string rootPath)
        {
            RootPath = rootPath;
        }

        /// <summary>
        /// 在工作区内分配一个唯一临时文件路径。
        /// </summary>
        /// <param name="hint">文件名提示，仅用于排障。</param>
        /// <param name="extension">文件扩展名，可不含前导点。</param>
        /// <returns>唯一且尚不存在的临时文件路径。</returns>
        public string AllocatePath(string hint, string extension)
            => TempFileService.AllocateTempPath(RootPath, hint, extension);

        /// <summary>
        /// 尽力递归删除工作区；失败仅记录不抛出（避免影响主流程）。
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                    Directory.Delete(RootPath, recursive: true);
            }
            catch
            {
                /* best effort */
            }
        }
    }
}
