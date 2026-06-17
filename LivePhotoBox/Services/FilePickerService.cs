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
                LogService.FileOp($"Folder picked: {result?.Path ?? "(cancelled)"}");
                return result;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Failed to pick folder", LogLevel.Error, ex);
                return null;
            }
        }

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
