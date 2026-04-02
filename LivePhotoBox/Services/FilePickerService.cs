using System;
using System.Diagnostics;
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
