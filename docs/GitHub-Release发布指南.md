# GitHub Release 发布指南

## 你需要产出什么

发 GitHub Release 时，提供 **两种包** 给用户选择：

| 包类型 | 后缀 | 用户怎么用 | 适合人群 |
|---|---|---|---|
| **便携版** | `.zip` | 解压 → 双击 `LivePhotoBox.exe` | 想试用、不想安装的人 |
| **MSIX 旁加载安装包** | `.msix` | 双击安装 → 开始菜单出现 | 想正式安装、有卸载入口的人 |

两种包都从**同一次构建**产出，内容是同一份，区别只在外层包装。

---

## 方式一：Visual Studio 手动打包（推荐，最简单）

### 便携版 zip

1. 顶部下拉选 **Release** + **x64**
2. `D:\Projects\live-photo-box\LivePhotoBox\bin\Release\net9.0-windows10.0.19041.0\win-x64\`
3. 把这个文件夹里的**所有文件**打包成 zip：
   - 右键文件夹 → 发送到 → 压缩文件夹
   - 或 PowerShell：`Compress-Archive -Path 'bin\Release\...\win-x64\*' -DestinationPath 'LivePhotoBox_v1.14.10_portable_x64.zip'`
4. 对 x86 / ARM64 重复

> 注意：便携版 zip 里 **不要** 包含 `.msix`、`.appinstaller`、`Dependencies` 文件夹等打包产物，只保留 exe/dll/资源文件。

### MSIX 安装包

1. **解决方案资源管理器** → 右键 `LivePhotoBox` 项目 → **发布**
2. 选择 **创建应用包** → **旁加载（Sideload）**
3. 证书选 **"是，使用当前证书"**（就是 `LivePhotoBox_TemporaryKey.pfx`）
4. 选择 **Release** 配置，勾选 x64 / x86 / ARM64
5. 输出目录选 `D:\Projects\live-photo-box\publish\msix\`
6. 点击 **创建**

生成的文件在 `publish/msix/` 下，每个架构一个 `.msix` 文件。

---

## 方式二：命令行构建（可脚本化）

```bash
# 1. 便携版 x64
dotnet publish LivePhotoBox/LivePhotoBox.csproj \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:Platform=x64 \
    -p:WindowsAppSDKSelfContained=true \
    -o publish/portable/x64

# 打包成 zip
Compress-Archive -Path publish/portable/x64/* `
    -DestinationPath publish/LivePhotoBox_v1.14.10_portable_x64.zip

# 2. MSIX 旁加载包 x64（需要证书文件 LivePhotoBox_TemporaryKey.pfx）
dotnet publish LivePhotoBox/LivePhotoBox.csproj \
    -c Release \
    -r win-x64 \
    -p:Platform=x64 \
    -p:WindowsAppSDKSelfContained=true \
    -p:AppxPackageDir=$(pwd)/publish/msix/ \
    -p:AppxBundle=Never \
    -p:AppxPackageSigningEnabled=true

# 3. 对 x86 / ARM64 重复上面步骤
```

或直接用项目里的一键脚本：
```bash
bash scripts/build-release.sh
```

---

## 上传到 GitHub Release

1. 打开 https://github.com/LengxiQwQ/live-photo-box/releases
2. 点击 **Draft a new release**
3. Tag 版本：`v1.14.10`（和 `Package.appxmanifest` 里的 Version 一致）
4. Title：`LivePhotoBox v1.14.10`
5. 把以下文件拖进去：
   ```
   LivePhotoBox_v1.14.10_portable_x64.zip
   LivePhotoBox_v1.14.10_portable_x86.zip
   LivePhotoBox_v1.14.10_portable_ARM64.zip
   LivePhotoBox_v1.14.10_x64.msix
   LivePhotoBox_v1.14.10_x86.msix
   LivePhotoBox_v1.14.10_ARM64.msix
   ```
6. 在描述里写上安装说明（见下方 Release Notes 模板）
7. 点击 **Publish release**

---

## Release Notes 模板

```markdown
## 📥 下载说明

### 便携版（推荐试用）
下载对应架构的 `.zip` 文件，解压到任意文件夹，双击 `LivePhotoBox.exe` 即可运行。
不需要安装、不写注册表、不创建开始菜单。

### 安装版（MSIX 旁加载）
下载对应架构的 `.msix` 文件，双击安装。
安装前需要信任开发者证书：

**Windows 11:**
1. 双击 `.msix` → 点击"安装"
2. 如果提示证书不受信任 → 点击"显示详细信息" → "仍要安装"

**Windows 10:**
1. 打开 **设置 → 更新和安全 → 面向开发人员**
2. 勾选 **旁加载应用** 或 **开发人员模式**
3. 再双击 `.msix` 安装

### 架构选择
- **x64**: 绝大多数 Windows 11/10 64 位电脑
- **ARM64**: Surface Pro X / 骁龙笔记本
- **x86**: 老旧 32 位系统

---

## 🔧 此版本更新

- 修复了非打包便携版启动崩溃的问题
- 现在同时提供便携版（zip）和安装版（MSIX）
```

---

## 一键装包验证

发布前，下载 zip 到另一台电脑/虚拟机，解压运行，确认不崩。

MSIX 也装一下，确认开始菜单能打开、设置能读写。
