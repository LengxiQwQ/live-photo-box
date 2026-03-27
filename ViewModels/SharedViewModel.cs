using System.Windows.Input;
using Microsoft.UI.Xaml.Input;

public partial class SharedViewModel
{
    // 其他属性和方法...

    public ICommand RestoreDefaultSettingsCommand { get; }

    public SharedViewModel()
    {
        RestoreDefaultSettingsCommand = new RelayCommand(RestoreDefaultSettings);
    }

    private void RestoreDefaultSettings()
    {
        // 恢复默认设置的实现
    }
}