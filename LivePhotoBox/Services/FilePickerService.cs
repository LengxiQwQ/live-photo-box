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
    public static class FilePickerService
    {
        public static async Task<StorageFolder?> PickFolderAsync()
        {
            try
            {
                var folderPicker = new FolderPicker();
                folderPicker.FileTypeFilter.Add("*");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

                var result = await folderPicker.PickSingleFolderAsync();
                AppLogService.FileOp($"Folder picked: {result?.Path ?? "(cancelled)"}");
                return result;
            }
            catch (Exception ex)
            {
                AppLogService.FileOp($"Failed to pick folder", LogLevel.Error, ex);
                return null;
            }
        }

        public static async Task OpenFileAsync(string path)
        {
            try
            {
                AppLogService.FileOp($"Opening file: {path}");
                var file = await StorageFile.GetFileFromPathAsync(path);
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                AppLogService.FileOp($"Failed to open file: {path}", LogLevel.Error, ex);
            }
        }

        public static async Task<bool> OpenUriAsync(Uri uri)
        {
            try
            {
                if (uri == null)
                {
                    AppLogService.FileOp("OpenUri called with null URI", LogLevel.Warning);
                    return false;
                }

                AppLogService.FileOp($"Opening URI: {uri}");
                return await Windows.System.Launcher.LaunchUriAsync(uri);
            }
            catch (Exception ex)
            {
                AppLogService.FileOp($"Failed to open URI: {uri}", LogLevel.Error, ex);
                return false;
            }
        }

        public static async Task<bool> ExportFileCopyAsync(string sourcePath, string suggestedFileName)
        {
            try
            {
                AppLogService.FileOp($"Export file requested: {sourcePath} -> {suggestedFileName}");

                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    AppLogService.FileOp($"Source file not found: {sourcePath}", LogLevel.Warning);
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
                    AppLogService.FileOp("Export cancelled by user");
                    return false;
                }

                StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
                await sourceFile.CopyAndReplaceAsync(targetFile);
                AppLogService.FileOp($"File exported successfully: {targetFile.Path}");
                return true;
            }
            catch (Exception ex)
            {
                AppLogService.FileOp($"Failed to export file: {sourcePath}", LogLevel.Error, ex);
                return false;
            }
        }

        public static void OpenFolderInExplorer(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath))
                {
                    AppLogService.FileOp("OpenFolderInExplorer called with empty path", LogLevel.Warning);
                    return;
                }

                AppLogService.FileOp($"Opening folder in explorer: {folderPath}");
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
                AppLogService.FileOp($"Failed to open folder: {folderPath}", LogLevel.Error, ex);
            }
        }

        public static void RevealInExplorer(string path)
        {
            try
            {
                AppLogService.FileOp($"Revealing in explorer: {path}");
                var processStartInfo = new ProcessStartInfo("explorer.exe")
                {
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                };

                Process.Start(processStartInfo);
            }
            catch (Exception ex)
            {
                AppLogService.FileOp($"Failed to reveal in explorer: {path}", LogLevel.Error, ex);
            }
        }
    }
}
