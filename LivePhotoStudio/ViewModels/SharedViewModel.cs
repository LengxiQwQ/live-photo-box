using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoStudio.Models;
using System.Collections.ObjectModel;
using System.Net.NetworkInformation;

namespace LivePhotoStudio.ViewModels
{
    public partial class SharedViewModel : ObservableObject
    {
        public static SharedViewModel Instance { get; } = new SharedViewModel();

        // === 状态与进度 ===
        [ObservableProperty] private string _appStatus = "就绪";
        [ObservableProperty] private double _comboProgress = 0;

        // === 转换引擎参数 ===
        // 0: V2 (现代), 1: V1 (兼容)
        [ObservableProperty] private int _selectedModeIndex = 0;
        [ObservableProperty] private int _threadCount = 8;
        [ObservableProperty] private bool _keepOriginal = true;

        // === 个性化参数 ===
        // 0: 中文, 1: English
        [ObservableProperty] private int _languageIndex = 0;
        // 0: 跟随系统, 1: 浅色, 2: 深色
        [ObservableProperty] private int _elementTheme = 0;
        // 0: Mica, 1: Mica Alt, 2: Acrylic, 3: None
        [ObservableProperty] private int _backdropIndex = 0;

        public ObservableCollection<LivePhotoTask> ComboTasks { get; } = new();

        [RelayCommand]
        private void StartCombo()
        {
            AppStatus = "正在处理...";
            // 处理逻辑...
        }
    }
}