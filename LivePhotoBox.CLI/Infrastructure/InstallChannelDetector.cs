using System;
using System.IO;

namespace LivePhotoBox.Cli.Infrastructure
{
    /// <summary>CLI 安装渠道。</summary>
    internal enum CliInstallChannel
    {
        /// <summary>便携 zip（独立 CLI zip 或便携版解压）。</summary>
        PortableCli,

        /// <summary>GUI + CLI 便携包（Live Photo Box.exe 与 CLI 同目录）。</summary>
        PortableBundle,

        /// <summary>Inno Setup 安装版（setup.exe）。</summary>
        InnoSetup,

        /// <summary>Scoop 包管理器安装（scoop install，路径位于 ~\scoop\apps\）。</summary>
        Scoop,

        /// <summary>WinGet portable 包安装。</summary>
        WinGet
    }

    /// <summary>
    /// 检测当前 CLI 副本的安装渠道，用于 --version / --info 展示。
    /// 判定顺序：WinGet（ARP 注册表）→ Scoop（路径）→ Inno Setup（unins000.exe）→ 便携。
    /// </summary>
    internal static class InstallChannelDetector
    {
        /// <summary>
        /// 返回当前副本的安装渠道。
        /// </summary>
        /// <param name="baseDirectory">运行目录，默认取 AppContext.BaseDirectory。</param>
        public static CliInstallChannel GetChannel(string? baseDirectory = null)
        {
            var dir = baseDirectory ?? AppContext.BaseDirectory;

            if (WingetInstallDetector.IsWingetManaged(dir))
                return CliInstallChannel.WinGet;

            // Scoop 包管理器安装：无卸载器，按路径识别（~\scoop\apps\ 或 SCOOP 自定义根）
            if (IsScoopManaged(dir))
                return CliInstallChannel.Scoop;

            // Inno Setup 安装版会在应用目录生成卸载器，便携版不会
            if (File.Exists(Path.Combine(dir, "unins000.exe")))
                return CliInstallChannel.InnoSetup;

            // GUI 便携包：GUI 主程序与 CLI 同目录
            if (File.Exists(Path.Combine(dir, "Live Photo Box.exe")))
                return CliInstallChannel.PortableBundle;

            return CliInstallChannel.PortableCli;
        }

        /// <summary>
        /// 判断目录是否位于 Scoop 应用树内。
        /// Scoop 没有卸载器（卸载走 scoop uninstall），只能按路径识别：
        /// 应用被解压到 &lt;Scoop 根&gt;\apps\&lt;app&gt;\current（junction），
        /// BaseDirectory 无论保留 current 还是解析到版本子目录，都落在 apps\ 前缀下。
        /// 默认根 %USERPROFILE%\scoop（SCOOP 环境变量可自定义）；
        /// 全局安装（scoop install --global）在 %ProgramData%\scoop（SCOOP_GLOBAL 可自定义）。
        /// </summary>
        private static bool IsScoopManaged(string dir)
        {
            if (IsUnderScoopApps(dir, Environment.GetEnvironmentVariable("SCOOP"),
                    Environment.SpecialFolder.UserProfile))
                return true;

            return IsUnderScoopApps(dir, Environment.GetEnvironmentVariable("SCOOP_GLOBAL"),
                Environment.SpecialFolder.CommonApplicationData);
        }

        private static bool IsUnderScoopApps(string dir, string? envOverride,
            Environment.SpecialFolder defaultFolder)
        {
            // SCOOP 环境变量是完整的 Scoop 根目录（含 apps\shims 的直接父级）；
            // 无环境变量时按默认根拼装：%USERPROFILE%\scoop 或 %ProgramData%\scoop。
            string root = !string.IsNullOrEmpty(envOverride)
                ? envOverride
                : Path.Combine(Environment.GetFolderPath(defaultFolder), "scoop");
            if (string.IsNullOrEmpty(root))
                return false;

            string appsDir = Path.Combine(root, "apps");
            return dir.StartsWith(appsDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 返回安装渠道的展示文本。统一格式：&lt;渠道名&gt; (CLI-only | GUI + CLI)。
        /// </summary>
        /// <param name="baseDirectory">运行目录，默认取 AppContext.BaseDirectory。</param>
        public static string GetChannelDisplay(string? baseDirectory = null)
        {
            var dir = baseDirectory ?? AppContext.BaseDirectory;
            return GetChannel(dir) switch
            {
                // WinGet 包发布的是 CLI-only zip，固定为 CLI-only
                CliInstallChannel.WinGet => "WinGet (CLI-only)",
                CliInstallChannel.Scoop => IsGuiCoLocated(dir)
                    ? "Scoop (GUI + CLI)"
                    : "Scoop (CLI-only)",
                CliInstallChannel.InnoSetup => "Inno Setup (GUI + CLI)",
                CliInstallChannel.PortableBundle => "Portable (GUI + CLI)",
                // 枚举剩余值仅 PortableCli，这里只作防御性兜底
                _ => "Portable (CLI-only)"
            };
        }

        // GUI 主程序是否与 CLI 同目录（用于区分 便携/Scoop 包内含 GUI 与否）
        private static bool IsGuiCoLocated(string dir) =>
            File.Exists(Path.Combine(dir, "Live Photo Box.exe"));
    }
}
