using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace LivePhotoBox.Controls;

public sealed partial class ProcessingEngineSettingsControl : UserControl
{
    private bool _loading;

    public ProcessingEngineSettingsControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ProcessingBackendSettingsService.Changed -= OnSettingsChanged;
        ProcessingBackendSettingsService.Changed += OnSettingsChanged;
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        ProcessingBackendSettingsService.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => Reload();

    private void Reload()
    {
        _loading = true;
        ProcessingVersionToggle.IsOn =
            ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt;
        _loading = false;
    }

    private void PipelineToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not ToggleSwitch toggle) return;
        ProcessingBackendSettingsService.SetMode(
            toggle.IsOn ? ProcessingPipelineMode.Rebuilt : ProcessingPipelineMode.Legacy);
    }
}
