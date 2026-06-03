using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Services;

namespace LivePhotoBox.ViewModels
{
    public partial class SettingsViewModel : ViewModelBase
    {
        public override string? PageStatusTag => null;

        [ObservableProperty]
        private int _languageIndex;

        partial void OnLanguageIndexChanged(int value)
        {
            AppSettingsService.SetValue(nameof(LanguageIndex), value);
        }

        [ObservableProperty]
        private int _elementTheme;

        partial void OnElementThemeChanged(int value)
        {
            AppSettingsService.SetValue(nameof(ElementTheme), value);
        }

        [ObservableProperty]
        private int _backdropIndex;

        partial void OnBackdropIndexChanged(int value)
        {
            AppSettingsService.SetValue(nameof(BackdropIndex), value);
        }

        public SettingsViewModel()
        {
            LoadSettings();
        }

        private void LoadSettings()
        {
            LanguageIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            ElementTheme = AppSettingsService.GetValue(nameof(ElementTheme), 0);
            BackdropIndex = AppSettingsService.GetValue(nameof(BackdropIndex), 0);
        }

        [RelayCommand]
        private void RestoreDefaultSettings()
        {
            LanguageIndex = 0;
            BackdropIndex = 0;
            ElementTheme = 0;
        }
    }
}
