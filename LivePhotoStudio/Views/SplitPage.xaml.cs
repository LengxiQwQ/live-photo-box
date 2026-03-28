using LivePhotoStudio.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;

namespace LivePhotoStudio.Views
{
    public sealed partial class SplitPage : Page
    {
        // 独立的页面 UI 模拟列表
        public ObservableCollection<LivePhotoTask> SplitTasks { get; } = new();

        public SplitPage()
        {
            this.InitializeComponent();
        }

        private void Grid_DragOver(object _, DragEventArgs e)
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "释放以导入需要拆分的照片";
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
                        Details = "等待拆分..."
                    });
                }
            }
        }
    }
}