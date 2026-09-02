using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /*
     * FilePickerService.cs
     *
     * 文件选择与系统交互服务。封装 WinRT 文件/文件夹选择器、系统启动器 (Launcher)
     * 以及 Windows 资源管理器操作（打开文件夹、选中文件）等与操作系统交互的功能。
     * ViewModel 层通过此服务调用系统 UI，不直接依赖 WinRT API。
     */
    public static class FilePickerService
    {
        // 弹出系统文件夹选择对话框，让用户选择一个文件夹。
        // 返回选中的 StorageFolder，用户取消时返回 null。
        // 需要绑定 WinUI 3 主窗口句柄以正确显示模态对话框。
        // 返回: 用户选中的文件夹，取消则返回 null
        public static async Task<StorageFolder?> PickFolderAsync()
        {
            try
            {
                var folderPicker = new FolderPicker();
                folderPicker.FileTypeFilter.Add("*");

                // WinUI 3 要求通过 WinRT.Interop 绑定窗口句柄才能使对话框模态
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

                var result = await folderPicker.PickSingleFolderAsync();
                LogService.FileOp($"Folder picked: {result?.Path ?? "(cancelled)"}");
                return result;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Failed to pick folder", LogLevel.Error, ex);
                return null;
            }
        }

        // 使用系统默认关联程序打开指定文件。
        // 例如文本文件用记事本、图片用照片应用等。
        // path: 要打开的文件路径
        public static async Task OpenFileAsync(string path)
        {
            try
            {
                LogService.FileOp($"Opening file: {path}");
                var file = await StorageFile.GetFileFromPathAsync(path);
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Failed to open file: {path}", LogLevel.Error, ex);
            }
        }

        // 使用默认浏览器（或系统注册的协议处理程序）打开指定 URI。
        // 用于打开 GitHub Issues、外部链接等。
        // uri: 要打开的 URI
        // 返回: 启动成功返回 true
        public static async Task<bool> OpenUriAsync(Uri uri)
        {
            try
            {
                if (uri == null)
                {
                    LogService.FileOp("OpenUri called with null URI", LogLevel.Warning);
                    return false;
                }

                LogService.FileOp($"Opening URI: {uri}");
                return await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Failed to open URI: {uri}", LogLevel.Error, ex);
                return false;
            }
        }

        // 弹出"另存为"对话框，将源文件复制到用户选择的位置。
        // 用于导出日志文件、报告等。如果用户取消保存则返回 false。
        // sourcePath: 源文件的完整路径
        // suggestedFileName: 对话框中建议的文件名
        // 返回: 导出成功返回 true
        public static async Task<bool> ExportFileCopyAsync(string sourcePath, string suggestedFileName)
        {
            try
            {
                LogService.FileOp($"Export file requested: {sourcePath} -> {suggestedFileName}");

                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    LogService.FileOp($"Source file not found: {sourcePath}", LogLevel.Warning);
                    return false;
                }

                string extension = Path.GetExtension(suggestedFileName);
                if (string.IsNullOrWhiteSpace(extension))
                {
                    extension = ".log";
                }

                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedFileName),
                    DefaultFileExtension = extension
                };

                savePicker.FileTypeChoices.Add(
                    ResourceService.GetString("Picker_LogFileType"),
                    new List<string> { extension });

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

                StorageFile? targetFile = await savePicker.PickSaveFileAsync();
                if (targetFile == null)
                {
                    LogService.FileOp("Export cancelled by user");
                    return false;
                }

                StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                await sourceFile.CopyAndReplaceAsync(targetFile);
                LogService.FileOp($"File exported successfully: {targetFile.Path}");
                return true;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Failed to export file: {sourcePath}", LogLevel.Error, ex);
                return false;
            }
        }

        /// <summary>
        /// 弹出 Windows 原生"另存为"对话框，将源文件复制到用户选择的位置。
        /// 支持图片和视频文件类型。用户取消时返回 null。
        /// </summary>
        /// <param name="sourcePath">源文件完整路径</param>
        /// <returns>保存后的目标文件路径，用户取消则返回 null</returns>
        public static async Task<string?> SaveFileAsAsync(string sourcePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    LogService.FileOp($"SaveFileAs: source not found: {sourcePath}", LogLevel.Warning);
                    return null;
                }

                var fileName = Path.GetFileName(sourcePath);
                var extension = Path.GetExtension(sourcePath);
                var nameWithoutExt = Path.GetFileNameWithoutExtension(sourcePath);

                LogService.FileOp($"SaveFileAs requested: {sourcePath}");

                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFileName = nameWithoutExt
                };

                // 根据扩展名添加对应的文件类型选项
                var extLower = extension.ToLowerInvariant();
                var displayName = extLower switch
                {
                    ".jpg" or ".jpeg" => "JPEG 图像",
                    ".png" => "PNG 图像",
                    ".heic" or ".heif" => "HEIC 图像",
                    ".bmp" => "BMP 图像",
                    ".gif" => "GIF 图像",
                    ".tiff" or ".tif" => "TIFF 图像",
                    ".webp" => "WebP 图像",
                    ".mov" => "MOV 视频",
                    ".mp4" => "MP4 视频",
                    _ => $"{extLower.TrimStart('.').ToUpperInvariant()} 文件"
                };
                savePicker.FileTypeChoices.Add(displayName, new List<string> { extLower.ToUpperInvariant() });

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

                var targetFile = await savePicker.PickSaveFileAsync();
                if (targetFile == null)
                {
                    LogService.FileOp("SaveFileAs cancelled by user");
                    return null;
                }

                // 复制源文件到目标位置（覆盖已存在的文件）
                var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                await sourceFile.CopyAndReplaceAsync(targetFile);

                LogService.FileOp($"SaveFileAs success: {sourcePath} -> {targetFile.Path}");
                return targetFile.Path;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"SaveFileAs failed: {sourcePath}", LogLevel.Error, ex);
                return null;
            }
        }

        /// <summary>
        /// 弹出"另存为"对话框，将 JPEG 源文件保存到用户选择的位置。
        /// 固定使用 .jpg 扩展名，建议文件名由调用方指定（不含扩展名）。
        /// </summary>
        /// <param name="sourcePath">JPEG 源文件完整路径</param>
        /// <param name="suggestedFileName">建议文件名（不含扩展名）</param>
        /// <returns>保存后的目标文件路径，用户取消则返回 null</returns>
        public static async Task<string?> SaveFileAsJpegAsync(string sourcePath, string suggestedFileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    LogService.FileOp($"SaveFileAsJpeg: source not found: {sourcePath}", LogLevel.Warning);
                    return null;
                }

                LogService.FileOp($"SaveFileAsJpeg requested: {sourcePath}, suggestedName={suggestedFileName}");

                var savePicker = new FileSavePicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    SuggestedFileName = suggestedFileName
                };
                savePicker.FileTypeChoices.Add("JPEG 图像", new List<string> { ".JPG" });

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

                var targetFile = await savePicker.PickSaveFileAsync();
                if (targetFile == null)
                {
                    LogService.FileOp("SaveFileAsJpeg cancelled by user");
                    return null;
                }

                var sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                await sourceFile.CopyAndReplaceAsync(targetFile);

                LogService.FileOp($"SaveFileAsJpeg success: {sourcePath} -> {targetFile.Path}");
                return targetFile.Path;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"SaveFileAsJpeg failed: {sourcePath}", LogLevel.Error, ex);
                return null;
            }
        }

        /// <summary>
        /// 打开"另存为"对话框，根据源文件类型提供格式选择。
        /// 源文件非 JPEG 时，提供"原格式" + "JPEG"两个选项，默认选中原格式。
        /// 源文件已是 JPEG 时只提供 JPEG 选项。
        /// </summary>
        /// <param name="sourceExtension">源文件扩展名（含点，如 ".HEIC"）</param>
        /// <param name="suggestedFileName">建议文件名（不含扩展名）</param>
        /// <param name="jpegOption">是否同时提供 JPEG 选项（默认 true）</param>
        /// <returns>用户选择的 StorageFile（含所选扩展名），取消则返回 null</returns>
        public static async Task<StorageFile?> PickSaveFileForExportAsync(
            string sourceExtension, string suggestedFileName, bool jpegOption = true)
        {
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = suggestedFileName
            };

            var extLower = sourceExtension.ToLowerInvariant();
            var extUpper = sourceExtension.ToUpperInvariant(); // 后缀统一大写
            bool isJpeg = extLower is ".jpg" or ".jpeg";

            if (!isJpeg)
            {
                // 原格式
                string originalLabel = extLower switch
                {
                    ".heic" or ".heif" => "HEIC 图像（原格式）",
                    ".png" => "PNG 图像（原格式）",
                    ".bmp" => "BMP 图像（原格式）",
                    ".tiff" or ".tif" => "TIFF 图像（原格式）",
                    _ => $"{extUpper.TrimStart('.')}（原格式）"
                };
                savePicker.FileTypeChoices.Add(originalLabel, new List<string> { extUpper });

                // JPEG 选项（可关闭，如 Apple 保存只需 HEIC）
                if (jpegOption)
                    savePicker.FileTypeChoices.Add("JPEG 图像", new List<string> { ".JPG" });
            }
            else
            {
                savePicker.FileTypeChoices.Add("JPEG 图像", new List<string> { ".JPG" });
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            var targetFile = await savePicker.PickSaveFileAsync();
            if (targetFile == null)
                LogService.FileOp("PickSaveFileForExport cancelled by user");

            return targetFile;
        }

        /// <summary>
        /// 多格式导出另存为对话框 — 提供全部 6 种图片格式供用户选择。
        /// </summary>
        /// <param name="suggestedFileName">建议文件名（不含扩展名）</param>
        /// <returns>用户选择的 StorageFile，取消则返回 null</returns>
        public static async Task<StorageFile?> PickSaveFileForExportMultiFormatAsync(string suggestedFileName)
        {
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = suggestedFileName
            };

            savePicker.FileTypeChoices.Add("JPEG 图像", new List<string> { ".JPG", ".JPEG" });
            savePicker.FileTypeChoices.Add("PNG 图像", new List<string> { ".PNG" });
            if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
                savePicker.FileTypeChoices.Add("HEIC 图像", new List<string> { ".HEIC", ".HEIF" });
            else
                savePicker.FileTypeChoices.Add("WebP 图像", new List<string> { ".WEBP" });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            return await savePicker.PickSaveFileAsync();
        }

        /// <summary>
        /// GIF 导出另存为对话框。
        /// </summary>
        public static async Task<StorageFile?> PickSaveFileForGifExportAsync(string suggestedFileName = "animated")
        {
            var savePicker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                SuggestedFileName = suggestedFileName
            };

            savePicker.FileTypeChoices.Add("GIF 动画", new List<string> { ".GIF" });

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);

            return await savePicker.PickSaveFileAsync();
        }

        // 在 Windows 资源管理器中打开指定文件夹。
        // 如果文件夹不存在则自动创建。用于快速定位日志目录、输出目录等。
        // folderPath: 要打开的文件夹路径
        public static void OpenFolderInExplorer(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    LogService.FileOp("OpenFolderInExplorer called with empty path", LogLevel.Warning);
                    return;
                }

                LogService.FileOp($"Opening folder in explorer: {folderPath}");
                Directory.CreateDirectory(folderPath);

                var processStartInfo = new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"\"{folderPath}\"",
                    UseShellExecute = true
                };

                Process.Start(processStartInfo);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Failed to open folder: {folderPath}", LogLevel.Error, ex);
            }
        }

        // 在 Windows 资源管理器中打开指定文件或文件夹所在的目录，并选中该条目。
        // 相当于右键菜单中的"打开文件所在位置"。
        // path: 要定位的文件或文件夹路径
        public static void RevealInExplorer(string path)
        {
            try
            {
                LogService.FileOp($"Revealing in explorer: {path}");
                var processStartInfo = new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                };

                Process.Start(processStartInfo);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Failed to reveal in explorer: {path}", LogLevel.Error, ex);
            }
        }
    }
}
