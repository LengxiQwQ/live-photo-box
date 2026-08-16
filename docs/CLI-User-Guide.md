# Live Photo Box CLI — User Guide

[![Latest release](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=latest%20release)](https://github.com/lengxiqwq/live-photo-box/releases) [![License](https://img.shields.io/badge/license-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![Repository](https://img.shields.io/badge/Repository-GitHub-0078D7?style=flat-square&logo=github)](https://github.com/lengxiqwq/live-photo-box) [![Issues](https://img.shields.io/badge/Issues-Report-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## Overview

Live Photo Box is available in two forms — a graphical interface and a command line. The command-line entry point `livephotobox` (alias `lpb`) is designed for scripting, AI, and automation. For everyday interactive use, please use the graphical interface, available on [Microsoft Store](https://apps.microsoft.com/detail/9n3d1qnrtvch?referrer=appbadge&mode=full) and [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases).

---

## Distribution Packages

Three packages are available on the [Releases page](https://github.com/lengxiqwq/live-photo-box/releases):

| Package | Contents | Best for | PATH |
|---------|----------|----------|------|
| `*-x64-setup.exe` | GUI + CLI, installed via setup wizard | General users who want the full app | Optional during install |
| `*-x64-portable.zip` | GUI + CLI, no installation required | Portable use on USB drives, or trying without installing | Add manually |
| `*-x64-cli.zip` | CLI only, no GUI or GUI dependencies | Servers, scripts, CI/CD, minimal footprint | Add manually |

All three packages include the same `livephotobox.exe` and its four aliases. The CLI-only package is the smallest.

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

## Executable Aliases

The tool ships under four equivalent names — use whichever is shortest:

| Alias | Description |
|-------|-------------|
| `livephotobox` | Full name |
| `livephoto` | Shortened |
| `livebox` | Compact |
| `lpb` | Short for Live Photo Box |

---

## Updating

Updates are **user-triggered**.

| Command | Action |
|---------|--------|
| `lpb update` | Check GitHub; if a newer version exists, download the matching package and install it |
| `lpb update-check` | Check only — no install |

**Options:**

| Option | Applies to | Description |
|--------|------------|-------------|
| `-y`, `--yes` | `update` | Skip the confirmation prompt and update automatically (required for scripts) |

### WinGet installs

A copy installed with WinGet (`winget install LengxiQwQ.LivePhotoBox`) **does not use the built-in update** — WinGet owns installing, upgrading, and uninstalling:

- `lpb update` and `lpb update-check` still check GitHub and report a newer version, but `lpb update` does **not** download or install anything — it prints `Update with: winget upgrade LengxiQwQ.LivePhotoBox` and exits.
- To update a WinGet copy:
  ```
  winget upgrade LengxiQwQ.LivePhotoBox
  ```
- Uninstall the same way: `winget uninstall LengxiQwQ.LivePhotoBox`.
- Not sure which channel your copy is? Run `lpb --info` — a WinGet copy reports `Channel: WinGet (CLI-only)`.

### Other install types

For portable, portable-bundle, and installer copies, `lpb update` performs the update itself. When a newer version is found, it prints the version and the matching package, then asks `Update now? [Y/n]` — Enter or `y` proceeds. The package is picked automatically by install type:

| Install type | Package |
|--------------|---------|
| Portable CLI-only | `*-x64-cli.zip` |
| Portable bundle (GUI + CLI) | `*-x64-portable.zip` |
| Installer (Inno Setup, GUI + CLI) | `*-x64-setup.exe` |

Both commands need internet; on failure they print the reason and a `Manual download: …` link.

---

## Quick Start

```powershell
# Show version (single line); `lpb -v` is a shortcut for `lpb --version`
lpb --version

# Show detailed environment info (install details, bundled tool versions)
lpb --info

# View protocol × format compatibility matrix
lpb protocols

# Convert a single pair (iPhone → Google Photos)
lpb merge photo.heic video.mov -p motionphoto -y

# Batch-convert a folder (→ HUAWEI, auto-confirm; writes ./MyPhotos/MyPhotos_huawei/)
lpb merge -d ./MyPhotos -p huawei -y

# Split a single-file live photo back into photo + video
lpb split photo.jpg -y
```

---

## Commands

| Command | Description |
|---------|-------------|
| `lpb protocols` | View protocol × format compatibility and device support |
| `lpb merge` | Merge image+video pairs (single pair or batch) |
| `lpb split` | Split single-file live photos into separate image and video files |
| `lpb repair` | Analyze and repair live photo metadata |
| `lpb --info` / `lpb --version` (`-v`) | Show version, environment, and bundled tool versions |

The `update` / `update-check` commands are covered in the Updating section above.

### `protocols` — View protocol × format compatibility and device support

Run `lpb protocols` to view this interactively, or `lpb protocols --json` for structured output.

**Compatibility matrix** — which output formats each protocol supports:

| Protocol | JPEG+MP4 | JPEG+MOV | HEIC+MP4 | HEIC+MOV | HEIC+MP4 (H.265) |
|---|---|---|---|---|---|
| Micro Video | ✅ | ✅ | ✖️ | ✖️ | ✖️ |
| Motion Photo | ✅ | ✅ | ✖️ | ✅ | ✖️ |
| OPPO O-Live | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| vivo Live Photo | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| Samsung Motion Photo | ✅ | ✖️ | ✅ | ✖️ | ✖️ |
| HUAWEI Moving Photo | ✅ | ✖️ | ✅ | ✖️ | ✅ |

`✅` — supported &nbsp;|&nbsp; `✖️` — not supported

`heic+mp4-h265` (index 4) is HUAWEI-native HEVC (H.265).

**Merge — device support:**

| Protocol | Devices | Status |
|---|---|---|
| Micro Video | Windows / Xiaomi (legacy MIUI) / Pixel | ✅ Supported |
| Motion Photo | Windows / Xiaomi / Pixel | ✅ Supported |
| OPPO O-Live | Windows / Xiaomi / OPPO | ✅ Supported |
| vivo Live Photo | Windows / vivo (≥ X300) | 🟡 In testing |
| Samsung Motion Photo | Windows / Samsung | 🟡 In testing |
| HUAWEI Moving Photo | HUAWEI / Honor | ✅ Supported |

**Split — device support** (split is supported in the CLI — see the `split` section below):

| Protocol | Devices | Status |
|---|---|---|
| Apple Live Photo | iPhone / iPad | ✅ Supported |
| vivo Live Photo | vivo (≤ X200) | 🟡 In testing |

**Split — protocol × format compatibility:**

| Protocol | keep | jpg+mov | heic+mov | jpg+mp4 |
|---|---|---|---|---|
| None (split only) | ✅ | ✅ | ✅ | ✅ |
| Apple Live Photo | ✖️ | ✅ | ✅ | ✖️ |
| vivo Live Photo | ✖️ | ✖️ | ✖️ | ✅ |

**JSON output** for scripting — includes each protocol's index, display name, devices, status, and formats, plus the split table:

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
| Recursive batch, keep folder structure | `lpb merge -d ./Photos -r -s -p motionphoto -o ./Output -y` |
| Preview without creating folders | `lpb merge -d ./Photos -p motionphoto --dry-run` |
| Custom filename template | `lpb merge -d ./Photos -p motionphoto -n "custom:{name}_{protocol}_{date}" -y` |
| Overwrite instead of auto-renaming | `lpb merge photo.jpg video.mp4 -p huawei -y -w` |
| Set key photo position (2.5 s) | `lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y` |

---

#### Full Option Reference

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
| `-p, --protocol <p>` | Target protocol (default `motion photo`): `micro video` (V1), `motion photo` (V2), `oppo`, `vivo`, `samsung`, `huawei`. Run `lpb protocols` for the full matrix. Multi-word names also work without spaces (no quotes needed): `microvideo`, `motionphoto` |
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

#### Default Output Location

When `-o` is omitted, output never lands in the terminal's current directory — it follows the **input**:

| Mode | Default output | Example |
|------|----------------|---------|
| Single pair | The **image's own directory** (photo and video may live in different folders; the photo wins) | `D:\Pics\IMG_001.jpg` + `D:\Videos\clip.mp4` → `D:\Pics\IMG_001_motionphoto.jpg` |
| Batch (`-d`) | A subfolder inside the input folder, named `{input_folder}_<protocol>` | `lpb merge -d ./MyPhotos -p motionphoto` → `./MyPhotos/MyPhotos_motionphoto/` |

- Folder/file names are English: `MyPhotos_huawei/`, `IMG_001huawei.jpg`.
- Single-pair files are named `{source_name}<protocol_suffix>` by default (e.g. `IMG_001motionphoto.jpg`) so they never overwrite the source photo.
- Batch files keep their source names — the protocol suffix lives in the **folder** name instead.
- `--dry-run` prints the resolved output path and creates **no** folders.

#### `--all-variants` — Generate every protocol × format combo

Instead of running separate merge commands for each protocol and format, generate all 14 supported combinations (7 protocols × their available formats) in one go. Ideal for developer QA and testing.

```powershell
# Default: writes to {image_dir}/{name}_variants/
lpb merge photo.jpg video.mp4 --all-variants

# Specify output directory
lpb merge photo.jpg video.mp4 --all-variants -o ./Out
```

Output: `photo_variants/` (in the image's directory or specified output) contains 12 files:
```
photo_MicroVideo_JPEG+MP4.jpg
...
photo_HUAWEI_MovingPhoto_HEIC+MP4 (H.265).heic
```

Notes:
- Single-pair mode only. Batch mode (`--dir`) is not supported.
- Naming is fixed — `--naming`, `--protocol`, and `--format` are ignored.
- `--key-timestamp` is supported — all variants use the same timestamp.

#### `--key-timestamp` — Set the key photo position in the video

When merging a single pair, the live photo metadata records **where on the video timeline the key photo (cover) belongs**. By default the tool follows the source video's own timeline; passing this option overrides it with your value.

```powershell
# Cover is at 2.5 seconds into the video
lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.5 -y

# mm:ss and hh:mm:ss forms are also accepted
lpb merge photo.jpg video.mp4 -p motionphoto --key-timestamp 1:30.500 -y
```

- Time formats: seconds (`1.5`), `mm:ss` (`1:30`), `hh:mm:ss` (`0:01:30`).
- Single-pair mode only — with batch mode (`-d`) it exits with an error.
- Can be combined with `--all-variants` — all variants share the same timestamp.

#### Pairing Methods

In batch mode (`-d`), the tool must decide which image belongs to which video:

| Method | How pairs are matched | Example |
|--------|-----------------------|---------|
| `name` (default) | Same base name, different extension | `photo_001.jpg` + `photo_001.mp4` → paired |
| `cid` | Apple `ContentIdentifier` UUID match, regardless of filename | `IMG_0002.HEIC` + `renamed.MOV` → paired |
| `vivo` | vivo camera ID in the JPEG tail + MP4 metadata | `vivo_photo.jpg` + `vivo_video.mp4` → paired |

`cid` requires `exiftool.exe` in the `Tools\` directory alongside the executable (included in all packages); `name` and `vivo` need no external tools.

#### Naming Templates

| Goal | Template | Example Output |
|------|----------|----------------|
| Keep original name | `-n keep` | `IMG_001.jpg` |
| Append protocol suffix | `-n suffix` | `IMG_001huawei.jpg` |
| Name + date | `-n "custom:{name}_{date}"` | `IMG_001_20260803.jpg` |
| Protocol as subdirectory | `-n "custom:{protocol}/{name}"` | `huawei/IMG_001.jpg` |
| Sequential numbering | `-n "custom:Photo_{counter:D4}"` | `Photo_0001.jpg` |
| Full metadata | `-n "custom:{name}_{protocol}_{date}_{time}"` | `IMG_001_huawei_20260803_143022.jpg` |

> **Note:** when `-n` is omitted, **single-pair** merges default to `suffix` (so the output never collides with the source photo) while **batch** merges default to `keep` (outputs go into a separate subfolder, so names stay unchanged). Explicitly passing `-n` always wins.

#### After-Completion Actions

| Action | Command |
|--------|---------|
| Archive source files | `lpb merge -d ./Photos -p motionphoto --after "move:./Archived" -y` |
| Recycle source files | `lpb merge -d ./Photos -p motionphoto --after recycle -y` |
| Leave source files unchanged (default) | `lpb merge -d ./Photos -p motionphoto --after none -y` |

Only source files from **successfully** merged pairs are affected.

#### Workflow Examples

```powershell
# Batch to Google Motion Photo format
lpb merge -d ./DCIM/Camera -p motionphoto -o ./LivePhotos -y

# Recursive batch with structure preservation + source archiving
lpb merge -d ./Photos -r -s -p motionphoto -o ./Output --after "move:./Originals" -y

# Scripted batch with error logging
lpb merge -d ./Photos -p huawei -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "Some files failed — see errors.log" }
```

---

### `split` — Split single-file live photos

The reverse of `merge`: splits single-file live photos (an image with an appended video) back into a separate photo and video. Supports two operating modes:

| Mode | Arguments | Use case |
|------|-----------|----------|
| Single file | `<file>` (auto-detected by extension) | Split one single-file live photo |
| Batch folder | `-d` | Split every single-file live photo in a directory |

#### Examples

| Goal | Command |
|------|---------|
| Split a single file (photo + video next to the source) | `lpb split photo.jpg` |
| Batch split a folder, auto-confirm | `lpb split -d ./MyPhotos -y` |
| Convert the video to JPG+MP4 (H.264) | `lpb split photo.jpg -f jpg+mp4` |
| Preview without processing | `lpb split -d ./MyPhotos --dry-run` |
| Only split vivo live photos | `lpb split -d ./MyPhotos --pairing vivo -y` |
| Overwrite existing outputs | `lpb split photo.jpg -w` |
| Export all variants (Apple + vivo + no-protocol) | `lpb split photo.jpg --all-variants` |

---

#### Full Option Reference

**Input**

| Option | Description |
|--------|-------------|
| `<file>` | One single-file live photo to split: `.jpg .jpeg .heic .heif` (image with an appended video) |
| `-d, --dir <folder>` | Folder with single-file live photos (batch mode); all detected live photos are split |
| `--pairing <protocol>` | Only split live photos of this protocol: `all` (no filter, default), `v1` (MicroVideo), `v2` (MotionPhoto), `oppo`, `vivo`, `samsung`, `huawei` |
| `-r, --recursive` | Include subdirectories when scanning |

**Output**

| Option | Description |
|--------|-------------|
| `-o, --output <folder>` | Output folder. Default: single file → the source file's own directory; batch → `{folder}_split` inside the input folder. Created as needed |
| `-w, --overwrite` | Replace existing files; otherwise name conflicts get auto-renamed (`photo.jpg` → `photo (2).jpg`) |
| `-s, --preserve-subdirs` | Replicate source subdirectory structure in the output |
| `--after <action>` | Post-split action on successful files: `none` (default), `move:PATH`, or `recycle` |

**Format**

| Option | Description |
|--------|-------------|
| `-p, --protocol <p>` | Target phone format (default `none`): `none` (split only), `apple` (Apple Live Photo), `vivo` (vivo Live Photo, ≤ X200). This iteration only splits the file — pairing metadata is not written yet |
| `-f, --format <f>` | Output format (default: first available for the protocol): `keep` (no conversion), `jpg+mov` (H.265), `heic+mov` (H.265), `jpg+mp4` (H.264) |
| `-n, --naming <rule>` | Output filename rule. Default: `keep`. `keep` (same name) or `custom:TEMPLATE` (tokens below) |

Naming tokens:

| Token | Meaning |
|-------|---------|
| `{name}` | Source filename |
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
| `-j, --parallel <n>` | How many files to process at once (default: CPU core count, max 5) |
| `-y, --yes` | Skip confirmation prompts. Useful for scripts / automation |
| `--dry-run` | Preview: show what would be done, don't actually process files |
| `-v, --verbose` | Show per-file status messages instead of summary only |
| `--all-variants` | Export ALL split variants (single-file mode only); output to `{output}/split_{name}_All_Variants/` |

#### Default Output Location

When `-o` is omitted, output never lands in the terminal's current directory — it follows the **input**:

| Mode | Default output | Example |
|------|----------------|---------|
| Single file | The **source file's own directory** | `lpb split photo.jpg` → photo + video next to the source |
| Batch (`-d`) | A subfolder inside the input folder, named `{folder}_split` | `lpb split -d ./MyPhotos` → `./MyPhotos/MyPhotos_split/` |

- The image keeps the source base name and extension; the video keeps the source video's container (`.mov` or `.mp4`).
- Splitting in place: when the image name would collide with the source file, it is auto-renamed (`photo.jpg` → `photo (2).jpg`); pass `-w` to overwrite instead.
- Batch files keep their source names — they land in the separate `{folder}_split/` subfolder.
- `--dry-run` prints the resolved output paths and creates **no** folders.

#### `--all-variants` — Export every split variant

From one single-file live photo, generate all 7 supported split variants in a single command (every protocol × format combination the GUI offers). Single-file mode only — batch (`-d`) is rejected. Ideal for developer QA and testing.

| Variant | Output pair |
|---------|-------------|
| No protocol (keep original) | `none_keep.<img-ext>` + `none_keep.<vid-ext>` |
| No protocol (JPG+MOV) | `none_jpg+mov.JPG` + `none_jpg+mov.MOV` |
| No protocol (HEIC+MOV) | `none_heic+mov.HEIC` + `none_heic+mov.MOV` |
| No protocol (JPG+MP4) | `none_jpg+mp4.JPG` + `none_jpg+mp4.MP4` |
| Apple Live Photo (JPG+MOV) | `apple_jpg+mov.JPG` + `apple_jpg+mov.MOV` |
| Apple Live Photo (HEIC+MOV) | `apple_heic+mov.HEIC` + `apple_heic+mov.MOV` |
| vivo Live Photo (JPG+MP4) | `vivo_jpg+mp4.JPG` + `vivo_jpg+mp4.MP4` |

```powershell
# Default: writes to {source_dir}/split_{name}_All_Variants/
lpb split photo.jpg --all-variants

# Specify output directory
lpb split photo.jpg --all-variants -o ./Out
```

Output: `split_photo_All_Variants/` contains the 14 files above. Files are named `{protocol}_{format}` (lowercase CLI values, e.g. `-p apple -f jpg+mov` → `apple_jpg+mov`) — the original name goes into the **folder** name only; no spaces in any file or folder name. For the keep variant the image keeps the source extension and the video keeps the source video's container (`.MOV` / `.MP4`). `-p` / `-f` / `-n` / `-w` / `--after` are ignored in this mode; `-j` still controls parallelism.

#### Protocol × Format Matrix

Which output formats each split protocol supports:

| Protocol | keep | jpg+mov | heic+mov | jpg+mp4 |
|---|---|---|---|---|
| `none` (split only) | ✅ | ✅ | ✅ | ✅ |
| `apple` (Apple Live Photo) | ✖️ | ✅ | ✅ | ✖️ |
| `vivo` (vivo Live Photo) | ✖️ | ✖️ | ✖️ | ✅ |

`✅` — supported &nbsp;|&nbsp; `✖️` — not supported

When `--format` is omitted, the default is the protocol's first available format: `keep` for `none`, `jpg+mov` for `apple`, `jpg+mp4` for `vivo`. Passing a `--format` the protocol does not support is an error (run `lpb protocols` to check).

#### Pairing Filter

`--pairing` restricts splitting to one protocol; live photos of other protocols are skipped. `all` (the default) scans everything.

| Value | Protocol |
|-------|----------|
| `all` | No filter (default) |
| `v1` | Micro Video (V1) |
| `v2` | Motion Photo (V2) |
| `oppo` | OPPO O-Live |
| `vivo` | vivo Live Photo |
| `samsung` | Samsung Motion Photo |
| `huawei` | HUAWEI Moving Photo |

#### Naming Templates

Split only supports `keep` (the default) and `custom:TEMPLATE` — there is no `suffix` mode. The template names both the image and the video (each keeps its own extension, e.g. `.jpg` / `.mov`).

| Goal | Template | Example Output |
|------|----------|----------------|
| Keep original name | `-n keep` (default) | `IMG_001.jpg` (image keeps its name) |
| Name + date | `-n "custom:{name}_{date}"` | `IMG_001_20260803.jpg` |
| Sequential numbering | `-n "custom:Photo_{counter:D4}"` | `Photo_0001.jpg` |

#### After-Completion Actions

| Action | Command |
|--------|---------|
| Archive source files | `lpb split -d ./Photos --after "move:./Archived" -y` |
| Recycle source files | `lpb split -d ./Photos --after recycle -y` |
| Leave source files unchanged (default) | `lpb split -d ./Photos --after none -y` |

Only source files from **successfully** split live photos are affected.

#### Workflow Examples

```powershell
# Split a single live photo, converting the video to JPG+MP4 (H.264)
lpb split photo.jpg -f jpg+mp4 -y

# Batch split a folder, only vivo live photos, auto-confirm
lpb split -d ./DCIM/Camera --pairing vivo -y

# Recursive batch with structure preservation + source archiving
lpb split -d ./Photos -r -s -o ./Output --after "move:./Originals" -y

# Scripted batch with error logging
lpb split -d ./Photos -o ./Out -y -v 2>errors.log
if ($LASTEXITCODE -ne 0) { Write-Host "Some files failed — see errors.log" }
```

---

### `repair` — Repair live photo metadata

Analyzes and fixes four kinds of metadata problems on existing live photo files: image rotation, embedded thumbnails, HEIC orientation, and video rotation. Images: `.jpg .jpeg .heic .heif`; videos: `.mov .mp4`.

| Mode | Arguments | Use case |
|------|-----------|----------|
| Single file | `<file>` (auto-detected by extension) | Fix one image or video |
| Batch folder | `-d` | Every media file in a directory |

#### Examples

| Goal | Command |
|------|---------|
| Fix a single file | `lpb repair photo.jpg` |
| Batch fix a folder | `lpb repair -d ./MyPhotos -y` |
| Preview without writing | `lpb repair -d ./MyPhotos --dry-run` |
| Only fix image rotation | `lpb repair -d ./Photos --no-thumbnail --no-heic --no-video -y` |
| Repair files from all devices | `lpb repair -d ./MyPhotos --all-devices -y` |
| Also copy intact files | `lpb repair -d ./MyPhotos --copy-perfect -y` |

---

#### Full Option Reference

**Input**

| Option | Description |
|--------|-------------|
| `<file>` | A single image or video to repair. Images: `.jpg .jpeg .heic .heif`; videos: `.mov .mp4` |
| `-d, --dir <folder>` | Directory to scan (batch mode). Every media file is analyzed; only files that need a fix are repaired |
| `-r, --recursive` | Include subdirectories when scanning |

**Fix**

| Option | Description |
|--------|-------------|
| `--no-rotate` | Disable image rotation fix (jpegtran lossless rotation) |
| `--no-thumbnail` | Disable embedded thumbnail stripping |
| `--no-heic` | Disable HEIC/HEIF orientation fix |
| `--no-video` | Disable video rotation bake (FFmpeg re-encode) |
| `--all-devices` | Repair files from all devices. Default: only Apple Live Photos (identified by their `ContentIdentifier` UUID) are repaired |
| `--repair-long-videos` | Also repair videos longer than 3.5 s (not real live photos). Default: skipped |
| `--copy-perfect` | Also copy files that need no repair to the output folder (batch mode only) |

All four fixes are **on by default** — use the `--no-*` flags to turn individual ones off.

**Output**

| Option | Description |
|--------|-------------|
| `-o, --output <folder>` | Output directory. Default: single file → `{name}_repaired{ext}` next to the source; batch → `{input}/{input}_repaired/`. Created as needed |
| `-w, --overwrite` | Overwrite an existing output in place; otherwise auto-rename (`photo.jpg` → `photo (2).jpg`) |
| `-s, --preserve-subdirs` | Replicate source subdirectory structure in the output |

**Execution**

| Option | Description |
|--------|-------------|
| `-j, --parallel <n>` | Max concurrent tasks (default: CPU core count, max 5) |
| `-y, --yes` | Skip all confirmation prompts. Required for scripting |
| `--dry-run` | Print planned operations without executing them |
| `-v, --verbose` | Per-file status instead of a summary only |

#### The Four Fixes

| Fix | What it does | Applies to |
|-----|--------------|------------|
| Image rotation | jpegtran lossless rotation, then resets the EXIF orientation tag | JPEG |
| Thumbnail strip | Strips the embedded thumbnail/preview image (reduces file size) | JPEG |
| HEIC orientation | Fixes EXIF orientation to match the QuickTime `Rotation` (mirror flag or angle mismatch) | HEIC/HEIF |
| Video rotation bake | FFmpeg re-encode baking the rotation matrix into the pixels | MOV/MP4 |

> **HEIC note:** the CLI enables the HEIC orientation fix by default (all four fixes on); the GUI's `IsHeicRepairEnabled` setting defaults to off. Pass `--no-heic` to match the GUI default.

#### Default Output Location

Repair never overwrites the source files. When `-o` is omitted:

| Mode | Default output | Example |
|------|----------------|---------|
| Single file | `{name}_repaired{ext}` in the source file's directory | `IMG_001.jpg` → `IMG_001_repaired.jpg` |
| Batch (`-d`) | `{input}/{input}_repaired/`, keeping source names | `lpb repair -d ./MyPhotos` → `./MyPhotos/MyPhotos_repaired/` |

#### Apple Live Photo Filter

By default only **Apple Live Photos** are repaired — identified by their `ContentIdentifier` UUID (present in both the still image and the paired video). Files without it are skipped. Pass `--all-devices` to repair files from every device.

#### Script Mode (JSON Output)

With `--json`, `repair` prints a single UTF-8 JSON document to stdout — no colors, no alignment, no prompts — so scripts can parse it reliably regardless of filename length or terminal width. `--json` implies `--yes` (skips confirmation).

Batch mode output:

```json
{
  "command": "repair",
  "mode": "batch",
  "input": "C:\\...\\Photos",
  "output": "C:\\...\\Photos_repaired",
  "scanned": 47,
  "apple": 39,
  "needsRepair": 27,
  "repaired": 27,
  "failed": 0,
  "skipped": 20,
  "errors": 0,
  "files": [
    { "Path": "C:\\...\\IMG_0139.JPG", "Name": "IMG_0139", "Status": "repaired", "Issue": "[90° rotation tag]", "Reason": "" },
    { "Path": "C:\\...\\other.mov", "Name": "other", "Status": "skipped", "Issue": "", "Reason": "non-Apple device" }
  ]
}
```

Top-level counts: `scanned` (media files found), `apple` (Apple Live Photos via ContentIdentifier), `needsRepair`, `repaired`, `failed`, `skipped`, `errors`. Under `--all-devices` the filter is off, so `apple` equals `scanned` (everything is treated as Apple).

`files[].Status` values: `repaired`, `failed`, `skipped`, `copied` (`--copy-perfect`), and `would-repair` / `would-copy` under `--dry-run`. Single-file mode may also return `cancelled` (interrupted).

Single-file mode returns a flat object instead: `command`, `mode`, `input`, `output`, `status`, `issue`, `reason`.

The JSON is UTF-8 encoded — when piping into a script, read stdout as UTF-8 (e.g. Python `json.loads(sys.stdin.buffer.read().decode("utf-8"))`).

#### Workflow Examples

```powershell
# Fix every live photo in a folder (Apple Live Photos only, auto-confirm)
lpb repair -d ./DCIM/Camera -y

# Fix all devices, recurse, keep folder structure — preview first
lpb repair -d ./Photos --all-devices -r -s --dry-run

# Strip thumbnails only — leave rotation and videos untouched
lpb repair -d ./Photos --no-rotate --no-heic --no-video -y
```

---

### `--info` / `--version` — Show version and environment

| Flag | Prints |
|------|--------|
| `lpb --version` / `lpb -v` | Version only (single line) |
| `lpb --info` | Version, install details (build date, runtime, platform, channel, location), log directory and current log file, bundled tool versions (exiftool, ffmpeg, …), repository and feedback links, and copyright |

Both run instantly with no network — environment info only; checking for updates is done via `lpb update-check`. Output is colorized in an interactive terminal and falls back to plain text when redirected or when `NO_COLOR` is set.

`-v` is a root-level shortcut for `--version`. Inside subcommands (e.g. `lpb merge -v`), `-v` keeps its subcommand meaning (`--verbose`).

---

## Exit Codes

| Code | Meaning |
|:---:|---------|
| 0 | All tasks completed successfully |
| 1 | Parameter error, or at least one task failed |
| 2 | Update check failed (network / GitHub unreachable) |
| 130 | Cancelled by user (Ctrl+C) |

---

## Troubleshooting

#### Unknown protocol error
Run `lpb protocols` to list valid protocol names and shorthand aliases.

#### Format not available for protocol
Run `lpb protocols` to view the compatibility matrix. For example, `heic+mp4-h265` is only available for `huawei`.

#### "exiftool not found" with `--pairing cid`
Add `exiftool.exe` to the `Tools\` folder next to the executable.

#### Output file extension differs from source
Expected behaviour. When the source is HEIC and a JPEG-based format is selected, the output uses `.jpg`.

#### Permission denied or file in use
Close gallery apps or file explorers that may be accessing the source files. Locked files cannot be read or moved on Windows.

---

## Getting Help

- **Documentation:** [English](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.md) · [简体中文](https://github.com/lengxiqwq/live-photo-box/blob/main/docs/CLI-User-Guide.zh-CN.md)
- **Bug reports & feature requests:** [GitHub Issues](https://github.com/lengxiqwq/live-photo-box/issues)
- **Latest release:** [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases)
- **Repository:** [github.com/lengxiqwq/live-photo-box](https://github.com/lengxiqwq/live-photo-box)

If this project is useful to you, consider giving it a ⭐ Star on GitHub.
