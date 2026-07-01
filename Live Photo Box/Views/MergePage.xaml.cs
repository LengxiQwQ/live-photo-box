/*
 * MergePage.xaml.cs
 *
 * 实况照片合并页面的代码后置。
 * 提供将分离的图片+视频合并为实况照片的功能。
 * 包含任务列表自动滚动、文件夹选择、全屏预览和错误详情提示。
 *
 * 对应 ViewModel：MergeViewModel
 *
 * 生命周期：
 *   - 构造函数 → 创建 TaskListAutoScroller，注册 Loaded/Unloaded
 *   - Loaded → 附加自动滚动器，绑定 ViewModel 事件
 *   - Unloaded → 分离自动滚动器，解绑事件
 *   - 用户操作（浏览文件夹、打开文件、预览等）通过事件处理
 */

using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;

namespace LivePhotoBox.Views
{
    public sealed partial class MergePage : Page
    {
        // 任务列表自动滚动器，在处理/扫描过程中保持当前任务可见
        private readonly TaskListAutoScroller _scroller;

        // 是否已绑定 ViewModel 事件，防止重复绑定
        private bool _eventsHooked;

        // 关联的 MergeViewModel
        public MergeViewModel ViewModel => AppViewModel.Instance.Merge;

        // 构造函数：初始化组件、创建自动滚动器、注册加载/卸载事件
        public MergePage()
        {
            InitializeComponent();

            _scroller = new TaskListAutoScroller(
                "Merge",
                isActive: () => ViewModel.IsProcessing || ViewModel.IsScanning,
                getTaskCount: () => ViewModel.Tasks.Count,
                getTaskAt: idx => ViewModel.Tasks[idx]);

            Loaded += MergePage_Loaded;
            Unloaded += MergePage_Unloaded;
        }

        // 输出格式下拉框加载完成后注入品牌说明副标题，并按最长协议名称固定宽度。
        // 收起时只显示名称（单行），展开时显示名称 + 灰色品牌说明（双行，在 Popup 中不影响面板高度）。
        private void ProtocolComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not ComboBox comboBox) return;

            // 防止 NavigationCacheMode="Required" 导致页面缓存后 Loaded 事件重复触发，
            // 再次执行会导致 item.Content 已从 string 变为 StackPanel，读取为空字符串而破坏显示。
            comboBox.Loaded -= ProtocolComboBox_Loaded;

            string[] names = new string[comboBox.Items.Count];
            string[] hintKeys = ["MergePage_Protocol_V1_Hint", "MergePage_Protocol_V2_Hint", "MergePage_Protocol_Oppo_Hint"];

            double maxNameWidth = 0;
            double fontSize = comboBox.FontSize > 0 && !double.IsNaN(comboBox.FontSize)
                ? comboBox.FontSize : 14.0;

            // 1. 强行锁定 ComboBox 外部框整体高度为标准 32px，防止任何由于内部元素变化引发的上下“抽搐”
            if (double.IsNaN(comboBox.Height))
            {
                comboBox.Height = 32;
            }

            // 2. 初始化：为每一项永久绑定一个固定结构的 StackPanel 容器
            for (int i = 0; i < comboBox.Items.Count && i < hintKeys.Length; i++)
            {
                if (comboBox.Items[i] is ComboBoxItem item)
                {
                    names[i] = (item.Content as string) ?? "";

                    // 测量最长文本宽度以固定控件总宽
                    var measureBlock = new TextBlock { Text = names[i], FontSize = fontSize, TextWrapping = TextWrapping.NoWrap };
                    measureBlock.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    maxNameWidth = Math.Max(maxNameWidth, measureBlock.DesiredSize.Width);

                    // 主标题：拿掉了之前坑爹的 Height=32 物理限制，恢复自然高，绝不发扁
                    var nameBlock = new TextBlock
                    {
                        Text = names[i],
                        FontSize = fontSize,
                        FontWeight = FontWeights.Normal // 初始默认不加粗
                    };

                    // 副标题：限制宽度并允许换行（防止撑宽面板），同时保留你最原始的 1px 上边距
                    string hint = ResourceService.GetString(hintKeys[i]);
                    var hintBlock = new TextBlock
                    {
                        Text = hint,
                        FontSize = 11,
                        Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                        Margin = new Thickness(0, 1, 0, 0),
                        TextWrapping = TextWrapping.Wrap,   // 允许长文本自动换行
                        MaxWidth = 200,                     // 限制最大宽度，防止横向撑爆下拉框
                        Visibility = Visibility.Collapsed   // 初始默认隐藏
                    };

                    // 堆叠容器：用 Spacing 控制两行紧凑字距，用 VerticalAlignment 让单行文本收起时在 32px 框里完美上下居中
                    var panel = new StackPanel
                    {
                        Spacing = 2,                                  // 两行字之间的呼吸间距
                        VerticalAlignment = VerticalAlignment.Center, // 确保收起时单行文字绝对居中
                        Children = { nameBlock, hintBlock }
                    };

                    item.Content = panel;

                    // 利用元组将内部控件的引用存进 Tag，方便事件中直接提取并秒刷属性
                    item.Tag = (nameBlock, hintBlock);
                }
            }

            if (maxNameWidth > 0)
                comboBox.Width = maxNameWidth + 64;

            // 3. 展开时：一键将所有项切为“展开态”（主标题加粗 + 显示副标题）
            comboBox.DropDownOpened += (_, _) =>
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.SemiBold;
                        hintBlock.Visibility = Visibility.Visible;
                    }
                }
            };

            // 统一的重置状态方法：将所有项切回“收起态”（主标题恢复常规细体 + 隐藏副标题）
            void ResetToCollapsedState()
            {
                foreach (var obj in comboBox.Items)
                {
                    if (obj is ComboBoxItem item && item.Tag is (TextBlock nameBlock, TextBlock hintBlock))
                    {
                        nameBlock.FontWeight = FontWeights.Normal;
                        hintBlock.Visibility = Visibility.Collapsed;
                    }
                }
            }

            // 4. 收起时（无论是正常选中收起，还是点击旁边空白处直接返回）：强制恢复常规细体
            // 此时外部显示框由于和内部共享同一个 panel 对象引用，属性一变，外部字体会瞬间同步变细！
            comboBox.DropDownClosed += (_, _) => ResetToCollapsedState();

            // 5. 选择项改变时也同步重置，双重保险
            comboBox.SelectionChanged += (_, _) => ResetToCollapsedState();
        }


        // 页面加载完成后附加自动滚动器，绑定 ViewModel 事件
        private void MergePage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(MergeTaskListView);

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll += OnAllCompleted;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;
        }

        // 页面卸载时分离自动滚动器，解绑 ViewModel 事件
        private void MergePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _scroller.NotifyPageUnloading();
            _scroller.Detach();

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        // 任务开始处理时通知自动滚动器定位
        private void OnTaskStarted(object? sender, MergeTask task) =>
            _scroller.NotifyTaskStarted(task.Index - 1);

        // 所有任务处理完成时通知自动滚动器
        private void OnAllCompleted(object? sender, EventArgs e) =>
            _scroller.NotifyAllCompleted(wasCancelled: ViewModel.WasStoppedByUser);

        // 响应 ViewModel 属性变更，通知自动滚动器状态变化
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsScanning))
            {
                if (ViewModel.IsScanning)
                    _scroller.NotifyScanStarting();
                else
                    _scroller.NotifyScanFinished();
            }
            else if (e.PropertyName == nameof(ViewModel.IsProcessing) && ViewModel.IsProcessing)
            {
                _scroller.NotifyProcessingStarting();
            }
            else if (e.PropertyName == nameof(ViewModel.IsPaused) && !ViewModel.IsPaused)
            {
                _scroller.NotifyProcessingResumed();
            }
        }

        // ── 文件操作 ──────────────────────────────────

        // 输入/输出路径文本框获得焦点时清空内容
        private void DirectoryBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox) textBox.Text = string.Empty;
        }

        // 浏览输入目录按钮点击：选择文件夹并更新 ViewModel
        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.InputDirectory = folder.Path;
        }

        // 浏览输出目录按钮点击：选择文件夹并更新 ViewModel
        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.OutputDirectory = folder.Path;
        }

        // 文件操作按钮点击：在资源管理器中打开文件所在位置
        private void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"MergePage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        // 文件组操作按钮点击：在资源管理器中打开文件组路径
        private void FileGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"MergePage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        private void MergeTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) { }

        // ── 全屏预览 ──────────────────────────────────

        // 缩略图按钮点击：在 Lightbox 中全屏预览文件
        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            var paths = ViewModel.Tasks.Select(t => t.ImagePath).ToList();
            int idx = paths.IndexOf(path);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(paths, idx);
        }

        // ── 错误详情提示 ──────────────────────────────────

        // 点击状态文本显示错误详情 TeachingTip
        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not MergeTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        // 错误详情提示关闭时清除目标引用
        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;

        // 跳转到合并相关设置
        private void GoToMergeSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWindow)
                mainWindow.NavigateToSettings("Merge");
        }
    }
}
