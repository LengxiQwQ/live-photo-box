# WinUI 3 自包含应用 — 打包/非打包双模式启动修复指南

## 背景

LivePhotoBox 需要同时支持两种部署模式：

| 模式 | 用途 | 特征 |
|---|---|---|
| **MSIX 打包** | Microsoft Store 发布 | 有包标识（Package Identity），App Container 沙箱 |
| **Unpackaged（非打包）** | GitHub Release + 开发调试 | 无包标识，普通 exe 直接运行 |

项目使用 **WinAppSDK 1.8 + .NET 9 + SelfContained 部署**。

---

## 问题现象

非打包模式启动时立即崩溃，退出码 `0xc000027b`，异常为 `System.InvalidOperationException`（位于 `WinRT.Runtime.dll`）。

---

## 根因分析

### 这不是项目的 Bug，是 WinUI 3 的架构级设计

Windows App SDK 默认假设应用运行在 **App Container** 中且拥有 **包标识**。
非打包支持是后来补的，框架给工具但不会自动生效。

问题分两个层面，**缺一不可**：

### 第一层：WinRT 运行时找不到 DLL（框架级）

```
打包版：  系统 → PackageGraph → 框架包声明 → 找到 DLL ✓
非打包：  系统 → ??? → 找不到 → 0xc000027b ✗
```

自包含部署把所有 WinAppSDK DLL 打包在 exe 同目录，但 WinRT 类型系统默认不找那里。

**必须手动告诉 WinRT "DLL 在我自己目录里"。**

### 第二层：应用代码调用了需要包标识的 WinRT API（应用级）

WinRT 跑起来后，代码里有些 API 底层依赖 App Container 的隔离存储和身份信息：

| API | 作用 | 为何 unpackaged 炸 |
|---|---|---|
| `Windows.Storage.ApplicationData.Current` | 应用数据存储 | 依赖包标识做数据隔离 |
| `Windows.ApplicationModel.Package.Current` | 获取包信息/版本号 | 没有包当然没有 Package |
| 部分 WinRT 类的静态属性激活 | 语言/系统设置等 | 无包环境下类型激活链路可能失败 |

**任何用了这些 API 的双模式应用都必须自己做兜底。**

---

## 修复方案

### 修复 1：让 WinRT 能找到自包含 DLL

#### `LivePhotoBox.csproj`（第 27 行）

```xml
<WindowsAppSdkUndockedRegFreeWinRTInitialize>true</WindowsAppSdkUndockedRegFreeWinRTInitialize>
```

作用：MSBuild 生成免注册激活所需的引导文件（`Microsoft.WindowsAppRuntime.dll` 等）。

#### `Services/WindowsAppSdkBootstrap.cs`

```csharp
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Services
{
    internal static class WindowsAppSdkBootstrap
    {
        [DllImport("Microsoft.WindowsAppRuntime.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int WindowsAppRuntime_EnsureIsLoaded();

        [ModuleInitializer]  // 在所有代码之前执行，包括 Program.Main()
        internal static void Initialize()
        {
            // 警告：此方法中绝对不能引用任何 WinRT 投影类型！
            // WinRT 尚未初始化，引用投影类型会导致其 DLL 模块构造器
            // 因类型激活失败而损坏 WinRT 状态。

            Environment.SetEnvironmentVariable(
                "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
                AppContext.BaseDirectory);          // 告诉 WinRT 去哪找 DLL

            WindowsAppRuntime_EnsureIsLoaded();    // 强制加载，触发 SxS 重定向
        }
    }
}
```

**关键点**：
- `[ModuleInitializer]` 保证在所有代码之前运行
- 只做两件事：设环境变量 + 加载 DLL
- **绝对不能**有 `using Microsoft.UI.Xaml` 等 WinRT 引用
- **绝对不能**调用 `TryInitialize`（那是给非自包含应用找系统框架包用的，会跟本地 DLL 冲突）

### 修复 2：给需要包标识的 API 做双模式兜底

#### 模式 A：API 有包/无包行为完全不同 → 双路径

**`Services/AppSettingsService.cs`** — 完整重写：

```csharp
// 打包模式：ApplicationData.Current.LocalSettings（系统 API）
// 非打包模式：JSON 文件 fallback（AppContext.BaseDirectory\appsettings.json）

private static ApplicationDataContainer? LocalSettings
{
    get
    {
        try
        {
            return ApplicationData.Current.LocalSettings;
        }
        catch (InvalidOperationException)
        {
            // 非打包：无包标识，静默回退到 JSON
            return null;
        }
    }
}
```

#### 模式 B：API 功能非关键 → try-catch 静默降级

**`Services/LanguageService.cs`** — WinRT 调用加保护：

```csharp
public static void ApplyLanguageOverride(string languageTag)
{
    try
    {
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = languageTag;
    }
    catch (InvalidOperationException)
    {
        // 非打包模式：语言 API 不可用，使用默认语言
        // 不影响应用正常功能
    }
}
```

#### 模式 C：已知有风险但已有 try-catch → 确认安全

**`Services/LogService.cs`** `ResolveLogDirectory()` — 已有 try-catch 兜底：

```csharp
try { return ApplicationData.Current.LocalFolder.Path; }
catch { return Environment.GetFolderPath(LocalApplicationData) + "\\LivePhotoBox\\Logs"; }
```

**`App.xaml.cs`** `AppVersion` 属性 — 已有 try-catch 兜底：

```csharp
try { var v = Package.Current.Id.Version; ... }
catch { return Assembly.GetEntryAssembly().GetName().Version; }
```

---

## 常见坑 & 避雷指南

### 1. [ModuleInitializer] 里不要引用 WinRT 类型

```csharp
// ❌ 错 —— 触发 WinRT 投影 DLL 过早加载
var hasPackage = AppInstance.Restart("");  
var pkg = Package.Current;

// ✅ 对 —— 只用系统 API
Environment.SetEnvironmentVariable(...);
DllImport + P/Invoke
```

### 2. 自包含应用不要调 TryInitialize

`MddBootstrapInitialize2`（`TryInitialize` 的底层）是去系统注册表里找已安装的框架包。
自包含应用的 DLL 在自己目录里，不在系统里。调它要么返回 `0x80670016`（找不到），
要么找到系统版本后和本地版本冲突导致 `0xc0000602`（fail fast）。

### 3. ApplicationData 需要包标识

```csharp
// ❌ unpackaged 下直接炸
var settings = ApplicationData.Current.LocalSettings;

// ✅ 双模式
try { settings = ApplicationData.Current.LocalSettings; }
catch (InvalidOperationException) { /* JSON fallback */ }
```

### 4. 写新代码时检查 WinRT API

调用任何 `Windows.*` 命名空间下的 API 时，心里过一遍：
- 这需要包标识吗？（存疑就搜一下官方文档）
- 不确定的话，加上 try-catch 兜底

---

## 验证清单

每次改完东西，两个模式都跑一遍：

| 检查项 | 打包版 | 非打包 |
|---|---|---|
| 启动不崩溃 | ☐ | ☐ |
| 设置读写正常 | ☐ | ☐ |
| 语言切换正常 | ☐ | ☐ |
| 日志写入正常 | ☐ | ☐ |
| 硬件检测正常 | ☐ | ☐ |
| 主窗口正常显示 | ☐ | ☐ |

---

## 相关文件

| 文件 | 作用 |
|---|---|
| `LivePhotoBox.csproj` L27 | `WindowsAppSdkUndockedRegFreeWinRTInitialize` 属性 |
| `Services/WindowsAppSdkBootstrap.cs` | `[ModuleInitializer]` WinRT 运行时引导 |
| `Services/AppSettingsService.cs` | 双模式设置存储 |
| `Services/LanguageService.cs` | WinRT 语言 API try-catch 保护 |
| `Services/LogService.cs` L676-691 | 日志目录双模式解析 |
| `App.xaml.cs` L41-56 | AppVersion 双模式获取 |

---

## 参考链接

- [Windows App SDK — Undocked RegFree WinRT](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps)
- [WinUI 3 — Unpackaged deployment](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/create-your-first-winui3-app#unpackaged-deployment)
- [MSBuild properties for Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix#msbuild-properties)
