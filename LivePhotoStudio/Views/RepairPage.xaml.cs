using LivePhotoStudio.Models;
using LivePhotoStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;

namespace LivePhotoStudio.Views
{
    public sealed partial class RepairPage : Page
    {
        public ObservableCollection<LivePhotoTask> RepairTasks { get; } = new();

        public RepairPage()
        {
            this.InitializeComponent();
            this.Loaded += RepairPage_Loaded;
        }

        private async void RepairPage_Loaded(object sender, RoutedEventArgs e)
        {
            await TryLoadBackgroundImage();
        }

        private async System.Threading.Tasks.Task TryLoadBackgroundImage()
        {
            try
            {
                var folder = await Windows.ApplicationModel.Package.Current.InstalledLocation.GetFolderAsync("Assets");
                var file = await folder.TryGetItemAsync("anime_lineart_bg.jpg");

                if (file != null)
                {
                    BgImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/anime_lineart_bg.jpg"));
                }
            }
            catch
            {
            }
        }

        private void Grid_DragOver(object _, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = ResourceService.GetString("RepairPage_DragCaption");
        }

        private async void Grid_Drop(object _, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    RepairTasks.Add(new LivePhotoTask
                    {
                        FileName = item.Name,
                        Status = ProcessStatus.Pending,
                        Details = ResourceService.GetString("RepairPage_TaskPending")
                    });
                }
            }
        }
    }
}