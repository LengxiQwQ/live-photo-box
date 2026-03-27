using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using System;
using System.IO;
using LivePhotoStudio.ViewModels;
using LivePhotoStudio.Models;

namespace LivePhotoStudio.Views
{
    public sealed partial class ComboPage : Page
    {
        public SharedViewModel ViewModel => SharedViewModel.Instance;

        public ComboPage()
        {
            this.InitializeComponent();
        }

        private void Grid_DragOver(object _, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "释放以导入";
        }

        private async void Grid_Drop(object _, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    ViewModel.ComboTasks.Add(new LivePhotoTask
                    {
                        FileName = item.Name,
                        Status = ProcessStatus.Pending,
                        Details = "等待处理..."
                    });
                }
            }
        }
    }
}