/*
 * KeyPhotoTask.cs
 *
 * 实况照片主图更换任务的单项数据模型。
 * 每个 KeyPhotoTask 代表队列中的一个处理任务，
 * 包含文件信息、处理状态、进度、缩略图及耗时等显示数据。
 *
 * 属性变更自动通知 UI 绑定（继承自 ObservableObject）。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Models
{
    public partial class KeyPhotoTask : ObservableObject
    {
        // 文件名（含扩展名），如 "IMG_001.HEIC"。
        [ObservableProperty]
        private string _fileName = string.Empty;

        // 文件完整路径，如 "C:\Photos\IMG_001.HEIC"。
        [ObservableProperty]
        private string _filePath = string.Empty;

        // 文件大小格式化文本，如 "6.3 MB"。
        [ObservableProperty]
        private string _fileSize = string.Empty;

        // 当前处理状态：Pending / Processing / Success / Failed。
        [ObservableProperty]
        private ProcessStatus _status = ProcessStatus.Pending;

        // 处理进度（0.0 ~ 100.0）。
        [ObservableProperty]
        private double _progress;

        // 已用时间格式化文本，如 "00:02"。
        [ObservableProperty]
        private string _elapsedTime = string.Empty;

        // 预计剩余时间格式化文本，如 "00:15"。
        [ObservableProperty]
        private string _remainingTime = string.Empty;

        // 状态描述文本，UI 直接显示。
        // 根据 Status 自动生成：Pending → "Waiting..."，
        // Processing → "Processing..."，Success → "Done"，Failed → "Failed"。
        [ObservableProperty]
        private string _statusText = "Waiting...";

        // 任务序号（队列中的位置，从 1 开始）。
        [ObservableProperty]
        private int _index;

        // 缩略图（64×64），可为 null（显示占位图标）。
        [ObservableProperty]
        private ImageSource? _thumbnail;
    }
}
