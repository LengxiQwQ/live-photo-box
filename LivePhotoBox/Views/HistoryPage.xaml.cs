using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LivePhotoBox.Views
{
    public sealed partial class HistoryPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public HistoryViewModel ViewModel => AppViewModel.Instance.History;

        public HistoryPage()
        {
            InitializeComponent();
        }

        private async void SelectFolder_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder == null) return;

            ViewModel.SelectedFolder = folder.Path;
            FolderPathText.Text = folder.Path;
        }
    }
}
