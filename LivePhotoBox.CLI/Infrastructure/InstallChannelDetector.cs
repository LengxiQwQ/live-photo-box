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

        /// <summary>WinGet portable 包安装。</summary>
        WinGet
    }

    /// <summary>
    /// 检测当前 CLI 副本的安装渠道，用于 --version / info 展示。
    /// 判定顺序：WinGet（ARP 注册表）→ Inno Setup（unins000.exe）→ 便携。
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

            // Inno Setup 安装版会在应用目录生成卸载器，便携版不会
            if (File.Exists(Path.Combine(dir, "unins000.exe")))
                return CliInstallChannel.InnoSetup;

            // GUI 便携包：GUI 主程序与 CLI 同目录
            if (File.Exists(Path.Combine(dir, "Live Photo Box.exe")))
                return CliInstallChannel.PortableBundle;

            return CliInstallChannel.PortableCli;
        }

        /// <summary>
        /// 返回安装渠道的展示文本。
        /// </summary>
        /// <param name="baseDirectory">运行目录，默认取 AppContext.BaseDirectory。</param>
        public static string GetChannelDisplay(string? baseDirectory = null) =>
            GetChannel(baseDirectory) switch
            {
                CliInstallChannel.WinGet => "WinGet",
                CliInstallChannel.InnoSetup => "Inno Setup (GUI + CLI)",
                CliInstallChannel.PortableBundle => "Portable (GUI + CLI)",
                _ => "Portable CLI-only"
            };
    }
}
