# KeyPhoto 页面 UI 备份 — 2026-07-06

## 背景

最初计划为 KeyPhotoPage（实况照片主图更换）实现完整 UI，按 WinUI 3 Fluent Design 规范编写了全套代码。
后来发现这些规范实际是用于"实况照片合成"页面的重构，而非 KeyPhoto 页面，因此撤销更改并备份于此。

## 备份内容

| 文件 | 说明 | 行数 |
|------|------|------|
| `Models/KeyPhotoTask.cs` | 队列项数据模型（ObservableObject） | ~80 |
| `ViewModels/KeyPhotoViewModel.cs` | 完整 ViewModel，含绑定属性、命令桩、DEBUG 示例数据 | ~260 |
| `Views/KeyPhotoPage.xaml` | 三行布局：CommandBar + 左侧4卡片 + 右侧任务队列 + 底部状态栏 | ~520 |
| `Views/KeyPhotoPage.xaml.cs` | Code-behind，含 FolderPicker 浏览逻辑 | ~85 |
| `Strings/zh-Hans/Resources.resw` | 中文资源（33条） | — |
| `Strings/en-US/Resources.resw` | 英文资源（33条） | — |

## 设计亮点

- **全 WinUI 原生控件**：CommandBar, ListView, AutoSuggestBox, ToggleSwitch 等
- **全部 ThemeResource**：支持亮/暗模式，不写死颜色
- **x:Uid 本地化**：所有文字通过 RESW
- **Card 风格**：统一 CornerRadius=12, Padding=16 的 Border 卡片
- **响应式布局**：左侧 320px 固定，右侧 * 自适应

## 页面布局

```
┌──────────────────────────────────────────────────────────┐
│ CommandBar: 添加文件 | 添加文件夹 | ▶ 开始 | ⏸ 暂停 | ⏹ 停止 | 🗑 清空  │
├───────────────┬──────────────────────────────────────────┤
│ 左侧 320px    │ 右侧 * (自适应任务队列)                      │
│ 4张Card       │ ListView + 空状态占位 + 搜索/排序/筛选      │
├───────────────┴──────────────────────────────────────────┤
│ 底部状态栏: Ready | ProgressBar | ✓成功 ✗失败 ⏱剩余时间    │
└──────────────────────────────────────────────────────────┘
```

## 待解决问题

- ToggleSwitch 的 x:Uid 需要用 `.Header` 而非 `.Text`（已修正在备份的 RESW 中）
- MVVM Toolkit 的 `[NotifyPropertyChangedFor]` 不能指向自身属性（已修正）
