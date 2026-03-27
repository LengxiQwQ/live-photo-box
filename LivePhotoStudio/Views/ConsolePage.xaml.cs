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
            // 强行停止的 GUI 响应
            AppendLog("[Warning] 接收到中断信号，正在强行停止当前队列任务...", isError: true);
            AppendLog("[System] 任务已强行终止！", isError: true);

            // TODO: 后续在这里触发后端多线程任务的 CancellationToken.Cancel()
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            ConsoleOutput.Text = string.Empty;
            AppendLog("[System] 控制台输出已清空。");
        }

        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(ConsoleOutput.Text)) return;

            var dataPackage = new DataPackage();
            dataPackage.SetText(ConsoleOutput.Text);
            Clipboard.SetContent(dataPackage);
            AppendLog("[System] 控制台输出已复制到剪贴板。");
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