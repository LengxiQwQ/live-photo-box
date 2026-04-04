using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace LivePhotoBox.Services
{
    public static class FilePickerService
    {
        public static async Task<StorageFolder?> PickFolderAsync()
        {
            var folderPicker = new FolderPicker();
            folderPicker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            return await folderPicker.PickSingleFolderAsync();
        }

        public static async Task OpenFileAsync(string path)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            await Windows.System.Launcher.LaunchFileAsync(file);
        }

        public static async Task<bool> ExportFileCopyAsync(string sourcePath, string suggestedFileName)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
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
                return false;
            }

            StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
            await sourceFile.CopyAndReplaceAsync(targetFile);
            return true;
        }

        public static void OpenFolderInExplorer(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            Directory.CreateDirectory(folderPath);

            var processStartInfo = new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"\"{folderPath}\"",
                UseShellExecute = true
            };

            Process.Start(processStartInfo);
        }

        public static void RevealInExplorer(string path)
        {
            var processStartInfo = new ProcessStartInfo("explorer.exe")
            {
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            };

            Process.Start(processStartInfo);
        }
    }
}
