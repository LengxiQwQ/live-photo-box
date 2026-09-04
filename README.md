<div align="center">
<h1>
  <img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/LivePhotoBox/Assets/Icons/AppIcon-full.png" width="130" align="left" hspace="16" />
  Live Photo Box（实况照片工具箱）
</h1>
<p><em>Unifying all live photo protocols for seamless cross-device viewing & migration</em></p>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=latest%20release" alt="Latest release"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/actions"><img src="https://img.shields.io/github/actions/workflow/status/lengxiqwq/live-photo-box/build.yml?style=flat-square&logo=githubactions" alt="Build"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11" alt="Platform">
  <img src="https://img.shields.io/badge/9.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 9" />
  <img src="https://img.shields.io/badge/C%23-13.0-239120?style=flat-square&logo=csharp" alt="C# 13" />
  <img src="https://img.shields.io/badge/WinUI%203-1.8-0078D7?style=flat-square&logo=windows" alt="WinUI 3" />
</p>

---

<p align="center">
  📖 README Language: &nbsp;<strong>English  &nbsp;·&nbsp; <a href="README.zh-CN.md">简体中文</a></strong>
</p>
</div>

## 🚀 Download

<div align="center">
  <a href="https://apps.microsoft.com/detail/9n3d1qnrtvch?referrer=appbadge&mode=full" target="_blank" rel="noopener noreferrer"><img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get it from Microsoft" height="52" width="190" hspace="35" /></a><a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/screenshots/GitHub.svg" alt="GitHub Releases" height="52" width="190" hspace="35" /></a>
</div>
<p align="center">
</p>
<p align="center">
  Or install the <b>CLI</b> via <b>winget</b>: <code>winget install LengxiQwQ.LivePhotoBox</code>
</p>

---

> 🎨 **Live Photo Box could use a new icon**
>
> Now that Live Photo Box's main features are gradually stabilizing, the author would like to give the app a more fitting icon and refresh the homepage banner as well.
>
> If you have a design direction you like or would like to try creating something, feel free to [share it in GitHub Discussions →](https://github.com/LengxiQwQ/live-photo-box/discussions/new?category=ideas). PNG, PSD, SVG, Figma files, and links to other design websites are all welcome. Simple sketches and ideas are welcome too. Thank you!

---

## 💡 Cross-Device Live Photos

Although Live Photos from different brands all combine a still image with a short video, they use different file structures and data formats. When moving across devices or platforms, this can lead to playback issues, incorrect orientation, lost motion, or missing metadata.

**Live Photo Box** provides a unified way to merge, split, convert, repair, and edit Live Photos, making it easy to move them between different brands and devices.

It also preserves as much of the original detail and information as possible, including HDR, EXIF data, camera settings, capture information, and original media content — keeping every conversion as close as possible to the original photo.

---

## 📸 Screenshots

<p align="center"><b>🖼️ Live Photo Edit</b><br><img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/screenshots/Edit.png" alt="Edit" width="80%" /></p>

<p align="center"><b>🔗 Merge Live Photo</b><br><img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/screenshots/merge.png" alt="Merge" width="80%" /></p>

---

> ℹ️ **Historical Releases vs. Current Architecture**
>
> Earlier Live Photo Box releases used bundled external tools such as FFmpeg, ExifTool, jpegtran and libheif command-line utilities for parts of the media and metadata pipeline. The current development branch has moved to a Rebuilt / Native-only architecture, and those external executables are no longer part of the current product runtime. As a result, older published releases may differ from the current master branch in dependencies, architecture and temporary feature availability.

---

## ✨ Core Features & Development Status

Live Photo Box strictly separates **Source Inspection / Cleaning**, **Neutral Extraction**, and **Target Writing**:

```text
GUI / CLI → Core Orchestration → LivePhotoBox.Native (C++20)
(Rebuilt-only · No Legacy Runtime · No Bundled External Executables)
```

### 🔗 Merge Live Photo (Combo)

Convert a **dual-file Live Photo** (or any image + video pair) into a **single-file Live Photo**, viewable on Windows and Android devices.

- **Merge anything in one click**: drag & drop or pick an image (`JPG` / `HEIC`) + a video (`MP4` / `MOV`), or scan a whole folder and auto-pair everything into a batch queue
- **Smart pairing**: auto-match image + video by filename, Apple `ContentIdentifier` UUID (inspected directly by Native without external tools), or vivo camera ID
- **Target protocols**: JPEG outputs supported across Google Micro Video (v1), Google Motion Photo (v2), OPPO O-Live, vivo, Samsung Motion Photo, and HUAWEI Moving Photo. Samsung and HUAWEI HEIC formats are supported via Native tail writers.
- **Batch file naming**: quickly compose filenames from template segments with drag-to-reorder, presets, separators, and live preview
- **Parallel batch merging**: queue with search, multi-dimension sorting, and status filters

| Target Merge Protocol | Format Support | Status in Master |
|---|---|---|
| Google - Micro Video (V1) | JPEG + MP4/MOV | ✅ Supported |
| Google - Motion Photo (V2) | JPEG + MP4/MOV | ✅ Supported |
| OPPO - O-Live Photo | JPEG + MP4/MOV | ✅ Supported |
| HUAWEI - Moving Photo | JPEG + MP4/MOV, HEIC + MP4 | ✅ Supported |
| Samsung - Motion Photo | JPEG + MP4/MOV, HEIC + MP4 | ✅ Supported (SEF trailer) |
| vivo - Live Photo | JPEG + MP4/MOV | ✅ Supported |
| Google / OPPO / vivo (HEIC target) | HEIC + MP4/MOV | 🟡 Safely rejected (Native HEIC XMP writer pending) |
| Apple - Live Photo (dual-file target) | HEIC/JPEG + MOV | ⏳ Deferred (Target writer roadmap P9) |

### 📸 Split Live Photo

Split a **Live Photo** into independent protocol-free neutral still image (`JPG` / `HEIC`) and video (`MP4` / `MOV`).

- **Batch split**: scan a folder, auto-detect and queue all Live Photos across Google, OPPO, vivo, Samsung, and HUAWEI
- **Neutral extraction & cleaning**: strips live photo metadata (XMP namespaces, SEF motion photo tags, Huawei LIVE marker) so the extracted image is no longer re-identified as a live photo, while preserving EXIF, ICC, GPS, and HDR gain maps
- **Naming templates**: segment-based naming with live preview

| Capability | Scope | Status in Master |
|---|---|---|
| Source Inspection | Google, OPPO, vivo, Samsung, HUAWEI, Apple | ✅ Supported (Native) |
| Neutral Split / Clean | Extract clean neutral image + video | ✅ Supported (Native) |
| Target Protocol Packaging | Re-package into Apple/vivo dual-file targets | ⏳ Deferred (Target writer roadmap P9) |

### 🖼️ Live Photo Edit (Cover / Key Photo)

Change Live Photo cover frame and export video frames.

> ⏳ **Status in Master**: The UI and CLI command exist, but the Rebuilt Native pipeline is not ready (`RebuiltPipelineNotReady`). Temporarily frozen pending Native reconstruction.

### 🛠️ Repair Live Photo

Repair display anomalies from exported Apple Live Photos (excess thumbnails, front camera orientation).

> ⏳ **Status in Master**: The UI and CLI command exist, but the Rebuilt Native pipeline is not ready (`RebuiltPipelineNotReady`). Temporarily frozen pending Native reconstruction (Roadmap P8).

### 📂 Photo Organize (In Development)

Automatically scan, categorize, and archive photos by device, date, and Live Photo type based on metadata. Starting with iPhone photos, expanding to more brands.

---


## 💻 Command Line (CLI)

Live Photo Box ships a **command-line interface** — `livephotobox` — that shares 100% of its core logic with the GUI, ideal for scripting and AI agents.

- **Commands**: `convert` (standalone media conversion), `protocols` (protocol & format compatibility), `merge` (single-pair or batch), `split` (extract neutral photo + video), `cover` / `keyphoto` (cover frame; pending Native rebuild), `repair` (metadata repair; pending Native rebuild), `update` / `update-check` (check & install updates)
- **Four executable aliases**: `livephotobox` / `livephoto` / `livebox` / `lpb`
- **Batch pairing & naming**: auto-pair by filename, Apple `ContentIdentifier` UUID, or vivo camera ID; rename outputs with templates like `-n custom:{name}_{date}`; `--after` moves sources to a folder / recycle bin on completion
- **Script-friendly**: `--json` outputs structured results that scripts and AI agents can consume directly; `--dry-run` previews operations without touching files
- **Sane output defaults**: single-pair merges write next to the source photo (protocol-suffixed name); batch merges write into a `{folder}_<protocol>` subfolder — never the terminal’s current directory. `-w` overwrites existing outputs instead of auto-renaming
- **Distribution**: bundled with the installer / portable build (optional “Add to PATH”), or a standalone single-file `-x64-cli.zip` (no install needed; ships `add-to-path.cmd` / `remove-from-path.cmd` helpers to add/remove PATH in one click); install the CLI-only edition via `winget install LengxiQwQ.LivePhotoBox`

📖 **CLI User Guide**: [English](docs/CLI-User-Guide.md) · [简体中文](docs/CLI-User-Guide.zh-CN.md)

---

## 🛠️ Tech Stack

| Layer | Technology | Version |
|-------|-----------|---------|
| Language | C# | 13.0 |
| Native backend | Visual C++ / C++20 DLL (stable C ABI) | MSVC x64 (v143/v145) |
| Runtime | .NET | 9.0 |
| UI Framework | Windows App SDK (WinUI 3) | 1.8 |
| Architecture | MVVM (CommunityToolkit.Mvvm) | 8.4.2 |
| Media & Protocol Engine | `LivePhotoBox.Native` (in-process C++20 ISO-BMFF / JPEG / SEF / Apple MakerNote inspection, cleaning, extraction, and composition) | — |
| Image Scaling | PhotoSauce.MagicScaler | 0.15.0 |
| Image Processing (GUI) | Windows Imaging Component (WIC) + Win2D | — / 1.3.2 |
| Markdown Rendering | Markdig | 1.3.2 |
| UI Extensions | CommunityToolkit.WinUI + FluentIcons | — |
| Command Line | System.CommandLine | 2.0.11 |
| Packaging | MSIX self-contained (GUI) / Single-file zip (CLI) | — |

> The current product runtime operates exclusively on the **Rebuilt / Native** engine (`LivePhotoBox.Native`) via a stable C ABI. Bundled external tool executables and Legacy runtimes have been completely removed from the product runtime.

---

## 💻 Build & Development

### Prerequisites

- [Visual Studio 2022](https://visualstudio.microsoft.com/) or later
- In VS Installer, select: **.NET desktop development** + **Universal Windows Platform development** + **Desktop development with C++** (MSVC x64 toolchain)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)

### Build

```bash
# Clone the repository
git clone https://github.com/lengxiqwq/live-photo-box.git
cd live-photo-box
```

The repo ships ready-made PowerShell build scripts (`scripts/`) for both GUI and CLI — no need to run `dotnet` commands by hand:

| Script | Produces |
|--------|----------|
| `scripts/build-dev.ps1` | Unpackaged dev build (GUI + CLI) into `publish/` |
| `scripts/build-cli-release.ps1` | Standalone single-file CLI zip (`publish/Live-Photo-Box-v{version}-x64-cli.zip`) |
| `scripts/build-release.ps1` | Full release trio: portable zip + CLI zip + installer |

> Scripts accept `-CI` for GitHub Actions and other CI environments (no `pause` prompt).

For local verification, run the Native smoke test and the solution/CLI test suites from the repository root:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/native/build-native.ps1 -Configuration Release -Architecture x64 -RunTests
dotnet test
python scripts/testing/run-cli-integration-test.py
```

All three commands must pass before creating a release tag. `scripts/build-release.ps1` also invokes the Native build and checks that `LivePhotoBox.Native.dll` is present in GUI and CLI outputs.

---

## 📁 Project Structure

```
live-photo-box/
├── LivePhotoBox.Core/        # Shared core library (protocols, merge/split/repair services, localization)
├── LivePhotoBox.Native/      # C++20 Native backend (stable C ABI; x64 DLL)
├── LivePhotoBox/             # Main project (WinUI 3 MSIX app)
│   ├── Assets/               # Icons, screenshots, static resources
│   ├── Controls/             # Custom controls (fullscreen lightbox, status bar)
│   ├── Converters/           # XAML value converters
│   ├── Helpers/              # Utilities (scrolling, formatting, hover preview, etc.)
│   ├── Models/               # Data models
│   ├── Services/             # GUI business logic (delegates to LivePhotoBox.Core)
│   ├── Strings/              # Multilingual resources (zh-Hans / en-US)
│   ├── ViewModels/           # MVVM ViewModel layer
│   └── Views/                # XAML pages
├── LivePhotoBox.CLI/         # Command-line interface (livephotobox)
├── tests/                    # Test projects (Core / CLI / UI / benchmarks)
├── docs/                     # Project documentation
├── changelogs/               # Release notes
├── scripts/                  # Build & packaging scripts
│   └── native/                # MSVC/Native build and smoke-test script
├── artifacts/                # Native build outputs (gitignored)
├── screenshots/              # Screenshots
├── lpb.cmd                   # Dev alias for the source CLI (runs current code, same as `dotnet run`)
└── README.md
```

📖 See <strong><a href="docs/项目总览.md">Project Overview</a></strong> for the complete directory reference.

---

## 📋 Changelog

📋 CHANGELOG: &nbsp;<strong><a href="changelogs/CHANGELOG.md">English</a> &nbsp;·&nbsp; <a href="changelogs/CHANGELOG.zh-CN.md">简体中文</a></strong>

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

---

## 📄 License

This project is open-source under the **GNU General Public License v3.0 (GPL 3.0)**. See the [LICENSE](LICENSE) file for details.

---

## 🙏 Credits

| Tool / Library | Purpose | License |
|---------------|---------|---------|
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MVVM framework | MIT |
| [PhotoSauce.MagicScaler](https://github.com/saucecontrol/PhotoSauce) | High-performance image scaling | MIT |
| [Microsoft.Graphics.Win2D](https://github.com/microsoft/Win2D) | GPU-accelerated 2D graphics | MIT |
| [Markdig](https://github.com/xoofx/markdig) | Markdown rendering | BSD-2-Clause |
| [FluentIcons](https://github.com/davidxuang/FluentIcons) | Fluent icon set | MIT |
| [ExifTool](https://exiftool.org/) / [FFmpeg](https://ffmpeg.org/) | Historical releases & offline verification test fixtures (`run-cli-integration-test.py`) | Perl / LGPL |

---

## ⭐️ Star History

<a href="https://www.star-history.com/?repos=lengxiqwq%2Flive-photo-box&type=date&legend=top-left">
 <picture>
   <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/chart?repos=lengxiqwq/live-photo-box&type=date&theme=dark&legend=top-left&sealed_token=OaKwkWC2X0kmrzy16Wj7Qef0e-M9T5jTHXDQh3JN1hdjg3twCmEZxCJ3vmpH8ZMlK6jjI7F_ntJENcAl11D2S64ym_jrGAnMVVtAtYVCtgUGBaYy9T5JPQ" />
   <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/chart?repos=lengxiqwq/live-photo-box&type=date&legend=top-left&sealed_token=OaKwkWC2X0kmrzy16Wj7Qef0e-M9T5jTHXDQh3JN1hdjg3twCmEZxCJ3vmpH8ZMlK6jjI7F_ntJENcAl11D2S64ym_jrGAnMVVtAtYVCtgUGBaYy9T5JPQ" />
   <img alt="Star History Chart" src="https://api.star-history.com/chart?repos=lengxiqwq/live-photo-box&type=date&legend=top-left&sealed_token=OaKwkWC2X0kmrzy16Wj7Qef0e-M9T5jTHXDQh3JN1hdjg3twCmEZxCJ3vmpH8ZMlK6jjI7F_ntJENcAl11D2S64ym_jrGAnMVVtAtYVCtgUGBaYy9T5JPQ" />
 </picture>
</a>

<!-- INSIGHTS:START -->
**📊 Repository Traffic**

Views: **2,390** ｜ Uniques: **183** (14-day) ｜ Clones: **2,068** ｜ Cloners: **161** (14-day)

**Top referrers (14-day):** github.com · Bing · Google · chatgpt.com · t.co · doubao.com  
**Top content (14-day):** releases · releases/tag/v2.2.1 · README.zh-CN.md · releases/tag/v2.2.0

> Data since 2026-08-02 · Last updated: 2026-09-04 (UTC+8)
<!-- INSIGHTS:END -->

---

<p align="center">
  <sub>Made with ❤️ by <a href="https://github.com/lengxiqwq">LengxiQwQ</a></sub>
</p>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/stargazers"><img src="https://img.shields.io/github/stars/lengxiqwq/live-photo-box?style=social" alt="Stars"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/network/members"><img src="https://img.shields.io/github/forks/lengxiqwq/live-photo-box?style=social" alt="Forks"></a>
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/downloads/lengxiqwq/live-photo-box/total?style=social" alt="Downloads"></a>
</p>
