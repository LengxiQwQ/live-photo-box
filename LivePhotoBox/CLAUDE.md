# LivePhotoBox — 实况照片工具箱

WinUI 3 (WinAppSDK 1.8 + .NET 9) 桌面应用，用于处理 iPhone 实况照片。

> 📖 **完整项目文档**: `docs/LivePhotoBox-ProjectOverview.md` — 包含完整目录树、每个文件说明、技术栈详情、架构图等。

## 项目结构

```
LivePhotoBox/
├── Assets/        # 图片、示例文件等静态资源
├── Controls/      # 自定义控件
├── Strings/       # 中英文资源 (Resources.resw)
├── Tools/         # exiftool, jpegtran, ffmpeg (可选)
├── ViewModels/    # MVVM ViewModel 层
└── Views/         # XAML 页面
    ├── HomePage.xaml(.cs)   # 主页/教程页
    ├── MergePage.xaml(.cs)  # 合成页
    ├── SplitPage.xaml(.cs)  # 拆分页
    ├── RepairPage.xaml(.cs) # 修复页
    └── ...
```

## 技术栈
- **框架**: .NET 9 + WinUI 3 (WinAppSDK 1.8)
- **架构**: MVVM (CommunityToolkit.Mvvm)
- **国际化**: zh-Hans / en-US
- **包管理**: MSIX 打包
