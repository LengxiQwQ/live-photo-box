using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;

namespace LivePhotoBox.Models;

/// <summary>
/// 输出格式选项模型，供 MergePage 与 SplitPage 的格式下拉框绑定消费。
/// </summary>
public sealed partial class OutputFormatOptionItem : ObservableObject
{
    public int Id { get; init; }
    public string DisplayName { get; init; } = string.Empty;
    public string Hint { get; init; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FontWeight))]
    [NotifyPropertyChangedFor(nameof(HintVisibility))]
    private bool _isExpanded;

    public FontWeight FontWeight => IsExpanded ? FontWeights.SemiBold : FontWeights.Normal;

    public Visibility HintVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
}
