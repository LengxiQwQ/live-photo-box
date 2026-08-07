# Live Photo Box CLI — User Guide

[![Release](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7)](https://github.com/lengxiqwq/live-photo-box/releases) [![License](https://img.shields.io/badge/license-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![Download](https://img.shields.io/badge/Download-Releases-0078D7?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/releases) [![Issues](https://img.shields.io/badge/Issues-Report-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## Overview

`livephotobox` is a command-line utility for merging image and video files into live photos compatible with various smartphone gallery applications. A live photo is a single file containing both a still image and a short video — when viewed in a supported gallery, the video plays automatically.

The CLI currently supports **merge**, **protocols**, and **update-check** commands. Split and repair features remain in the GUI application.

---

## Distribution Packages

Three packages are available on the [Releases page](https://github.com/lengxiqwq/live-photo-box/releases):

| Package | Contents | Best for |
|---------|----------|----------|
| `*-x64-setup.exe` | GUI + CLI, installed via setup wizard | General users who want the full app |
| `*-x64-portable.zip` | GUI + CLI, no installation required | Portable use on USB drives, or trying without installing |
| `*-x64-cli.zip` | CLI only, no GUI or GUI dependencies | Servers, scripts, CI/CD, minimal footprint |

All three packages include the same `livephotobox.exe` and its six aliases. The CLI-only package is the smallest — it omits the WinUI GUI and its runtime (~80 MB saved).

### Keeping Up to Date

The CLI does not auto-update. To check for new versions, use the built-in command:

```powershell
lpb update-check
```

This queries the GitHub Releases API and compares the latest published version against your installed version. If a newer release is available, it prints the version number and download URL.

You can also visit the [Releases page](https://github.com/lengxiqwq/live-photo-box/releases) manually, download the latest package matching your use case, and replace the existing files.

---

## Quick Start

```powershell
# View protocol × format compatibility matrix
lpb protocols

# Convert a single pair (iPhone → Google Photos)
lpb merge photo.heic video.mov -p v2 -y

# Batch-convert a folder (→ HUAWEI, auto-confirm; writes ./MyPhotos/MyPhotos_huawei/)
lpb merge -d ./MyPhotos -p huawei -y

# Preview without executing
lpb merge -d ./MyPhotos --dry-run

# Generate all protocol × format variants at once (for testing/QA)
lpb merge photo.jpg video.mp4 --all-variants
```

---

## Executable Aliases

The tool ships under six equivalent names — use whichever is shortest:

| Alias | Description |
|-------|-------------|
| `livephotobox` | Full name |
| `livephoto` | Shortened |
| `livebox` | Compact |
| `lipbox` | Alternative |
| `lpb` | Short for Live Photo Box |
| `lpbx` | Extended short form |

```powershell
livephotobox protocols
lpb protocols
lipbox protocols
# All three produce identical output.
```

---

## Commands

### `protocols` — View format compatibility matrix

```
lpb protocols
```

```
  Protocol          JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+H265
  ─────────         ────────   ────────   ────────   ────────   ────────
  Fusion               ✅          ✅          ──          ──          ──
  V1_MicroVideo        ✅          ✅          ──          ──          ──
  V2_MotionPhoto       ✅          ✅          ──          ✅          ──
  OPPO_OLive           ✅          ──          ──          ──          ──
  vivo_LivePhoto       ✅          ──          ──          ──          ──
  Samsung_MotionPhoto  ✅          ──          ✅          ──          ──
  HUAWEI_MovingPhoto   ✅          ──          ✅          ──          ✅
```

`✅` — supported &nbsp;|&nbsp; `──` — not supported

`heic+mp4-h265` (index 4) is HUAWEI-native HEVC (H.265).

**JSON output** for scripting:

```powershell
lpb protocols --json
```

---

### `merge` — Merge image+video pairs

The primary command. Supports two operating modes:

| Mode | Arguments | Use case |
|------|-----------|----------|
| Single pair | `photo.jpg video.mp4` (auto-detected) | One image and one video |
| Batch folder | `-d` | Directory of pairs (auto-matched by filename) |

#### Examples

```powershell
# iPhone → Google Photos (V2)
lpb merge IMG_001.HEIC IMG_001.MOV -p v2 -y

# → HUAWEI native HEVC
lpb merge photo.jpg video.mp4 -p huawei -f heic+mp4-h265 -y

# Batch folder → HUAWEI, no prompts (default writes ./MyPhotos/MyPhotos_huawei/)
lpb merge -d ./MyPhotos -p huawei -y

# Batch folder → HUAWEI, explicit output folder
lpb merge -d ./MyPhotos -p huawei -o ./Output -y

# Batch with subdirectory scanning and structure preservation
lpb merge -d ./Photos -r -s -p v2 -o ./Output -y

# Dry run — preview only, creates no folders
lpb merge -d ./Photos -p v2 --dry-run

# Custom filename template
lpb merge -d ./Photos -p v2 -n "custom:{name}_{protocol}_{date}" -y

# Overwrite existing outputs instead of auto-renaming to " (2)"
lpb merge photo.jpg video.mp4 -p huawei -y -w
```

---

## Full Option Reference

```
lpb merge [options]

═══ INPUT ═══
  <image> <video>          Image + video file pair. Auto-detected by extension, any order.
                             Image formats: .jpg .jpeg .heic .heif
                             Video formats: .mp4 .mov
  -d, --dir <folder>       Directory to scan. Files sharing the same base name are
                             automatically paired. For batch mode.
  -r, --recursive          Include subdirectories when scanning.
  --pairing <method>       Pairing strategy (batch mode only):
                             name  — Match by filename (default)
                             cid   — Match by Apple ContentIdentifier UUID
                             vivo  — Match by vivo camera livephoto ID

═══ OUTPUT ═══
  -o, --output <folder>    Output directory. Default (when omitted):
                             • Single pair → the image's own directory (writes next to the photo)
                             • Batch      → {input_folder}/{input_folder}_<protocol>/ subfolder
                             Pass -o to override; the folder is created as needed.
  -w, --overwrite          Overwrite an existing output file in place. Without this,
                             conflicts auto-rename: photo.jpg → photo (2).jpg.
  -s, --preserve-subdirs   Replicate source subdirectory structure in the output.
  --after <action>         Post-merge action (successful pairs only):
                             none        — Leave source files in place (default)
                             move:PATH   — Move source files to the specified path
                             recycle     — Move source files to the Windows recycle bin

═══ FORMAT ═══
  -p, --protocol <p>       Target protocol [default: v2].
                             fusion  — Universal Android
                             v1      — Google Micro Video (legacy)
                             v2      — Google Motion Photo (modern)
                             oppo    — OPPO / OnePlus O-Live
                             vivo    — vivo Live Photo
                             samsung — Samsung Motion Photo
                             huawei  — HUAWEI / Honor Moving Photo
                             Run 'lpb protocols' for the full matrix.

  -f, --format <f>         Output container (default: first available for protocol).
                             jpg+mp4       — JPEG + H.264 MP4 (widest compatibility)
                             jpg+mov       — JPEG + MOV (Apple-style)
                             heic+mp4      — HEIC + H.264 MP4 (requires HEIC source)
                             heic+mov      — HEIC + MOV
                             heic+mp4-h265 — HEIC + H.265 MP4 (HUAWEI native HEVC)

  -n, --naming <rule>      Output filename rule. Default: suffix for a single pair, keep for batch.
                             keep           — Same as source image name (batch default)
                             suffix         — Append protocol short name: photo → photomotionphoto (single-pair default)
                             custom:TEMPLATE — Template with tokens:
                               {name}          Source filename
                               {protocol}      Protocol short name
                               {date}          Current date (yyyyMMdd)
                               {date:format}   Custom date, e.g. {date:yyyy-MM-dd}
                               {time}          Current time (HHmmss)
                               {exif_date}     Photo capture date (from file)
                               {exif_time}     Photo capture time (from file)
                               {counter}       Auto-increment (001, 002, …)
                               {counter:D3}    Zero-padded counter, e.g. D3 = 001

═══ EXECUTION ═══
  -j, --parallel <n>       Max concurrent tasks (default: CPU core count, max 5).
                             Higher values increase throughput, use more CPU & I/O.
  -y, --yes                Skip all confirmation prompts. Required for scripting.
  --dry-run                Print planned operations without executing them.
  -v, --verbose            Output per-file status instead of summary only.
  --all-variants           Generate all protocol × format combos (single-pair only).
                             Output to {image_dir}/{name}_variants/ by default. Files named {name}_{Protocol}_{Format}.ext.
```

### Default Output Location

When `-o` is omitted, output never lands in the terminal's current directory — it follows the **input**:

| Mode | Default output | Example |
|------|----------------|---------|
| Single pair | The **image's own directory** (photo and video may live in different folders; the photo wins) | `D:\Pics\IMG_001.jpg` + `D:\Videos\clip.mp4` → `D:\Pics\IMG_001_motionphoto.jpg` |
| Batch (`-d`) | A subfolder inside the input folder, named `{input_folder}_<protocol>` | `lpb merge -d ./MyPhotos -p v2` → `./MyPhotos/MyPhotos_motionphoto/` |

- Folder/file names are English: `MyPhotos_huawei/`, `IMG_001huawei.jpg`.
- Single-pair files are named `{source_name}<protocol_suffix>` by default (e.g. `IMG_001motionphoto.jpg`) so they never overwrite the source photo.
- Batch files keep their source names — the protocol suffix lives in the **folder** name instead.
- `--dry-run` prints the resolved output path and creates **no** folders.

### `--all-variants` — Generate every protocol × format combo

Instead of running separate merge commands for each protocol and format, generate all 14 supported combinations (7 protocols × their available formats) in one go. Ideal for developer QA and testing.

```powershell
# Default: writes to {image_dir}/{name}_variants/
lpb merge photo.jpg video.mp4 --all-variants

# Specify output directory
lpb merge photo.jpg video.mp4 --all-variants -o ./Out
```

Output: `photo_variants/` (in the image's directory or specified output) contains 14 files:
```
photo_Fusion_JPEG+MP4.jpg
photo_Fusion_JPEG+MOV.jpg
photo_V1_MicroVideo_JPEG+MP4.jpg
...
photo_HUAWEI_MovingPhoto_HEIC+MP4 (H.265).heic
```

Notes:
- Single-pair mode only. Batch mode (`--dir`) is not supported.
- Naming is fixed — `--naming`, `--protocol`, and `--format` are ignored.
- Parentheses and spaces in names like `HEIC+MP4 (H.265)` are valid Windows filename characters.

---

### `update-check` — Check for newer versions

```
lpb update-check
```

Queries the GitHub Releases API for the latest published version and compares it against the installed version.

Example output (up to date):
```
Current version : 2.1.1
Checking GitHub ... OK
Latest version  : 2.1.1

You are running the latest version.
```

Example output (update available):
```
Current version : 2.1.0
Checking GitHub ... OK
Latest version  : 2.1.1

A newer version is available: v2.1.1
  Live Photo Box v2.1.1
  Download: https://github.com/lengxiqwq/live-photo-box/releases/tag/v2.1.1
```

Requires internet access. On failure (network timeout, GitHub unreachable), prints the manual download URL and exits with code 2.

---

## Pairing Methods

When operating in batch mode (`-d`), the tool must determine which image belongs to which video.

### `name` (default)

Files with matching base names and different extensions are paired.

```
photo_001.jpg  +  photo_001.mp4  →  paired
photo_002.heic +  photo_002.mov  →  paired
IMG_1234.jpg   +  VID_1234.mp4   →  not paired (different base names)
```

### `cid` — Apple ContentIdentifier

Apple Live Photos carry a UUID in their `ContentIdentifier` metadata. Files sharing the same UUID are paired regardless of filename.

```
IMG_0001.HEIC  +  IMG_0001.MOV   →  filename pair
IMG_0002.HEIC  +  renamed.MOV    →  CID pair (matching UUID)
```

Requires `exiftool.exe` in the `Tools\` directory alongside the executable. Included in all distribution packages.

### `vivo` — vivo Camera ID

vivo devices embed a `com.android.camera.livephoto` identifier in both the JPEG tail and MP4 metadata. Matching IDs are paired.

```
vivo_photo.jpg  +  vivo_video.mp4  →  paired by vivo ID
```

No external tools required — pure file I/O.

---

## Naming Templates

| Goal | Template | Example Output |
|------|----------|----------------|
| Keep original name | `-n keep` | `IMG_001.jpg` |
| Append protocol suffix | `-n suffix` | `IMG_001huawei.jpg` |
| Name + date | `-n "custom:{name}_{date}"` | `IMG_001_20260803.jpg` |
| Protocol as subdirectory | `-n "custom:{protocol}/{name}"` | `huawei/IMG_001.jpg` |
| Sequential numbering | `-n "custom:Photo_{counter:D4}"` | `Photo_0001.jpg` |
| Full metadata | `-n "custom:{name}_{protocol}_{date}_{time}"` | `IMG_001_huawei_20260803_143022.jpg` |

> **Note:** when `-n` is omitted, **single-pair** merges default to `suffix` (so the output never collides with the source photo) while **batch** merges default to `keep` (outputs go into a separate subfolder, so names stay unchanged). Explicitly passing `-n` always wins.

---

## After-Completion Actions

```powershell
# Archive source files
lpb merge -d ./Photos -p v2 --after "move:./Archived" -y

# Recycle source files
lpb merge -d ./Photos -p v2 --after recycle -y

# Leave source files unchanged (default)
lpb merge -d ./Photos -p v2 --after none -y
```

Only source files from **successfully** merged pairs are affected.

---

## Workflow Examples

```powershell
# iPhone → Google Photos
lpb merge IMG_1234.HEIC IMG_1234.MOV -p v2 -y

# iPhone → HUAWEI (native HEVC)
lpb merge IMG_1234.HEIC IMG_1234.MOV -p huawei -f heic+mp4-h265 -y

# Batch to universal Android format
lpb merge -d ./DCIM/Camera -p fusion -o ./LivePhotos -y

# Recursive batch with structure preservation + source archiving
lpb merge -d ./Photos -r -s -p v2 -o ./Output --after "move:./Originals" -y

# Scripted batch with error logging
lpb merge -d ./Photos -p huawei -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "Some files failed — see errors.log" }
```

---

## Protocol Compatibility

| Protocol | Compatible Devices | Status |
|----------|--------------------|--------|
| Apple Live Photo | iPhone / iPad | Supported |
| Google Micro Video (V1) | Windows / Xiaomi (MIUI) / Pixel | Supported |
| Google Motion Photo (V2) | Windows / Xiaomi / Pixel | Supported |
| OPPO O-Live Photo | Windows / Xiaomi / OPPO / OnePlus | Supported |
| HUAWEI Moving Photo | HUAWEI / Honor | Supported |
| vivo Live Photo | Windows / Xiaomi / vivo (X300+) | Testing |
| Samsung Motion Photo | Samsung | Testing |

---

## Exit Codes

| Code | Meaning |
|:---:|---------|
| 0 | All tasks completed successfully |
| 1 | Parameter error, or at least one pair failed |
| 130 | Cancelled by user (Ctrl+C) |

---

## Architecture

The CLI and the GUI desktop application share the same merge pipeline in `LivePhotoBox.Core`:

```
LivePhotoBox.Core        ← Protocol logic, HEIC conversion, video transcoding
    ↑               ↑
    │               │
Live Photo Box    LivePhotoBox.CLI
(WinUI GUI)       (Console CLI)
```

Both call `LivePhotoMergeRunnerService.ProcessSinglePairAsync()`. Any fix or protocol update in Core applies to both.

The CLI is English-only. All strings are embedded in `LivePhotoBox.Core.dll` — no separate language files required.

---

## Troubleshooting

### Unknown protocol error
Run `lpb protocols` to list valid protocol names and shorthand aliases.

### Format not available for protocol
Run `lpb protocols` to view the compatibility matrix. For example, `heic+mp4-h265` is only available for `huawei`.

### "exiftool not found" with `--pairing cid`
The CID pairing method requires `exiftool.exe` in the `Tools\` directory alongside the executable. Included in all distribution packages.

### Output file extension differs from source
Expected behaviour. When the source is HEIC and a JPEG-based format is selected, the output uses `.jpg`. The internal structure is correct for the chosen protocol.

### Permission denied or file in use
Close gallery apps or file explorers that may be accessing the source files. Locked files cannot be read or moved on Windows.

---

## Getting Help

- **Documentation:** [English](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.md) · [简体中文](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.zh-CN.md)
- **Bug reports & feature requests:** [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- **Latest release:** [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)
- **Repository:** [github.com/lengxiqwq/live-photo-box](https://github.com/lengxiqwq/live-photo-box)

If this project is useful to you, consider giving it a ⭐ Star on GitHub.
