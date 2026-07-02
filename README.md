<div align="center">
<h1>
  <img src="Live Photo Box/Assets/Icons/AppIcon-full.png" alt="icon" width="130" align="left" hspace="16" />
  Live Photo Box（实况照片工具箱）
</h1>
<p><em>A toolbox for managing, repairing, and converting Apple Live Photos on Windows &amp; Android</em></p>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7" alt="Release"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/actions"><img src="https://img.shields.io/github/actions/workflow/status/lengxiqwq/live-photo-box/build.yml?style=flat-square&logo=githubactions" alt="Build"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11" alt="Platform">
  <img src="https://img.shields.io/badge/9.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 9" />
  <img src="https://img.shields.io/badge/C%23-13.0-239120?style=flat-square&logo=csharp" alt="C# 13" />
  <img src="https://img.shields.io/badge/WinUI%203-1.8-0078D7?style=flat-square&logo=windows" alt="WinUI 3" />
</p>

---

<p align="center">
  📖 README Language: &nbsp;<strong>English  &nbsp;·&nbsp; <a href="README.zh-cn.md">简体中文</a></strong>
</p>
<p align="center">
  📋 CHANGELOG: &nbsp;<strong><a href="changelogs/CHANGELOG.md">English</a> &nbsp;·&nbsp; <a href="changelogs/CHANGELOG.zh-CN.md">简体中文</a></strong>
</p>

</div>

## 🚀 Download

<div align="center">
  <a href="https://apps.microsoft.com/detail/9n3d1qnrtvch?referrer=appbadge&mode=full" target="_blank"><img src="./screenshots/Microsoft.svg" alt="Get it from Microsoft" height="52" hspace="35" /></a><a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="./screenshots/GitHub.svg" alt="GitHub Releases" height="52" hspace="35" /></a>
</div>
<p align="center">
  <sub>
    Architecture: <b>x64</b>
    &nbsp;|&nbsp;
    System Requirements: <b>Windows 10 (1809+)</b> or <b>Windows 11</b>
    &nbsp;|&nbsp;
    No runtime required (self-contained .NET 9 + WinAppSDK 1.8)
  </sub>
</p>

---

## 💡 What Is This?

iPhone Live Photos are essentially a still image + a short video clip. The problem is that Apple's format doesn't play well with Windows:

- **No native preview** — Windows File Explorer only shows a static image
- **Metadata loss & pairing corruption** after cross-platform transfers (e.g. iOS → Android → back), turning Live Photos into plain JPEGs
- **Front-camera videos appear rotated or stretched** — Windows ignores the rotation metadata that iOS uses to correct orientation

**Live Photo Box** solves all these problems — letting you view, manage, and repair Live Photos on Windows just like on an iPhone.

Built on **WinUI 3 (Windows App SDK 1.8)**, fully native to Windows 11 Fluent Design guidelines, with Mica / Acrylic materials and dark/light theme support.

---

## 📸 Screenshots

<table align="center">
<tr>
  <td align="center"><b>🏠 Home & Tutorial</b><br><img src="screenshots/home.png" alt="Home" width="100%" /></td>
  <td align="center"><b>📸 Split Live Photo</b><br><img src="screenshots/split.png" alt="Split" width="100%" /></td>
</tr>
<tr>
  <td align="center"><b>🔗 Merge Live Photo</b><br><img src="screenshots/merge.png" alt="Merge" width="100%" /></td>
  <td align="center"><b>🛠️ Repair Live Photo</b><br><img src="screenshots/repair.png" alt="Repair" width="100%" /></td>
</tr>
<tr>
  <td align="center"><b>⚙️ Settings</b><br><img src="screenshots/settings.png" alt="Settings" width="100%" /></td>
  <td align="center"><b>✨ Acrylic Semi-Transparent Effect</b><br><img src="screenshots/acrylic_thin.png" alt="Acrylic effect" width="100%" /></td>
</tr>
</table>

---

## ✨ Core Features

### 🔗 Merge Live Photo

Combine **Apple Live Photos** or any still image + video clip into a **standard Live Photo** (single-file format), viewable on Windows and Android devices.

- Supports **`Google (V1 & V2)` /` OPPO` / `Xiaomi`** protocols
- Writes complete `EXIF` + `QuickTime` metadata (`ContentIdentifier` `UUID`) automatically
- **Apple-native Live Photo pairing**: matches photos and videos by Apple `ContentIdentifier` (`UUID`) even when filenames are completely different; gracefully degrades to capture-time ±2 s tolerance when `UUID` is unavailable
- **Live Photos merged via any supported protocol are fully viewable on Windows** (Motion Photo V2 recommended)

### 📸 Split Live Photo

Split a **Live Photo** (single-file form) into an independent still image (`JPG` / `HEIC`) and video (`MP4` / `MOV`).

- Strips `XMP` metadata to prevent the split image from being erroneously re-identified as a Live Photo, while preserving all other metadata
- Rebuilds `JPEG` segment-by-segment; `EXIF` / `ICC` / `GPS` / shooting parameters are retained

### 🛠️ Repair Live Photo

Deeply repair the visual defects that occur when iPhone Live Photos are exported to Windows.

- **Excess thumbnail & horizontal stretch** (pre-iOS 17.3): Apple once embedded low-resolution thumbnails tagged with orientation, which Windows misinterprets as landscape, causing stretching or squashing. Losslessly fixed via `jpegtran` rotation + stripping the extraneous thumbnail
- **Front-camera video rotation**: the iPhone front camera stores vertical pixels horizontally and relies on an orientation tag — which Windows ignores. Fixed by `FFmpeg` re-encode to bake the rotation matrix into the pixel data
- **HEIC orientation correction**: rectifies miswritten `Orientation` tags
- **ContentIdentifier restoration**: auto-repairs photo-video `UUID` pairings
- Scan and review **diagnostic details** per photo; filter by file type or repair status

### 🖼️ Replace Key Photo (In Development)

Change the cover frame of a Live Photo freely.

- Extract any frame from the video as the new key photo
- Import a custom image
- Lossless replacement — all Live Photo properties remain intact

### 📂 Photo Organize (In Development)

Automatically scan, categorize, and archive photos by device, date, and Live Photo type based on EXIF metadata. Initial support will target iPhone photos.

---

## 📋 Supported Live Photo Protocols

| Protocol | Source | Description |
|----------|--------|-------------|
| Google — Micro Video (V1) | Google (legacy) | `MP4` video appended to `JPEG` end, offset in `GCamera:MicroVideoOffset`. Used by older Xiaomi MIUI / Pixel devices |
| Google — Motion Photo (V2) | Google | Modern standard, `Container:Directory` `XMP` structure. Used by Google Pixel / Xiaomi HyperOS 3+ |
| OPPO/OnePlus — O Live Photo | OPPO / OnePlus | Extended `Motion Photo V2`, adds `OpCamera` namespace + `EXIF` `UserComment`. Used by OPPO ColorOS / OnePlus OxygenOS |

> ⚡ **Live Photos merged via any supported protocol are fully viewable on Windows 11.**

---

## 🛠️ Tech Stack

| Layer | Technology | Version |
|-------|-----------|---------|
| Language | C# | 13.0 |
| Runtime | .NET | 9.0 |
| UI Framework | Windows App SDK (WinUI 3) | 1.8 |
| Architecture | MVVM (CommunityToolkit.Mvvm) | 8.4.2 |
| Image Processing | Magick.NET (ImageMagick) + Win2D | 14.14.0 / 1.3.2 |
| Metadata Engine | `ExifTool` (daemon mode, v13.x) | — |
| Video Processing | `FFmpeg` (NVENC / QSV / AMF hardware acceleration) | — |
| JPEG Operations | `jpegtran` (lossless rotation, thumbnail stripping) | — |
| UI Extensions | CommunityToolkit.WinUI + FluentIcons | — |
| Packaging | MSIX self-contained (no runtime required) | — |

---

## 💻 Build & Development

### Prerequisites

- [Visual Studio 2022](https://visualstudio.microsoft.com/) or later
- In VS Installer, select: **.NET desktop development** + **Universal Windows Platform development** (includes Windows App SDK components)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Build

```bash
# Clone the repository
git clone https://github.com/lengxiqwq/live-photo-box.git
cd live-photo-box

# Restore NuGet packages
dotnet restore

# Build the project
dotnet build "Live Photo Box/Live Photo Box.csproj"

# Run
dotnet run --project "Live Photo Box/Live Photo Box.csproj"
```

---

## 📁 Project Structure

```
live-photo-box/
├── Live Photo Box/            # Main project (WinUI 3 MSIX app)
│   ├── Assets/                # Icons, screenshots, static resources
│   ├── Controls/              # Custom controls (fullscreen lightbox, status bar)
│   ├── Converters/            # XAML value converters
│   ├── Helpers/               # Utilities (scrolling, formatting, hover preview, etc.)
│   ├── Models/                # Data models
│   ├── Services/              # Business logic layer
│   │   └── Protocols/         # Live Photo protocol implementations (3 types)
│   ├── Strings/               # Multilingual resources (zh-Hans / en-US)
│   ├── ViewModels/            # MVVM ViewModel layer
│   └── Views/                 # XAML pages
├── docs/                      # Project documentation
├── changelogs/                # Release notes
├── scripts/                   # Build & packaging scripts
└── README.md
```

> 📖 See [`docs/LivePhotoBox-ProjectOverview.md`](docs/LivePhotoBox-ProjectOverview.md) for the complete directory reference.

---

## 🌍 Localization

| Language | Status |
|----------|:------:|
| 中文（简体）(zh-Hans) | ✅ Complete |
| English (en) | ✅ Complete |

Follows system language automatically; can also be switched manually in Settings.

---

## 🤝 Contributing

Issues and Pull Requests are welcome!

- 🐛 **Bug reports** and 💡 **Feature requests** → [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- 🔧 **Code contributions** → Fork → Feature Branch → Pull Request

### Guidelines

- UI text should use RESW resource files, not hardcoded strings
- Follow the project's MVVM layering conventions
- Add multi-line comments at the top of each file describing its purpose

---

## 📄 License

This project is open-source under the **GNU General Public License v3.0 (GPL 3.0)**. See the [LICENSE](LICENSE) file for details.

---

## 🙏 Credits

| Tool / Library | Purpose | License |
|---------------|---------|---------|
| [ExifTool](https://exiftool.org/) by Phil Harvey | Global metadata parsing, injection, XMP reconstruction | Perl |
| [FFmpeg](https://ffmpeg.org/) | Video transcoding and stream remuxing | LGPL/GPL |
| [jpegtran](https://jpegclub.org/) | Lossless JPEG transforms (DCT coefficient space) | Free software |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) by dlemstra | HEIC/HEIF decoding via libheif | Apache 2.0 |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM framework | MIT |
| [Win2D](https://github.com/microsoft/Win2D) | GPU-accelerated graphics | MIT |
| [FluentIcons](https://github.com/davidxuang/FluentIcons) | Fluent icon set | MIT |

---

<p align="center">
  <sub>Made with ❤️ by <a href="https://github.com/lengxiqwq">LengxiQwQ</a></sub>
</p>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/stargazers"><img src="https://img.shields.io/github/stars/lengxiqwq/live-photo-box?style=social" alt="Stars"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/network/members"><img src="https://img.shields.io/github/forks/lengxiqwq/live-photo-box?style=social" alt="Forks"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/downloads/lengxiqwq/live-photo-box/total?style=social" alt="Downloads"></a>
</p>
