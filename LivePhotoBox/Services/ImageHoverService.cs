using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Services
{
    public sealed class ImageHoverService : IDisposable
    {
        private readonly Canvas _overlay;
        private readonly Border _previewBorder;
        private readonly Image _previewImage;
        private readonly double _maxWindowRatio;
        private readonly double _margin;
        private readonly Dictionary<string, (double Width, double Height)> _imageSizes = new();

        private bool _isHoverActive;

        public ImageHoverService(Canvas overlay, Border previewBorder, Image previewImage,
            double maxWindowRatio = 0.5, double margin = 20.0)
        {
            _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
            _previewBorder = previewBorder ?? throw new ArgumentNullException(nameof(previewBorder));
            _previewImage = previewImage ?? throw new ArgumentNullException(nameof(previewImage));
            _maxWindowRatio = maxWindowRatio;
            _margin = margin;
        }

        public void Register(Border border)
        {
            if (border == null) return;
            border.PointerEntered += OnPointerEntered;
            border.PointerExited += OnPointerExited;
        }

        public void Unregister(Border border)
        {
            if (border == null) return;
            border.PointerEntered -= OnPointerEntered;
            border.PointerExited -= OnPointerExited;
        }

        private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_isHoverActive) return;
            if (sender is not Border border) return;
            if (border.Child is not Image sourceImage || sourceImage.Source == null) return;

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

            var root = border.XamlRoot;
            double winW = root.Size.Width;
            double winH = root.Size.Height;
            double maxW = winW * _maxWindowRatio;
            double maxH = winH * _maxWindowRatio;
            double scale = Math.Min(Math.Min(maxW / imgW, maxH / imgH), 1.0);

            _previewImage.Source = sourceImage.Source;
            double renderW = imgW * scale;
            double renderH = imgH * scale;
            _previewImage.Width = renderW;
            _previewImage.Height = renderH;
            _previewBorder.Width = renderW;
            _previewBorder.Height = renderH;

            _overlay.Width = winW;
            _overlay.Height = winH;

            Canvas.SetLeft(_previewBorder, _margin);
            Canvas.SetTop(_previewBorder, _margin);

            _isHoverActive = true;
            _overlay.Visibility = Visibility.Visible;
        }

        private void OnPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_isHoverActive) return;
            _isHoverActive = false;
            _overlay.Visibility = Visibility.Collapsed;
            _previewImage.Source = null;
        }

        public void Dispose()
        {
        }
    }
}
