/*
 * ImageHoverService.cs
 *
 * 图片悬停预览服务。监听已注册 Border 的鼠标进入/离开事件，
 * 在鼠标悬停时于覆盖层 Canvas 上显示该图片按比例缩放的大图预览。
 */

using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Helpers
{
    // 图片悬停预览服务。监听已注册 Border 的鼠标进入/离开事件，
    // 在鼠标悬停时于覆盖层 Canvas 上显示该图片的大图预览，
    // 图片按比例缩放至不超过窗口指定比例且不超过原始尺寸。
    // 实现 IDisposable 以在销毁时清理所有事件订阅。
    public sealed class ImageHoverService : IDisposable
    {
        private readonly Canvas _overlay;                          // 预览覆盖层 Canvas
        private readonly Border _previewBorder;                    // 预览图片的边框容器
        private readonly Image _previewImage;                      // 预览图片控件
        private readonly double _maxWindowRatio;                   // 预览最大占窗口比例
        private readonly double _margin;                           // 预览距窗口边缘的边距
        private readonly Dictionary<string, (double Width, double Height)> _imageSizes = new(); // 缓存已知图片尺寸（名称 -> 宽高）
        private readonly List<Border> _registeredBorders = new();  // 已注册的 Border 列表，用于 Dispose 时统一清理

        private bool _isHoverActive;                               // 当前是否正在显示预览，防止重复触发

        // 初始化 ImageHoverService。
        // overlay: 用于承载预览的 Canvas 覆盖层。
        // previewBorder: 包裹预览图片的 Border 控件。
        // previewImage: 用于显示大图的 Image 控件。
        // maxWindowRatio: 预览图片最大尺寸占窗口的比例，默认 0.5（即不超过窗口宽高的 50%）。
        // margin: 预览图片距窗口左上角的边距（像素），默认 20。
        // 抛出 ArgumentNullException：overlay、previewBorder 或 previewImage 为 null 时。
        public ImageHoverService(Canvas overlay, Border previewBorder, Image previewImage,
            double maxWindowRatio = 0.5, double margin = 20.0)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _previewBorder = previewBorder ?? throw new ArgumentNullException(nameof(previewBorder));
            _previewImage = previewImage ?? throw new ArgumentNullException(nameof(previewImage));
            _maxWindowRatio = maxWindowRatio;
            _margin = margin;
        }

        // 注册一个 Border 控件，使其在鼠标悬停时显示图片预览。
        // 该 Border 的 Child 必须为 Image 控件且 Source 非空。
        // border: 要注册的 Border 控件。
        public void Register(Border border)
        {
            if (border == null) return;
            _registeredBorders.Add(border);
            border.PointerEntered += OnPointerEntered;
            border.PointerExited += OnPointerExited;
        }

        // 取消注册 Border 控件，移除其悬停事件监听。
        // border: 要取消注册的 Border 控件。
        public void Unregister(Border border)
        {
            if (border == null) return;
            _registeredBorders.Remove(border);
            border.PointerEntered -= OnPointerEntered;
            border.PointerExited -= OnPointerExited;
        }

        // 鼠标进入 Border 时触发：从缓存或 ActualWidth/ActualHeight 获取图片原始尺寸，
        // 按 _maxWindowRatio 等比缩放（不超过原始尺寸），在覆盖层中显示大图预览。
        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_isHoverActive) return;
            if (sender is not Border border) return;
            if (border.Child is not Image sourceImage || sourceImage.Source == null) return;

            // 获取图片原始宽高（优先从缓存读取，避免反复测量）
            double imgW, imgH;
            if (_imageSizes.TryGetValue(sourceImage.Name, out var size))
            {
                imgW = size.Width;
                imgH = size.Height;
            }
            else
            {
                imgW = sourceImage.ActualWidth;
                imgH = sourceImage.ActualHeight;
            }
            if (imgW <= 0 || imgH <= 0) return;

            // 计算等比缩放比例：不超过窗口宽高 * _maxWindowRatio，且不超过原始尺寸
            var root = border.XamlRoot;
            double winW = root.Size.Width;
            double winH = root.Size.Height;
            double maxW = winW * _maxWindowRatio;
            double maxH = winH * _maxWindowRatio;
            double scale = Math.Min(Math.Min(maxW / imgW, maxH / imgH), 1.0);

            // 设置预览图片源及缩放后尺寸
            _previewImage.Source = sourceImage.Source;
            double renderW = imgW * scale;
            double renderH = imgH * scale;
            _previewImage.Width = renderW;
            _previewImage.Height = renderH;
            _previewBorder.Width = renderW;
            _previewBorder.Height = renderH;

            // 设置覆盖层尺寸并定位预览到左上角（+ 边距）
            _overlay.Width = winW;
            _overlay.Height = winH;

            Canvas.SetLeft(_previewBorder, _margin);
            Canvas.SetTop(_previewBorder, _margin);

            _isHoverActive = true;
            _overlay.Visibility = Visibility.Visible;
        }

        // 鼠标离开 Border 时触发：隐藏覆盖层并清除预览图片源。
        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isHoverActive) return;
            _isHoverActive = false;
            _overlay.Visibility = Visibility.Collapsed;
            _previewImage.Source = null;
        }

        // 释放资源：取消所有已注册 Border 的事件订阅，清空缓存和预览图片源。
        public void Dispose()
        {
            foreach (var border in _registeredBorders)
            {
                border.PointerEntered -= OnPointerEntered;
                border.PointerExited -= OnPointerExited;
            }
            _registeredBorders.Clear();
            _imageSizes.Clear();
            _previewImage.Source = null;
        }
    }
}
