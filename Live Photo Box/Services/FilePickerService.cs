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
    // 文件选择与系统交互服务 — 封装 WinRT 文件/文件夹选择器、系统启动器 (Launcher)
    // 以及 Windows 资源管理器操作（打开文件夹、选中文件）等与操作系统文件管理交互的功能。
    // ViewModel 层通过此服务调用系统 UI，不直接依赖 WinRT API。
    public static class FilePickerService
    {
        // 弹出系统文件夹选择对话框，让用户选择一个文件夹。
        // 返回选中的 StorageFolder，用户取消时返回 null。
        // 需要绑定 WinUI 3 主窗口句柄以正确显示模态对话框。
        // è¿å: 用户选中的文件夹，取消则返回 null
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
        // è¿å: 启动成功返回 true
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
        // è¿å: 导出成功返回 true
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
