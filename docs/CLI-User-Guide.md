# Live Photo Box CLI — User Guide

[![Latest release](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=latest%20release)](https://github.com/lengxiqwq/live-photo-box/releases) [![License](https://img.shields.io/badge/license-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![Repository](https://img.shields.io/badge/Repository-GitHub-0078D7?style=flat-square&logo=github)](https://github.com/lengxiqwq/live-photo-box) [![Issues](https://img.shields.io/badge/Issues-Report-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## Overview

`livephotobox` (`lpb`) is the command-line companion to **Live Photo Box**. It merges a photo (`JPG` / `HEIC`) and a video (`MP4` / `MOV`) into a single-file **live photo** — the format phone galleries play as a moving image.

It shares 100% of its logic with the GUI, so it's ideal for scripting and AI agents. Five commands are available: `merge`, `protocols`, `info`, `update-check`, and `update`. Splitting and repair remain GUI-only for now.

---

## Distribution Packages

Three packages are available on the [Releases page](https://github.com/lengxiqwq/live-photo-box/releases):

| Package | Contents | Best for | PATH |
|---------|----------|----------|------|
| `*-x64-setup.exe` | GUI + CLI, installed via setup wizard | General users who want the full app | Optional during install |
| `*-x64-portable.zip` | GUI + CLI, no installation required | Portable use on USB drives, or trying without installing | Add manually |
| `*-x64-cli.zip` | CLI only, no GUI or GUI dependencies | Servers, scripts, CI/CD, minimal footprint | Add manually |

All three packages include the same `livephotobox.exe` and its four aliases. The CLI-only package is the smallest — it omits the WinUI GUI and its runtime (~80 MB saved).

---

## Adding the CLI to your PATH

On Windows, running an executable from the current folder requires a `.\` prefix — e.g. `.\lpb --version`. To call `lpb` (or any alias) from any directory, add the install folder to your **user PATH**.

The package includes two helper scripts at its root for one-click setup:

- `add-to-path.cmd` — double-click to add this folder to your user PATH (no admin required)
- `remove-from-path.cmd` — double-click to remove it from your user PATH again

Run the script from the folder that contains `livephotobox-boot.exe` (the portable / CLI package root). Restart your terminal afterwards, then any alias works globally:

| Without PATH | With PATH |
|--------------|-----------|
| `.\lpb merge photo.heic video.mov` — only from the CLI folder | `lpb merge photo.heic video.mov` — from any folder |

---

## Updating

Updates are **user-triggered** — the CLI never checks in the background.

| Command | Action |
|---------|--------|
| `lpb update` | Check GitHub; if a newer version exists, download the matching package and install it |
| `lpb update-check` | Check only — no install |

**Options:**

| Option | Applies to | Description |
|--------|------------|-------------|
| `-y`, `--yes` | `update` | Skip the confirmation prompt and update automatically (required for scripts) |

When a newer version is found, `lpb update` prints the version and the matching package, then asks `Update now? [Y/n]` — Enter or `y` proceeds. The package is picked automatically by install type:

| Install type | Package |
|--------------|---------|
| Portable CLI-only | `*-x64-cli.zip` |
| Portable bundle (GUI + CLI) | `*-x64-portable.zip` |
| Installer (Inno Setup, GUI + CLI) | `*-x64-setup.exe` |

Both commands need internet; on failure they print the reason and a `Manual download: …` link. WinGet-managed copies skip the built-in update — run `winget upgrade LengxiQwQ.LivePhotoBox` instead.

---

## Executable Aliases

The tool ships under four equivalent names — use whichever is shortest:

| Alias | Description |
|-------|-------------|
| `livephotobox` | Full name |
| `livephoto` | Shortened |
| `livebox` | Compact |
| `lpb` | Short for Live Photo Box |

---

## Quick Start

```powershell
# Show version
lpb --version

# Show detailed environment info (bundled tool versions, quick update check)
lpb info

# View protocol × format compatibility matrix
lpb protocols

# Convert a single pair (iPhone → Google Photos)
lpb merge photo.heic video.mov -p motion photo -y

# Batch-convert a folder (→ HUAWEI, auto-confirm; writes ./MyPhotos/MyPhotos_huawei/)
lpb merge -d ./MyPhotos -p huawei -y
```

---

## Commands

| Command | Description |
|---------|-------------|
| `lpb protocols` | View the protocol × format compatibility matrix |
| `lpb merge` | Merge image+video pairs (single pair or batch) |
| `lpb info` / `lpb --version` | Show version, environment, and bundled tool versions |

The `update` / `update-check` commands are covered in the Updating section above.

### `protocols` — View format compatibility matrix

```
lpb protocols
```

```
  Merge — protocol × format compatibility

  Protocol              JPEG+MP4   JPEG+MOV   HEIC+MP4   HEIC+MOV   HEIC+MP4(H.265)
  ────────────────────── ────────   ────────   ────────   ────────   ──────────────
  Fusion (testing)         ✅          ✅          ✖️          ✖️          ✖️
  Micro Video              ✅          ✅          ✖️          ✖️          ✖️
  Motion Photo             ✅          ✅          ✖️          ✅          ✖️
  OPPO O-Live              ✅          ✖️          ✖️          ✖️          ✖️
  vivo Live Photo          ✅          ✖️          ✖️          ✖️          ✖️
  Samsung Motion Photo     ✅          ✖️          ✅          ✖️          ✖️
  HUAWEI Moving Photo      ✅          ✖️          ✅          ✖️          ✅

  Split — single-file live photo splitting (split not yet supported — use the GUI app)

  Protocol            Devices
  ─────────────────────────────────────────
  Apple Live Photo    iPhone / iPad
  vivo Live Photo     vivo (≤ X300)
```

`✅` — supported &nbsp;|&nbsp; `✖️` — not supported

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

| Goal | Command |
|------|---------|
| HUAWEI native HEVC (single pair) | `lpb merge photo.jpg video.mp4 -p huawei -f heic+mp4-h265 -y` |
| Batch → HUAWEI, explicit output folder | `lpb merge -d ./MyPhotos -p huawei -o ./Output -y` |
| Recursive batch, keep folder structure | `lpb merge -d ./Photos -r -s -p motion photo -o ./Output -y` |
| Preview without creating folders | `lpb merge -d ./Photos -p motion photo --dry-run` |
| Custom filename template | `lpb merge -d ./Photos -p motion photo -n "custom:{name}_{protocol}_{date}" -y` |
| Overwrite instead of auto-renaming | `lpb merge photo.jpg video.mp4 -p huawei -y -w` |
| Set key photo position (2.5 s) | `lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y` |

---

### `info` / `--version` — Show version and environment

| Command | Prints |
|---------|--------|
| `lpb --version` | Compact version banner: version, build date, runtime, install channel, location |
| `lpb info` | Same fields, plus bundled tool versions (exiftool, ffmpeg, …) and a quick update check |

Both run instantly with no network — only `info`'s final update check needs it, reporting failure inline instead of failing the command. Output is colorized in an interactive terminal and falls back to plain text when redirected or when `NO_COLOR` is set.

---

## Full Option Reference

**Input**

| Option | Description |
|--------|-------------|
| `<image> <video>` | Image + video pair, auto-detected by extension, any order. Images: `.jpg .jpeg .heic .heif`; videos: `.mp4 .mov` |
| `-d, --dir <folder>` | Directory to scan (batch mode); files sharing the same base name auto-pair |
| `-r, --recursive` | Include subdirectories when scanning |
| `--pairing <method>` | Pairing strategy (batch only): `name` — by filename (default); `cid` — Apple ContentIdentifier UUID; `vivo` — vivo camera ID |
| `--key-timestamp <time>` | Key photo position on the video timeline (single-pair only). Accepts seconds (`1.5`), `mm:ss` (`1:30`), `hh:mm:ss` (`0:01:30`); default follows the source video |

**Output**

| Option | Description |
|--------|-------------|
| `-o, --output <folder>` | Output directory. Default: single pair → the image's own folder; batch → `{input}/{input}_<protocol>/`. Created as needed |
| `-w, --overwrite` | Overwrite an existing output in place; otherwise auto-rename (`photo.jpg` → `photo (2).jpg`) |
| `-s, --preserve-subdirs` | Replicate source subdirectory structure in the output |
| `--after <action>` | Post-merge action on successful pairs: `none` (default), `move:PATH`, or `recycle` |

**Format**

| Option | Description |
|--------|-------------|
| `-p, --protocol <p>` | Target protocol (default `motion photo`): `fusion`, `micro video` (V1), `motion photo` (V2), `oppo`, `vivo`, `samsung`, `huawei`. Run `lpb protocols` for the full matrix |
| `-f, --format <f>` | Output container (default: first available for the protocol): `jpg+mp4`, `jpg+mov`, `heic+mp4`, `heic+mov`, `heic+mp4-h265` |
| `-n, --naming <rule>` | Output filename rule. Default: single pair = `suffix`, batch = `keep`. `keep`, `suffix`, or `custom:TEMPLATE` (tokens below) |

Naming tokens:

| Token | Meaning |
|-------|---------|
| `{name}` | Source filename |
| `{protocol}` | Protocol short name |
| `{date}` | Current date (yyyyMMdd) |
| `{date:format}` | Custom date, e.g. `{date:yyyy-MM-dd}` |
| `{time}` | Current time (HHmmss) |
| `{exif_date}` | Photo capture date (from the file) |
| `{exif_time}` | Photo capture time (from the file) |
| `{counter}` | Auto-increment (001, 002, …) |
| `{counter:D3}` | Zero-padded counter, e.g. D3 = 001 |

**Execution**

| Option | Description |
|--------|-------------|
| `-j, --parallel <n>` | Max concurrent tasks (default: CPU core count, max 5) |
| `-y, --yes` | Skip all confirmation prompts. Required for scripting |
| `--dry-run` | Print planned operations without executing them |
| `-v, --verbose` | Per-file status instead of a summary only |
| `--all-variants` | Generate all protocol × format combos (single-pair only); output to `{dir}/{name}_variants/` |

### Default Output Location

When `-o` is omitted, output never lands in the terminal's current directory — it follows the **input**:

| Mode | Default output | Example |
|------|----------------|---------|
| Single pair | The **image's own directory** (photo and video may live in different folders; the photo wins) | `D:\Pics\IMG_001.jpg` + `D:\Videos\clip.mp4` → `D:\Pics\IMG_001_motionphoto.jpg` |
| Batch (`-d`) | A subfolder inside the input folder, named `{input_folder}_<protocol>` | `lpb merge -d ./MyPhotos -p motion photo` → `./MyPhotos/MyPhotos_motionphoto/` |

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
photo_MicroVideo_JPEG+MP4.jpg
...
photo_HUAWEI_MovingPhoto_HEIC+MP4 (H.265).heic
```

Notes:
- Single-pair mode only. Batch mode (`--dir`) is not supported.
- Naming is fixed — `--naming`, `--protocol`, and `--format` are ignored.
- `--key-timestamp` is supported — all variants use the same timestamp.
- Parentheses and spaces in names like `HEIC+MP4 (H.265)` are valid Windows filename characters.

---

### `--key-timestamp` — Set the key photo position in the video

When merging a single pair, the live photo metadata records **where on the video timeline the key photo (cover) belongs**. By default the tool follows the source video's own timeline (e.g. Apple MOV still-image time, vivo metadata); passing this option overrides it with your value.

```powershell
# Cover is at 2.5 seconds into the video
lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y

# mm:ss and hh:mm:ss forms are also accepted
lpb merge photo.jpg video.mp4 -p motion photo --key-timestamp 1:30.500 -y
```

- Time formats: seconds (`1.5`), `mm:ss` (`1:30`), `hh:mm:ss` (`0:01:30`), converted to microseconds internally.
- Single-pair mode only — with batch mode (`-d`) it exits with an error.
- Each protocol stores the timestamp differently; the tool adapts automatically:

| Protocol | Where it's stored |
|----------|-------------------|
| Motion Photo / OPPO / vivo / Samsung / Fusion | XMP (OPPO / Fusion also write the primary-photo timestamp field) |
| Micro Video | XMP `MicroVideoPresentationTimestampUs` |
| HUAWEI | MP4 `covertime` metadata + tail-bytes cover frame number (no XMP) |

- Can be combined with `--all-variants` — all variants share the same timestamp.
- Values beyond the video duration: HUAWEI clamps to the last frame; other protocols write the value as-is.

---

## Pairing Methods

In batch mode (`-d`), the tool must decide which image belongs to which video:

| Method | How pairs are matched | Example |
|--------|-----------------------|---------|
| `name` (default) | Same base name, different extension | `photo_001.jpg` + `photo_001.mp4` → paired |
| `cid` | Apple `ContentIdentifier` UUID match, regardless of filename | `IMG_0002.HEIC` + `renamed.MOV` → paired |
| `vivo` | vivo camera ID in the JPEG tail + MP4 metadata | `vivo_photo.jpg` + `vivo_video.mp4` → paired |

`cid` requires `exiftool.exe` in the `Tools\` directory alongside the executable (included in all packages); `name` and `vivo` need no external tools — pure file I/O.

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

| Action | Command |
|--------|---------|
| Archive source files | `lpb merge -d ./Photos -p motion photo --after "move:./Archived" -y` |
| Recycle source files | `lpb merge -d ./Photos -p motion photo --after recycle -y` |
| Leave source files unchanged (default) | `lpb merge -d ./Photos -p motion photo --after none -y` |

Only source files from **successfully** merged pairs are affected.

---

## Workflow Examples

```powershell
# Batch to universal Android format
lpb merge -d ./DCIM/Camera -p fusion -o ./LivePhotos -y

# Recursive batch with structure preservation + source archiving
lpb merge -d ./Photos -r -s -p motion photo -o ./Output --after "move:./Originals" -y

# Scripted batch with error logging
lpb merge -d ./Photos -p huawei -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "Some files failed — see errors.log" }
```

---

## Protocol Compatibility

**Merge** — the protocols `lpb merge` can produce:

| Merge Protocol | Devices | Status |
|---|---|---|
| Fusion Motion Photo | Windows / Android (universal) | 🟡 In testing |
| Google Micro Video | Windows / Xiaomi (legacy MIUI) / Pixel | ✅ Supported |
| Google Motion Photo | Windows / Xiaomi / Pixel | ✅ Supported |
| OPPO O-Live Photo | Windows / Xiaomi / OPPO | ✅ Supported |
| HUAWEI Moving Photo | HUAWEI / Honor | ✅ Supported |
| Samsung Motion Photo | Windows / Samsung | 🟡 In testing |
| vivo Live Photo | Windows / vivo (≥ X300) | 🟡 In testing |

**Split** — single-file live photo protocols (split not yet supported — use the GUI app for splitting):

| Split Protocol | Devices | Status |
|---|---|---|
| Apple Live Photo | iPhone / iPad | 🟡 In testing |
| vivo Live Photo | vivo (≤ X300) | 🟡 In testing |

---

## Exit Codes

| Code | Meaning |
|:---:|---------|
| 0 | All tasks completed successfully |
| 1 | Parameter error, or at least one task failed |
| 2 | Update check failed (network / GitHub unreachable) |
| 3 | Update skipped — this copy is WinGet-managed (use winget) |
| 130 | Cancelled by user (Ctrl+C) |

---

## Architecture

The CLI and the GUI desktop app share the same merge pipeline in `LivePhotoBox.Core` — both call `LivePhotoMergeRunnerService.ProcessSinglePairAsync()`, so any fix or protocol update applies to both. The CLI is English-only; all strings are embedded in `LivePhotoBox.Core.dll`.

---

## Troubleshooting

#### Unknown protocol error
Run `lpb protocols` to list valid protocol names and shorthand aliases.

#### Format not available for protocol
Run `lpb protocols` to view the compatibility matrix. For example, `heic+mp4-h265` is only available for `huawei`.

#### "exiftool not found" with `--pairing cid`
Add `exiftool.exe` to the `Tools\` folder next to the executable.

#### Output file extension differs from source
Expected behaviour. When the source is HEIC and a JPEG-based format is selected, the output uses `.jpg`. The internal structure is correct for the chosen protocol.

#### Permission denied or file in use
Close gallery apps or file explorers that may be accessing the source files. Locked files cannot be read or moved on Windows.

---

## Getting Help

- **Documentation:** [English](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.md) · [简体中文](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.zh-CN.md)
- **Bug reports & feature requests:** [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- **Latest release:** [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)
- **Repository:** [github.com/lengxiqwq/live-photo-box](https://github.com/lengxiqwq/live-photo-box)

If this project is useful to you, consider giving it a ⭐ Star on GitHub.
