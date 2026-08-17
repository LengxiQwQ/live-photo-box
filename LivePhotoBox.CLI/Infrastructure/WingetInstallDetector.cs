/*
 * WingetInstallDetector.cs
 *
 * 检测当前 CLI 副本是否由 WinGet 安装（portable 包）。
 *
 *   - 判据：ARP 注册表 Uninstall 子键存在 WinGetPackageIdentifier=LengxiQwQ.LivePhotoBox，
 *     且 InstallLocation / TargetFullPath 指向当前运行目录
 *   - 这些值仅由 WinGet 写入，其它安装方式不会产生，判定无歧义
 */

using System;
using System.IO;
using Microsoft.Win32;

namespace LivePhotoBox.Cli.Infrastructure
{
    internal static class WingetInstallDetector
    {
        private const string PackageId = "LengxiQwQ.LivePhotoBox";

        private const string UninstallSubKey =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

        private const string Wow6432UninstallSubKey =
            @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

        /// <summary>
        /// 判断当前进程所在目录是否被 WinGet 作为 portable 包管理。
        /// </summary>
        /// <param name="baseDirectory">运行目录，默认取 AppContext.BaseDirectory。</param>
        /// <returns>true 表示由 WinGet 管理，内置更新应禁用。</returns>
        public static bool IsWingetManaged(string? baseDirectory = null)
        {
            try
            {
                var normalizedBase = Normalize(baseDirectory ?? AppContext.BaseDirectory);
                if (normalizedBase is null)
                    return false;

                return MatchesAnyRoot(Registry.CurrentUser.OpenSubKey(UninstallSubKey), normalizedBase)
                    || MatchesAnyRoot(Registry.LocalMachine.OpenSubKey(UninstallSubKey), normalizedBase)
                    || MatchesAnyRoot(Registry.LocalMachine.OpenSubKey(Wow6432UninstallSubKey), normalizedBase);
            }
            catch (Exception)
            {
                // 检测失败时按“非 WinGet 安装”处理，绝不让 --version / info 崩溃
                return false;
            }
        }

        private static bool MatchesAnyRoot(RegistryKey? uninstallRoot, string baseDirectory)
        {
            if (uninstallRoot is null)
                return false;

            try
            {
                foreach (var subKeyName in uninstallRoot.GetSubKeyNames())
                {
                    using var subKey = uninstallRoot.OpenSubKey(subKeyName);
                    if (subKey is null)
                        continue;

                    // 只有 WinGet 会写这个值；不存在则跳过
                    var packageId = subKey.GetValue("WinGetPackageIdentifier") as string;
                    if (!string.Equals(packageId, PackageId, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // 交叉验证：该 ARP 条目管理的确实是当前这份拷贝
                    if (PathMatches(subKey.GetValue("InstallLocation") as string, baseDirectory))
                        return true;
                    if (PathMatches(
                            Path.GetDirectoryName(subKey.GetValue("TargetFullPath") as string),
                            baseDirectory))
                        return true;
                }
            }
            catch (System.Security.SecurityException)
            {
                // 无权限读取注册表时不做判定，保持与“非 WinGet 安装”一致
            }
            catch (UnauthorizedAccessException)
            {
            }

            return false;
        }

        private static bool PathMatches(string? path, string baseDirectory)
        {
            var normalized = Normalize(path);
            return normalized is not null &&
                string.Equals(normalized, baseDirectory, StringComparison.OrdinalIgnoreCase);
        }

        private static string? Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            try
            {
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch
            {
                return null;
            }
        }
    }
}
