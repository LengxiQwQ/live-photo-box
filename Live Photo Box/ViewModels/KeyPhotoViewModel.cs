/*
 * KeyPhotoViewModel.cs — 第七轮精修

 * 照片行用格式替代日期 · 日期移到协议行 · EXIF 去标题去 DisplayP3
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using System.Collections.ObjectModel;

namespace LivePhotoBox.ViewModels
{
    public partial class KeyPhotoViewModel : ViewModelBase
    {
        public KeyPhotoViewModel() => PopulateDesignTimeData();
        public override string? PageStatusTag => null;

        [ObservableProperty] private string _currentDirectory = @"C:\Users\Example\Pictures\LivePhotos";
        [ObservableProperty] private string _searchText = string.Empty;
        [ObservableProperty] private int _selectedSortIndex;
        public ObservableCollection<KeyPhotoFileItem> FileItems { get; } = new();

        // ── 左卡：照片格式 + 视频格式 + 协议 · 日期 ──
        [ObservableProperty] private string _photoFileName = "IMG_8842.HEIC";
        [ObservableProperty] private string _photoInfoLine = "4032 × 3024  │  6.3 MB  │  HEIC";
        [ObservableProperty] private string _videoInfoLine = "1920 × 1440  │  8.2 MB  │  H.265";
        [ObservableProperty] private string _protocolLine = "MotionPhoto V2  ·  2024/12/15 14:32";

        // ── 右区：设备名作标题，参数每行一个 ──
        [ObservableProperty] private string _exifCamera = "Apple iPhone 15 Pro";
        [ObservableProperty] private string _exifFocalLength = "24 mm";
        [ObservableProperty] private string _exifAperture = "f/1.8";
        [ObservableProperty] private string _exifShutterSpeed = "1/120 s";
        [ObservableProperty] private string _exifIso = "ISO 80";

        [ObservableProperty] private string _timelineInfo = "总时长：3.2 秒    共 48 帧";
        public int TimelineThumbnailCount => 14;

        [ObservableProperty] private bool _isModified;
        [RelayCommand] private void GoBack() { }
        [RelayCommand] private void Restore() { }
        [RelayCommand] private void Save() { IsModified = false; }
        [RelayCommand] private void SaveAs() { }
        [RelayCommand] private void Export() { }
        [RelayCommand] private void BrowseFolder() { }
        [RelayCommand] private void ViewFullProperties() { }

        private void PopulateDesignTimeData()
        {
            FileItems.Add(new() { FileName = "IMG_8842.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8842.HEIC", FileSize = "6.3 MB", Resolution = "4032 × 3024", DateTaken = "2024/12/15 14:32" });
            FileItems.Add(new() { FileName = "IMG_8843.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8843.HEIC", FileSize = "5.1 MB", Resolution = "3840 × 2160", DateTaken = "2024/12/15 13:18" });
            FileItems.Add(new() { FileName = "IMG_8845.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8845.HEIC", FileSize = "8.7 MB", Resolution = "4032 × 3024", DateTaken = "2024/12/14 09:45" });
            FileItems.Add(new() { FileName = "IMG_8850.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8850.HEIC", FileSize = "4.2 MB", Resolution = "3264 × 2448", DateTaken = "2024/12/13 17:02" });
            FileItems.Add(new() { FileName = "IMG_8852.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8852.HEIC", FileSize = "7.1 MB", Resolution = "4032 × 3024", DateTaken = "2024/12/12 11:30" });
            FileItems.Add(new() { FileName = "IMG_8860.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8860.HEIC", FileSize = "9.3 MB", Resolution = "3840 × 2160", DateTaken = "2024/12/10 16:55" });
            FileItems.Add(new() { FileName = "IMG_8871.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8871.HEIC", FileSize = "3.8 MB", Resolution = "3024 × 3024", DateTaken = "2024/12/09 08:12" });
            FileItems.Add(new() { FileName = "IMG_8875.HEIC", FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8875.HEIC", FileSize = "11.2 MB", Resolution = "4032 × 3024", DateTaken = "2024/12/08 14:20" });
        }
    }
}
