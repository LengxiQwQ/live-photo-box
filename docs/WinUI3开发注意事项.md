# WinUI 3 注意事项

> WinUI 3 / Windows App SDK 开发中经常踩的坑。

---

## 1. 多语言资源文件（RESW）命名规则 — 常踩的坑

### 1.1 XAML `x:Uid` 用法

```xml
<!-- TextBlock → 框架自动追加 .Text，查找 KeyPhotoPage_Foo.Text -->
<TextBlock x:Uid="KeyPhotoPage_Foo" />
```

- `x:Uid` 写**不带** `.Text` 的 key 名
- 框架在资源文件中查找：`KeyPhotoPage_Foo.Text`

### 1.2 代码 `ResourceService.GetString()` 用法

```csharp
// 直接传完整 key 名
string text = ResourceService.GetString("KeyPhotoPage_Foo");
```

- 传什么 key 就查什么 key，与 XAML 的自动追加规则无关
- key 名中**不要**带 `.` 后缀（如 `.Text`），避免冲突

### 1.3 禁止冲突（最容易搞错的地方）

`.` 在 PRI 资源编译器中是**层级分隔符**：

```
KeyPhotoPage_ExportProgressPrefix      → 被视为容器（scope）
KeyPhotoPage_ExportProgressPrefix.Text → 被视为其子资源
```

**两者不能同时存在**，否则编译报错：

> `PRI278: 'Resources/KeyPhotoPage_ExportProgressPrefix/Text' defined as both resource and scope`

### 1.4 总结表格

| 使用场景 | RESW key 名 | 例子 |
|----------|-------------|------|
| XAML `x:Uid` 引用 | 带 `.Text`（框架自动找） | RESW 里写 `Foo.Text`，x:Uid 写 `Foo` |
| 代码 `GetString()` 引用 | 不带 `.Text` | RESW 里写 `Foo`，代码查 `Foo` |
| 两者混用同一个 key | ❌ 不行 | 导致 PRI 层级冲突 |

> ⚠️ 如果某个 key 是 `Foo.Text` 且只用 `GetString()` 访问（没有 x:Uid），说明它不该带 `.Text`。应改名去掉 `.Text`，比如 `FooLabel`。

---

## 2. XAML 多语言硬性规则

1. **所有可见文本必须走 x:Uid**
   严禁在 `Text` / `Content` 中硬编码文字。

2. **描述文本末尾不要句号**
   `SettingsCard.Description` 末尾不加 `.` / `。`。

3. **调试工具区颜色**
   调试区的 `ToggleSwitch`、`Slider` 用红色 `#D32F2F`，不跟随主题色。

---

## 3. XAML 编译器相关

- `XamlCompilerOtl=true` 启用新版编译器→**可能导致运行时闪退**（与复杂 x:Bind 不兼容）
- 大 XAML 文件（>50 KB）→ net472 版 XAML 编译器可能崩（0xC0000005=访问越界），需拆文件

---

## 4. 其他 WinUI 3 注意

- `ContentDialog` 需要 `XamlRoot`，ViewModel 中通过 `App.MainWindow?.Content?.XamlRoot` 获取
- 更多见 `docs/顽疾修复记录.md`
