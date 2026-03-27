using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoStudio.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using Windows.Storage;

namespace LivePhotoStudio.ViewModels
{
    public partial class SharedViewModel : ObservableObject
    {
        public static SharedViewModel Instance { get; } = new SharedViewModel();

        // === 状态与进度 ===
        [ObservableProperty] private string _appStatus = "就绪";
        [ObservableProperty] private double _comboProgress = 0;

        // === 实况照片合成参数 ===
        // 0: V1, 1: V2 
        [ObservableProperty] private int _selectedModeIndex = 1; // 默认为 1 (即 V2)

        public int[] ThreadOptions { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        [ObservableProperty] private int _threadCount = 4; // 默认为 4

        [ObservableProperty] private bool _keepOriginal = true;

        // === 实况照片拆解参数 ===
        // 0: MP4, 1: MOV
        [ObservableProperty] private int _splitVideoFormat = 1; // 默认改为 1 (MOV)

        // === 个性化参数 ===
        [ObservableProperty] private int _languageIndex = 0;
        [ObservableProperty] private int _elementTheme = 0;
        [ObservableProperty] private int _backdropIndex = 0;

        public ObservableCollection<LivePhotoTask> ComboTasks { get; } = new();

        public SharedViewModel()
        {
            LoadSettings();
            // 监听属性改变，自动保存设置
            PropertyChanged += OnPropertyChangedSave;
        }

        private void LoadSettings()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            if (settings.TryGetValue(nameof(SelectedModeIndex), out var mode)) SelectedModeIndex = (int)mode;
            if (settings.TryGetValue(nameof(ThreadCount), out var thread)) ThreadCount = (int)thread;
            if (settings.TryGetValue(nameof(KeepOriginal), out var keep)) KeepOriginal = (bool)keep;
            if (settings.TryGetValue(nameof(SplitVideoFormat), out var split)) SplitVideoFormat = (int)split;
            if (settings.TryGetValue(nameof(LanguageIndex), out var lang)) LanguageIndex = (int)lang;
            if (settings.TryGetValue(nameof(ElementTheme), out var theme)) ElementTheme = (int)theme;
            if (settings.TryGetValue(nameof(BackdropIndex), out var backdrop)) BackdropIndex = (int)backdrop;
        }

        private void OnPropertyChangedSave(object? sender, PropertyChangedEventArgs e)
        {
            // 忽略非设置相关的属性
            if (e.PropertyName == nameof(AppStatus) || e.PropertyName == nameof(ComboProgress)) return;

            var settings = ApplicationData.Current.LocalSettings.Values;
            var propertyInfo = GetType().GetProperty(e.PropertyName!);
            if (propertyInfo != null)
            {
                settings[e.PropertyName!] = propertyInfo.GetValue(this);
            }
        }

        [RelayCommand]
        private void RestoreDefaultSettings()
        {
            // 恢复所有默认设置
            LanguageIndex = 0;
            BackdropIndex = 0;
            ElementTheme = 0;
            SelectedModeIndex = 1; // V2
            ThreadCount = 4;
            KeepOriginal = true;
            SplitVideoFormat = 1; // MOV
        }

        [RelayCommand]
        private void StartCombo()
        {
            AppStatus = "正在处理...";
            // 处理逻辑...
        }
    }
}