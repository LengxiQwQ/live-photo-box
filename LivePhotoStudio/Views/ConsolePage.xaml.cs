using LivePhotoStudio.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using System;

namespace LivePhotoStudio.Views
{
    public sealed partial class ConsolePage : Page
    {
        public ConsolePage()
        {
            this.InitializeComponent();
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            AppendLog(ResourceService.GetString("ConsolePage_Log_StopRequested"), isError: true);
            AppendLog(ResourceService.GetString("ConsolePage_Log_StopForced"), isError: true);
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ConsoleOutput.Text = string.Empty;
            AppendLog(ResourceService.GetString("ConsolePage_Log_Cleared"));
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ConsoleOutput.Text)) return;

            var dataPackage = new DataPackage();
            dataPackage.SetText(ConsoleOutput.Text);
            Clipboard.SetContent(dataPackage);
            AppendLog(ResourceService.GetString("ConsolePage_Log_Copied"));
        }

        /// <summary>
        /// 提供给外部批量任务调用的日志打印方法
        /// </summary>
        public void AppendLog(string message, bool isError = false)
        {
            // 获取当前时间戳
            string timestamp = DateTime.Now.ToString("HH:mm:ss.fff");

            // 简单的终端日志拼接
            ConsoleOutput.Text += $"[{timestamp}] {message}{Environment.NewLine}";

            // 自动滚动到底部
            ConsoleOutput.SelectionStart = ConsoleOutput.Text.Length;
        }
    }
}