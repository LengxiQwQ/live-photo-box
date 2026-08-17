/*
 * AboutViewModel.cs
 *
 * AboutPage（关于页）的视图模型，提供版本信息、链接跳转与 GitHub 头像缓存。
 *
 *   - 暴露应用版本号与分发渠道（商店版/安装版/便携版）
 *   - 提供更新记录（Changelog）链接，中英文跳转不同页面
 *   - 自动下载并本地缓存 GitHub 用户头像
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.System;

namespace LivePhotoBox.ViewModels
{
    public partial class AboutViewModel : ViewModelBase
    {
        #region Properties

        // <inheritdoc/>
        public override string? PageStatusTag => null;

        // 当前应用版本号
        public string AppVersion { get; }

        // 当前应用分发渠道（商店版 / 安装版 / 便携版）
        public string AppDistribution { get; }

        // 更新记录链接（中英文跳不同页面）
        public string ChangelogUrl =>
            LanguageService.IsChineseUi()
                ? "https://github.com/LengxiQwQ/live-photo-box/blob/master/changelogs/CHANGELOG.zh-CN.md"
                : "https://github.com/LengxiQwQ/live-photo-box/blob/master/changelogs/CHANGELOG.md";

        // GitHub 用户头像（自动下载并缓存到本地）
        [ObservableProperty]
        private ImageSource? _avatarSource;

        #endregion

        #region Commands

        // 在默认浏览器中打开指定链接。
        // 使用 System.Diagnostics.Process.Start（原生 Win32 进程启动）
        // 替代 Windows.System.Launcher.LaunchUriAsync（WinRT COM 跨进程调用），
        // 彻底避免 WinRT 异步边界导致的 Composition 渲染闪烁。
        [RelayCommand]
        private void OpenLink(string url)
        {
            if (!string.IsNullOrEmpty(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = url,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    // 静默——打开链接失败不影响应用功能
                }
            }
        }

        // 在应用内弹窗展示 GitHub 更新日志（先抓取 markdown，再用 WebView2 内联渲染）
        [RelayCommand]
        private async Task ShowChangelogAsync()
        {
            var xamlRoot = App.MainWindow?.Content?.XamlRoot;
            if (xamlRoot != null)
                await ChangelogDialogService.ShowAsync(xamlRoot);
        }

        // 在应用内弹窗展示 CLI 使用手册（优先读本地文件，缺失时从 GitHub 抓取，WebView2 渲染）
        [RelayCommand]
        private async Task ShowCliManualAsync()
        {
            var xamlRoot = App.MainWindow?.Content?.XamlRoot;
            if (xamlRoot != null)
                await CliManualDialogService.ShowAsync(xamlRoot);
        }

        #endregion

        #region Constructor

        public AboutViewModel()
        {
            AppVersion = GetAppVersion();
            AppDistribution = GetAppDistribution();

            // 后台自动加载 GitHub 头像
            _ = LoadAvatarAsync();
        }

        #endregion

        #region Methods

        /// <summary>
        /// 获取当前应用版本号
        /// </summary>
        private static string GetAppVersion()
        {
            // 统一走 App.DisplayVersion：展示用版本只保留前 3 段（主.次.修订）。
            // 完整 4 段 App.AppVersion 仍用于读取与更新比较，UI 一律显示 3 位。
            var version = App.DisplayVersion;
            // "0.0.0" 是 App.AppVersion 的最后兜底——程序集版本也读不到时返回，
            // 不应向用户展示一个假版本号
            if (version == "0.0.0")
                return ResourceService.GetString("AboutPage_VersionUnknown");
            return version;
        }

        /// <summary>
        /// 判断当前应用的分发渠道，统一走 App.DeploymentModeResourceKey。
        /// 商店版 / 安装版（Inno Setup）/ 便携版 三者由 InstallChannelDetector 按卸载器身份区分
        /// （见 Services/InstallChannelDetector.cs）。
        /// 末尾追加 CLI 附带标注：安装版/便携版把 CLI（livephotobox-boot.exe）与 GUI 放同一目录，
        /// 商店版打包目录没有 CLI，据此区分「含 CLI」与「仅 GUI」（与 CLI 侧 InstallChannelDetector 互为镜像）。
        /// </summary>
        private static string GetAppDistribution()
        {
            string mode = ResourceService.GetString(App.DeploymentModeResourceKey);

            bool hasCli = File.Exists(Path.Combine(AppContext.BaseDirectory, "livephotobox-boot.exe"));
            string cliSuffix = hasCli
                ? ResourceService.GetString("AboutPage_Mode_HasCli")
                : ResourceService.GetString("AboutPage_Mode_GuiOnly");
            return mode + cliSuffix;
        }

        /// <summary>
        /// 从 GitHub 获取用户头像并缓存到本地。
        /// 缓存与当前版本绑定，版本变化时自动重新下载。
        /// 覆盖所有边界情况：
        ///   - 首次运行 / 缓存缺失 → 下载
        ///   - App 更新（版本对不上）→ 重新下载
        ///   - 缓存文件损坏（解码失败）→ 删除缓存，下次启动重试
        ///   - 下载中途失败 → 不写缓存文件，下次启动重试
        ///   - 下载成功但版本文件写入失败 → 不写版本文件，下次启动重试
        ///   - 网络不可用 → 静默降级显示占位头像
        /// </summary>
        private async Task LoadAvatarAsync()
        {
            try
            {
                string cacheDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LivePhotoBox", "Cache");
                Directory.CreateDirectory(cacheDir);

                string avatarPath = Path.Combine(cacheDir, "avatar.png");
                string versionPath = Path.Combine(cacheDir, "avatar.version");

                // 读取缓存版本号（文件不存在 = null → 触发下载）
                string? cachedVersion = File.Exists(versionPath)
                    ? await File.ReadAllTextAsync(versionPath)
                    : null;

                // 条件：版本不匹配 或 头像文件缺失 → 下载
                // 下载失败不会留下缓存文件，下次启动条件仍成立 → 自动重试
                if (cachedVersion != AppVersion || !File.Exists(avatarPath))
                {
                    await DownloadAvatarAsync(avatarPath, versionPath);
                }

                // 尝试加载缓存头像
                if (File.Exists(avatarPath))
                {
                    // 快速完整性检查：空文件直接删掉走重试
                    if (new FileInfo(avatarPath).Length == 0)
                    {
                        CleanCache(avatarPath, versionPath);
                        return;
                    }

                    var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                    if (dispatcher != null)
                    {
                        dispatcher.TryEnqueue(async () =>
                        {
                            try
                            {
                                using var fileStream = File.OpenRead(avatarPath);
                                using var randomAccessStream = fileStream.AsRandomAccessStream();
                                var bitmap = new BitmapImage();
                                // 指定解码宽度为 160（2× 显示尺寸），
                                // 避免大图直接压缩到 80×80 时产生锯齿边缘
                                bitmap.DecodePixelWidth = 160;
                                await bitmap.SetSourceAsync(randomAccessStream);
                                AvatarSource = bitmap;
                            }
                            catch
                            {
                                // 解码失败 → 缓存文件损坏，清除缓存让下次启动重新下载
                                CleanCache(avatarPath, versionPath);
                            }
                        });
                    }
                }
            }
            catch
            {
                // 完全静默——头像加载失败不影响任何功能
            }
        }

        /// <summary>
        /// 删除损坏的头像缓存，确保下次启动触发重新下载。
        /// </summary>
        private static void CleanCache(string avatarPath, string versionPath)
        {
            try { if (File.Exists(avatarPath)) File.Delete(avatarPath); } catch { }
            try { if (File.Exists(versionPath)) File.Delete(versionPath); } catch { }
        }

        /// <summary>
        /// 通过 GitHub API 下载用户头像并写入缓存（原子写入）。
        /// 先下载到 .tmp 临时文件，全部成功后原子替换，避免写入一半时崩溃留下残缺文件。
        /// 任何步骤失败都不会留下缓存文件 → 下次启动自动重试。
        /// </summary>
        private static async Task DownloadAvatarAsync(string avatarPath, string versionPath)
        {
            string tempPath = avatarPath + ".tmp";
            try
            {
                // 清理上次残留的临时文件
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }

                using var httpClient = new HttpClient();
                httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LivePhotoBox");
                httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github.v3+json");

                // 先获取 avatar_url
                string json = await httpClient.GetStringAsync("https://api.github.com/users/LengxiQwQ");
                string? avatarUrl = null;
                using (var doc = JsonDocument.Parse(json))
                {
                    avatarUrl = doc.RootElement.GetProperty("avatar_url").GetString();
                }

                if (string.IsNullOrEmpty(avatarUrl))
                    return;

                // 下载头像到临时文件
                byte[] imageBytes = await httpClient.GetByteArrayAsync(avatarUrl);
                await File.WriteAllBytesAsync(tempPath, imageBytes);

                // 先写版本文件，再原子替换头像文件。
                // 顺序：版本文件写入成功后才交换，避免写出半截的残缺缓存。
                string currentVersion = GetAppVersion();
                await File.WriteAllTextAsync(versionPath, currentVersion);
                File.Move(tempPath, avatarPath, overwrite: true);

            }
            catch
            {
                // 任何步骤失败 → 清理可能留下的残缺文件 → 下次启动自动重试
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        #endregion
    }
}
