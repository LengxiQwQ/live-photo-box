<div align="center">
<h1>
  <img src="https://raw.githubusercontent.com/lengxiqwq/live-photo-box/master/LivePhotoBox/Assets/Icons/AppIcon-full.png" width="130" align="left" hspace="16" />
  Live Photo Box（实况照片工具箱）
</h1>
<p><em>Unifying all live photo protocols for seamless cross-device viewing & migration</em></p>

<p align="center">
  <a href="https://github.com/lengxiqwq/live-photo-box/releases"><img src="https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=latest%20release" alt="Latest release"></a>
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11" alt="Platform">
  <img src="https://img.shields.io/badge/C%23-13.0-239120?style=flat-square&logo=csharp" alt="C# 13" />
  <img src="https://img.shields.io/badge/C%2B%2B-20-00599C?style=flat-square&logo=c%2B%2B" alt="C++20" />
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet" alt=".NET 9" />
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

## ✨ Core Features

### 🖼️ Live Photo Edit

Change the key photo / cover frame of a Live Photo, picking the perfect moment from the video timeline.

- Filmstrip video timeline with frame-by-frame preview
- One-click cover replacement, export single frames or all video frames, or export as video / GIF animation
- Quick live photo protocol conversion
- View basic file attributes and live photo protocol information

### 🔗 Merge Live Photo (Combo)

Convert a **dual-file Live Photo** (or any image + video pair) into a **single-file Live Photo**, viewable on Windows and Android devices.

- **Merge anything in one click**: drag & drop or pick an image (`JPG` / `HEIC`) + a video (`MP4` / `MOV`), or scan a whole folder and auto-pair everything into a batch queue
- **Smart pairing**: auto-match image + video by filename, Apple `ContentIdentifier` UUID, or vivo camera ID; or merge ready-made dual-file pairs directly
- **Brand switching**: switch target protocols in one click, paired with `JPEG+MP4` / `JPEG+MOV` / `HEIC+MP4` / `HEIC+MOV` / `HEIC+MP4(H.265)` output formats (automatically filters supported options)
- **Batch file naming**: compose filenames from template segments (original name / protocol / date / time / EXIF date-time / counter / custom text) with drag-to-reorder, presets, separators, and live preview — no regex needed
- **Post-processing**: automatically move source files to a specified folder or Recycle Bin on completion
- **Parallel batch merging**: queue with search, multi-dimension sorting, and status filters; multi-task parallel processing with real-time progress, success/failure counts, and elapsed time

| Merge Protocol | Target Devices | Status |
|---|---|---|
| Google - Micro Video (v1) | Windows / Xiaomi (legacy MIUI) / Pixel | ✅ Supported |
| Google - Motion Photo (v2) | Windows / Xiaomi / Pixel | ✅ Supported |
| OPPO - O-Live Photo | Windows / Xiaomi / OPPO | ✅ Supported |
| HUAWEI - Moving Photo | Huawei / Honor | ✅ Supported |
| Samsung - Motion Photo | Windows / Samsung | ✅ Supported |
| vivo - Live Photo | Windows / vivo (≥ x300) | 🟡 In Testing |

### 📸 Split Live Photo

Split a **Live Photo** (single-file format) into a **dual-file Live Photo**, or into an independent still image (`JPG` / `HEIC`) and video (`MP4` / `MOV`).

- **Batch split**: scan a folder, auto-detect and queue all Live Photos; filter by protocol (Google / OPPO / vivo / Samsung / Huawei) to split specific brands
- **Protocol output**: convert to Apple / vivo dual-file Live Photos (writing pairing metadata), or remux to `HEIC+MOV` / `JPG+MOV` / `JPG+MP4` formats
- **Strip live photo metadata**: prevents extracted still images from being falsely recognized as live photos again; preserves all other metadata including `EXIF` / `ICC` / `GPS` / capture settings
- **Naming templates**: segment-based naming consistent with the merge page, supporting drag-and-drop reordering and live preview

> ⚠️ **About iPhone / iPad**: Due to iOS system limitations, Live Photos cannot be imported directly into iOS devices. This software generates standard Live Photo data; import requires third-party tools like i4Tools.

| Split Protocol | Target Devices | Status |
|---|---|---|
| Apple - Live Photo | iPhone / iPad | ✅ Supported |
| vivo - Live Photo | vivo (≤ x200) | 🟡 In Testing |

### 🛠️ Repair Live Photo

Repair display anomalies from exported Apple Live Photos. View **diagnostic details** for each photo after scanning, and filter by file type or repair status.

- **Excess thumbnail & horizontal stretching** (before iOS 17.3): Apple embedded low-resolution thumbnails with orientation tags, causing Windows to treat them as horizontal photos and stretch or compress them. Clears orientation anomalies and strips excess thumbnails
- **Front camera video rotation**: iPhone front cameras store vertical pixels horizontally and rely on orientation tags, which Windows ignores. Clears the rotation matrix so videos play in the correct orientation
- **HEIC orientation errors**: Corrects erroneous `Orientation` tags if present

### 📂 Photo Organize (In Development)

Automatically scan, categorize, and archive photos by device, date, and Live Photo type based on metadata. Starting with iPhone photos, expanding to more brands.

---

## 💻 Command Line (CLI)

Live Photo Box ships a **command-line interface** — `livephotobox` — that shares 100% of its core logic with the GUI, ideal for scripting and AI agents.

- **Commands**: `merge` (single-pair or batch merge), `split` (split into photo + video), `repair` (repair Apple live photo display issues), `cover` (inspect/modify cover timestamp), `protocols` (protocol & format compatibility query), `update` / `update-check` (check & install updates)
- **Four executable aliases**: `livephotobox` / `livephoto` / `livebox` / `lpb`
- **Batch pairing & naming**: auto-pair by filename, Apple `ContentIdentifier` UUID, or vivo camera ID; rename outputs with templates like `-n custom:{name}_{date}`; `--after` moves sources to a folder / recycle bin on completion
- **Script-friendly**: `--json` outputs structured results that scripts and AI agents can consume directly; `--dry-run` previews operations without touching files
- **Sane output defaults**: single-pair merges write next to the source photo (protocol-suffixed name); batch merges write into a `{folder}_<protocol>` subfolder — never the terminal’s current directory. `-w` overwrites existing outputs instead of auto-renaming
- **Distribution**: bundled with the installer / portable build (optional “Add to PATH”), or a standalone single-file `-x64-cli.zip` (no install needed; ships `add-to-path.cmd` / `remove-from-path.cmd` helpers to add/remove PATH in one click); install the CLI-only edition via `winget install LengxiQwQ.LivePhotoBox`

📖 **CLI User Guide**: [English](docs/CLI-User-Guide.md) · [简体中文](docs/CLI-User-Guide.zh-CN.md)

---

## 🛠️ Tech Stack

| Technology                | Version      | Role                                                                 |
| ------------------------- | ------------ | -------------------------------------------------------------------- |
| C#                        | 13.0         | GUI, CLI, application workflows and orchestration                    |
| C++                       | C++20        | Low-level media containers, metadata and Live/Motion Photo protocols |
| .NET                      | 9.0          | Application runtime                                                  |
| WinUI 3 / Windows App SDK | 1.8          | Windows desktop UI                                                   |
| CommunityToolkit.Mvvm     | 8.4.2        | MVVM and state management                                            |
| LivePhotoBox.Native       | Stable C ABI | Bridge between C# and the Native C++ core                            |
| WIC                       | Windows      | Image decoding, encoding and conversion                              |
| Windows Media Foundation  | Windows      | Video probing, remuxing and transcoding                              |
| PhotoSauce.MagicScaler    | 0.15.0       | Image scaling and processing                                         |
| System.CommandLine        | 2.0.11       | CLI framework                                                        |
| MSIX / Self-contained     | —            | GUI and CLI distribution                                             |
> Note: Some currently published releases still use the previous external-tool-based media pipeline. The development branch is moving these responsibilities into LivePhotoBox.Native, and the new Native architecture will replace that implementation in a future release.

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
| [FFmpeg](https://ffmpeg.org/) | Previous-generation runtime & independent verification reference | LGPL/GPL |
| [ExifTool](https://exiftool.org/) | Previous-generation runtime & independent verification reference | Perl |

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

Views: **2,483** ｜ Uniques: **209** (14-day) ｜ Clones: **2,110** ｜ Cloners: **171** (14-day)

**Top referrers (14-day):** github.com · Bing · Google · chatgpt.com · t.co · doubao.com  
**Top content (14-day):** releases · releases/tag/v2.2.1 · README.zh-CN.md · issues

> Data since 2026-08-02 · Last updated: 2026-09-05 (UTC+8)
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
