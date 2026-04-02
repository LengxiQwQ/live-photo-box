using LivePhotoStudio.Models;
using LivePhotoStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;

namespace LivePhotoStudio.Views
{
    public sealed partial class SplitPage : Page
    {
        public ObservableCollection<LivePhotoTask> SplitTasks { get; } = new();

        public SplitPage()
        {
            this.InitializeComponent();
        }

        private void Grid_DragOver(object _, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = ResourceService.GetString("SplitPage_DragCaption");
        }

        private async void Grid_Drop(object _, DragEventArgs e)
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var item in items)
                {
                    SplitTasks.Add(new LivePhotoTask
                    {
                        FileName = item.Name,
                        Status = ProcessStatus.Pending,
                        Details = ResourceService.GetString("Task_Pending")
                    });
                }
            }
        }
    }
}