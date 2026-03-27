using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;
using LivePhotoStudio.Models;

namespace LivePhotoStudio.ViewModels
{
    public partial class SharedViewModel : ObservableObject
    {
        public static SharedViewModel Instance { get; } = new SharedViewModel();

        // === 底部状态栏 ===
        [ObservableProperty] private string _appStatus = "就绪";

        // === 全局设置参数 ===
        [ObservableProperty] private string _selectedMode = "V2";
        [ObservableProperty] private int _threadCount = 8;
        [ObservableProperty] private bool _keepOriginal = true;

        // === 主题与材质控制 ===
        // 0: 跟随系统, 1: 浅色, 2: 深色
        [ObservableProperty] private int _elementTheme = 0;
        // 0: Mica (云母), 1: Mica Alt, 2: 亚克力 (Acrylic), 3: 纯色
        [ObservableProperty] private int _backdropIndex = 0;

        // === 合成页面进度 ===
        [ObservableProperty] private double _comboProgress = 0;

        // === 任务列表 (共享) ===
        public ObservableCollection<LivePhotoTask> ComboTasks { get; } = new();

        // === 合成命令 ===
        [RelayCommand]
        private void StartCombo()
        {
            AppStatus = "正在处理...";
            // TODO: 实现合成逻辑
        }
    }
}