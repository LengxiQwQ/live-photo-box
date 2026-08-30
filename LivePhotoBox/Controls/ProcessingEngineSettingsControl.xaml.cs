using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LivePhotoBox.Controls
{
    public sealed partial class ProcessingEngineSettingsControl : UserControl
    {
        private bool _loading;
        public ObservableCollection<BackendProtocolRow> Rows { get; } = [];

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
            ProcessingBackendSettings settings = ProcessingBackendSettingsService.Load();
            ModeComboBox.SelectedIndex = (int)settings.Mode;
            if (ProtocolExpander.IsExpanded)
                PopulateRows(settings);
            else
                Rows.Clear();
            _loading = false;
        }

        private void PopulateRows(ProcessingBackendSettings? settings = null)
        {
            settings ??= ProcessingBackendSettingsService.Load();
            NativeRuntimeInfo runtime = NativeRuntime.Probe();
            Rows.Clear();
            foreach (ProcessingBackendProtocolDefinition definition in ProcessingBackendProtocolCatalog.All)
            {
                NativeBackendMaturity maturity = ProcessingBackendSettingsService.GetNativeMaturity(definition, runtime);
                Rows.Add(new BackendProtocolRow(
                    definition.Key,
                    ResourceService.GetString($"SettingsPage_ProcessingBackend_{ToResourceKey(definition.Key)}"),
                    ResourceService.GetString(maturity == NativeBackendMaturity.Unavailable
                        ? "SettingsPage_ProcessingBackend_Status_Unavailable"
                        : maturity == NativeBackendMaturity.Preview
                            ? "SettingsPage_ProcessingBackend_Status_Preview"
                            : "SettingsPage_ProcessingBackend_Status_Stable"),
                    settings.GetPreference(definition.Key) == ProcessingBackendPreference.PreferNative,
                    settings.Mode == ProcessingBackendMode.Custom && maturity != NativeBackendMaturity.Unavailable));
            }
        }

        private void ProtocolExpander_Expanded(object sender, EventArgs e)
        {
            _loading = true;
            PopulateRows();
            _loading = false;
        }

        private void ProtocolExpander_Collapsed(object sender, EventArgs e) => Rows.Clear();

        private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || ModeComboBox.SelectedIndex < 0) return;
            ProcessingBackendSettingsService.SetMode((ProcessingBackendMode)ModeComboBox.SelectedIndex);
        }

        private void ProtocolToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_loading || sender is not ToggleSwitch toggle || toggle.Tag is not string key) return;
            ProcessingBackendSettingsService.SetPreference(key, toggle.IsOn
                ? ProcessingBackendPreference.PreferNative
                : ProcessingBackendPreference.Legacy);
        }

        private static string ToResourceKey(string key) => key.Replace("-", "_");
    }

    public sealed class BackendProtocolRow : INotifyPropertyChanged
    {
        private bool _preferNative;
        public string Key { get; }
        public string DisplayName { get; }
        public string Status { get; }
        public bool IsSelectable { get; }
        public bool PreferNative
        {
            get => _preferNative;
            set { if (_preferNative != value) { _preferNative = value; PropertyChanged?.Invoke(this, new(nameof(PreferNative))); } }
        }

        public BackendProtocolRow(string key, string displayName, string status, bool preferNative, bool isSelectable) =>
            (Key, DisplayName, Status, _preferNative, IsSelectable) =
            (key, displayName, status, preferNative, isSelectable);

        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
