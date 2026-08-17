/*
 * InstallChannelDetector.cs
 *
 * GUI 安装渠道检测。按卸载工具的身份识别当前安装包类型
 * （商店 / Inno Setup / Scoop / 便携版）。
 */

using System;
using System.Diagnostics;
using System.IO;

namespace LivePhotoBox.Services
{
    /// <summary>GUI 安装渠道。</summary>
    public enum GuiInstallChannel
    {
        /// <summary>微软商店版（MSIX 打包，仅 GUI）。</summary>
        Store,

        /// <summary>Inno Setup 安装版（setup.exe 安装，GUI + CLI 同目录）。</summary>
        InnoSetup,

        /// <summary>Scoop 包管理器安装（scoop install，位于 ~\scoop\apps\，无卸载器）。</summary>
        Scoop,

        /// <summary>便携版（zip 直接解压）。</summary>
        Portable
    }

    /// <summary>
    /// 检测当前 GUI 副本的安装渠道。
    /// 判定顺序：商店（MSIX）→ Inno Setup（按卸载器身份）→ 便携。
    /// 与 CLI 侧 InstallChannelDetector（LivePhotoBox.CLI/Infrastructure）互为镜像，
    /// 判定规则保持一致，避免两侧展示不一致。
    /// </summary>
    public static class InstallChannelDetector
    {
        /// <summary>
        /// 返回当前副本的安装渠道。
        /// </summary>
        /// <param name="baseDirectory">运行目录，默认取 AppContext.BaseDirectory。</param>
        public static GuiInstallChannel GetChannel(string? baseDirectory = null)
        {
            var dir = baseDirectory ?? AppContext.BaseDirectory;

            // 商店版：MSIX 打包，Package.Current 可用
            if (App.IsPackaged)
                return GuiInstallChannel.Store;

            // Inno Setup 安装版：应用目录存在 Inno Setup 卸载器（unins000.exe）
            if (IsInnoSetupUninstaller(dir))
                return GuiInstallChannel.InnoSetup;

            // Scoop 包管理器安装：无卸载器，按路径识别（~\scoop\apps\ 或 SCOOP 自定义根）
            if (IsScoopManaged(dir))
                return GuiInstallChannel.Scoop;

            return GuiInstallChannel.Portable;
        }

        /// <summary>
        /// 判断当前副本是否为 Inno Setup 安装版。
        /// </summary>
        public static bool IsInnoSetup() => IsInnoSetupUninstaller(AppContext.BaseDirectory);

        /// <summary>
        /// 按卸载器的身份判定目录是否为 Inno Setup 安装版。
        /// Inno Setup 卸载器（unins000.exe）的可机器校验特征：
        ///   1. 文件名恒为 unins000.exe，另带数据文件 unins000.dat
        ///   2. FileVersion 恒以 "51." 开头 —— Inno Setup 5.x/6.x 卸载器统一版本签名
        ///   3. FileDescription 为 "Setup/Uninstall"（可本地化，中文为 "安装/卸载"）
        /// ProductName / CompanyName 不可靠（第三方构建常为空），不做判定依据。
        ///
        /// 判定策略：文件名存在 且 版本号确认 "51." → 确认 Inno Setup；
        /// 版本信息缺失/损坏 → 宽松回退为 文件名 + 数据文件 同存 仍视为 Inno Setup，
        /// 保证卸载器版本信息被剥离的合法安装不会被误判为便携版。
        /// </summary>
        /// <param name="dir">应用安装目录。</param>
        public static bool IsInnoSetupUninstaller(string dir)
        {
            string uninstaller = Path.Combine(dir, "unins000.exe");
            if (!File.Exists(uninstaller))
                return false;

            try
            {
                var vi = FileVersionInfo.GetVersionInfo(uninstaller);
                if (vi.FileVersion != null &&
                    vi.FileVersion.StartsWith("51.", StringComparison.Ordinal))
                    return true;
            }
            catch
            {
                // 读不到版本信息 → 走下方宽松回退
            }

            // 宽松回退：文件名 + 数据文件同存仍视为 Inno Setup
            return File.Exists(Path.Combine(dir, "unins000.dat"));
        }

        /// <summary>
        /// 判断目录是否位于 Scoop 应用树内。
        /// Scoop 没有卸载器（卸载走 scoop uninstall），只能按路径识别：
        /// 应用被解压到 &lt;Scoop 根&gt;\apps\&lt;app&gt;\current（junction），
        /// BaseDirectory 无论保留 current 还是解析到版本子目录，都落在 apps\ 前缀下。
        /// 默认根 %USERPROFILE%\scoop（SCOOP 环境变量可自定义）；
        /// 全局安装（scoop install --global）在 %ProgramData%\scoop（SCOOP_GLOBAL 可自定义）。
        /// </summary>
        /// <param name="dir">应用安装目录。</param>
        public static bool IsScoopManaged(string dir)
        {
            if (IsUnderScoopApps(dir, Environment.GetEnvironmentVariable("SCOOP"),
                    Environment.SpecialFolder.UserProfile))
                return true;

            return IsUnderScoopApps(dir, Environment.GetEnvironmentVariable("SCOOP_GLOBAL"),
                Environment.SpecialFolder.CommonApplicationData);
        }

        /// <summary>
        /// 判断 dir 是否位于给定 Scoop 根的 apps 子目录下。
        /// </summary>
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
    }
}
