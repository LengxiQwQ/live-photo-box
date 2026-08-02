using System;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// HEIC 缩略图提供者工厂。
    /// 根据用户设置（ThumbnailProviderIndex）返回对应的 IThumbnailProvider 实例。
    /// 模式参考 HeicConverterService.DecoderIndex。
    /// </summary>
    public static class ThumbnailProviderFactory
    {
        private static readonly IThumbnailProvider[] _providers =
        [
            new MagickHeicThumbnailProvider(),
            new MagicScalerHeicThumbnailProvider(),
        ];

        /// <summary>用户选择的提供者索引：0=Magick.NET, 1=MagicScaler</summary>
        public static int ProviderIndex =>
            AppSettingsService.GetValue("ThumbnailProviderIndex", 0);

        /// <summary>当前选中的缩略图提供者</summary>
        public static IThumbnailProvider Current =>
            _providers[Math.Clamp(ProviderIndex, 0, _providers.Length - 1)];

        /// <summary>所有可用提供者（用于设置页展示）</summary>
        public static IThumbnailProvider[] All => _providers;
    }
}
