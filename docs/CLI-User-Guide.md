# Live Photo Box CLI — User Guide

[![Latest release](https://img.shields.io/github/v/release/lengxiqwq/live-photo-box?style=flat-square&color=0078D7&label=latest%20release)](https://github.com/lengxiqwq/live-photo-box/releases) [![License](https://img.shields.io/badge/license-GPL%203.0-blue?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/blob/main/LICENSE) [![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-0078D7?style=flat-square&logo=windows11)](https://github.com/lengxiqwq/live-photo-box) [![Repository](https://img.shields.io/badge/Repository-GitHub-0078D7?style=flat-square&logo=github)](https://github.com/lengxiqwq/live-photo-box) [![Issues](https://img.shields.io/badge/Issues-Report-red?style=flat-square)](https://github.com/lengxiqwq/live-photo-box/issues)

---

## Overview

Live Photo Box is available in two forms — a graphical interface and a command line. The command-line entry point `livephotobox` (alias `lpb`) is designed for scripting, AI, and automation. For everyday interactive use, please use the graphical interface, available on [Microsoft Store](https://apps.microsoft.com/detail/9n3d1qnrtvch?referrer=appbadge&mode=full) and [GitHub Releases](https://github.com/lengxiqwq/live-photo-box/releases).

---

## Installation

Four install options are available on the [Releases page](https://github.com/lengxiqwq/live-photo-box/releases):

| Method | Install | Contents | PATH |
|--------|---------|----------|------|
| WinGet | `winget install LengxiQwQ.LivePhotoBox` | CLI only | Added automatically — no manual step |
| Installer | Run `*-x64-setup.exe` | GUI + CLI | Optional during install — no manual step |
| Portable | Extract `*-x64-portable.zip` | GUI + CLI | Add manually |
| CLI-only | Extract `*-x64-cli.zip` | CLI only | Add manually |

All packages include the same `livephotobox.exe` and its four aliases. WinGet and installer copies get PATH set up during install — only the portable and CLI-only zips need manual PATH (see below). WinGet-managed copies are updated and uninstalled via WinGet — not `lpb update` (see Updating below).

---

## Adding the CLI to your PATH

On Windows, running an executable from the current folder requires a `.\` prefix — e.g. `.\lpb --version`. To call `lpb` (or any alias) from any directory, add the install folder to your **user PATH**. WinGet and installer copies get PATH set up during install — the steps below are only needed for the portable and CLI-only zips.

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

### WinGet-managed copies

A copy installed with WinGet **does not use the built-in update** — WinGet owns installing, upgrading, and uninstalling:

- `lpb update` / `update-check` still report newer versions, but `lpb update` does not install — it prints `Update with: winget upgrade LengxiQwQ.LivePhotoBox` and exits.
- Update: `winget upgrade LengxiQwQ.LivePhotoBox` · Uninstall: `winget uninstall LengxiQwQ.LivePhotoBox`.
- Not sure which channel your copy is? Run `lpb --info` — a WinGet copy reports `Channel: WinGet (CLI-only)`.

### Portable & installer copies

`lpb update` performs the update itself, asking `Update now? [Y/n]` first (Enter/`y` proceeds); the matching package is picked automatically.

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

# Convert standalone media through the Rebuilt Native pipeline (no external media CLI)
lpb convert input.mov -o output.mp4 --codec h264
lpb convert input.heic -o output.jpg

# Convert a single pair (iPhone → Google Photos)
lpb merge photo.heic video.mov -p motionphoto -y

# Batch-convert a folder (→ HUAWEI, auto-confirm; writes ./MyPhotos/MyPhotos_huawei/)
lpb merge -d ./MyPhotos -p huawei -y

# Split a single-file live photo back into photo + video
lpb split photo.jpg -y

# Batch-split a folder (folder is auto-detected; -d also works)
lpb split ./MyPhotos -y

# View current cover position & protocol info of an existing live photo (no changes)
lpb cover photo.jpg

# Change the cover to 1.5s into the video (writes {name}_cover{frame}.jpg next to the source)
lpb cover photo.jpg --at 1.5 -y
```

---

## Commands

| Command | Description |
|---------|-------------|
| `lpb convert` | Convert a standalone JPEG/HEIC image or MOV/MP4 video through the Rebuilt Native media pipeline |
| `lpb protocols` | View protocol × format compatibility and device support |
| `lpb merge` | Merge image+video pairs (single pair or batch) |
| `lpb split` | Split single-file live photos into separate image and video files |
| `lpb cover` | Change the cover frame (Key Photo) of an existing live photo; alias `keyphoto` |
| `lpb repair` | Analyze and repair live photo metadata |
| `lpb --info` / `lpb --version` (`-v`) | Show version, environment, and bundled tool versions |

The `update` / `update-check` commands are covered in the Updating section above.

### `convert` — Rebuilt Native standalone media conversion

`convert` accepts a standalone JPEG/HEIC image or MOV/MP4 video. Image output is selected by the output extension (`.jpg`/`.jpeg` or `.heic`/`.heif`). Video output is selected by the output extension (`.mp4` or `.mov`); use `--codec copy`, `--codec h264`, or `--codec hevc` to select stream copy/remux or a real Native transcode. The source is probed by Native before conversion; a failed probe is an explicit failure and never falls back to caller-supplied guesses.

```powershell
lpb convert input.mov -o output.mp4 --codec h264
lpb convert input.mp4 -o output.mov --codec hevc
lpb convert input.heic -o output.jpg
lpb convert input.jpg -o output.heic --overwrite
lpb convert input.jpg -o output.heic
```

---

### `protocols` — View protocol × format compatibility and device support

Run `lpb protocols` to view this interactively, or `lpb protocols --json` for structured output.

The command reports the active global backend and the protocol/format matrix. In the default `rebuilt` mode, standalone conversion and the current merge/split media paths use Native; rebuilt split exposes only protocol-free neutral output. Target protocol writers remain isolated for a later phase. Standalone media conversion is available via `lpb convert`.

**Compatibility matrix** — which output formats each protocol supports:

| Protocol | JPEG + MP4 | JPEG + MOV | HEIC + MP4 | HEIC + MOV | HEIC + MP4 (H.265) |
|---|---|---|---|---|---|
| Google Micro Video (v1) | ✅ | ✅ | ✖️ | ✖️ | ✖️ |
| Google Motion Photo (v2) | ✅ | ✅ | ✖️ | ✅ | ✖️ |
| OPPO O-Live Photo | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| vivo Live Photo | ✅ | ✖️ | ✖️ | ✖️ | ✖️ |
| Samsung Motion Photo | ✅ | ✖️ | ✅ | ✖️ | ✖️ |
| HUAWEI Moving Photo | ✅ | ✖️ | ✅ | ✖️ | ✅ |

`✅` — supported &nbsp;|&nbsp; `✖️` — not supported

**Merge — device support:**

| Protocol | Devices | Status |
|---|---|---|
| Google Micro Video (v1) | Windows / Xiaomi (legacy MIUI) / Pixel | ✅ Supported |
| Google Motion Photo (v2) | Windows / Xiaomi / Pixel | ✅ Supported |
| OPPO O-Live Photo | Windows / Xiaomi / OPPO | ✅ Supported |
| vivo Live Photo | Windows / vivo (≥ X300) | 🟡 In testing |
| Samsung Motion Photo | Windows / Samsung | ✅ Supported |
| HUAWEI Moving Photo | HUAWEI / Honor | ✅ Supported |

**Split — device support:**

| Protocol | Devices | Status |
|---|---|---|
| Neutral split | Any device | ✅ Supported |

**Split — protocol × format compatibility:**

| Protocol | Keep | JPG + MOV | HEIC + MOV | JPG + MP4 |
|---|---|---|---|---|
| None (split only) | ✅ | ✅ | ✅ | ✅ |
> In the Rebuilt Native pipeline, split exports protocol-free neutral media (`none`). Target protocol writers are not yet implemented.

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
| Batch folder | `<path>` (auto-detected: no extension) or `-d` | Directory of pairs (auto-matched by filename) |

#### Examples

| Goal | Command |
|------|---------|
| Batch merge a folder, auto-confirm | `lpb merge ./MyPhotos -p motionphoto -y` (folder auto-detected; `-d` also works) |
| HUAWEI native HEVC (single pair) | `lpb merge photo.jpg video.mp4 -p huawei -f heic+mp4-h265 -y` |
| Batch → HUAWEI, explicit output folder | `lpb merge -d ./MyPhotos -p huawei -o ./Output -y` |
| Recursive batch, keep folder structure | `lpb merge -d ./Photos -r -s -p motionphoto -o ./Output -y` |
| Preview without creating folders | `lpb merge -d ./Photos -p motionphoto --dry-run` |
| Custom filename template | `lpb merge -d ./Photos -p motionphoto -n "custom:{name}_{protocol}_{date}" -y` |
| Overwrite instead of auto-renaming | `lpb merge photo.jpg video.mp4 -p huawei -y -w` |
| Set key photo position (2.500 s) | `lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.500 -y` |

> **Note:** Wildcards (`*.jpg`) are not supported. Pass a folder (`-d`) or list files explicitly.

---

#### Full Option Reference

**Input**

| Option | Description |
|--------|-------------|
| `<image> <video>` | Image + video pair, auto-detected by extension, any order. Images: `.jpg .jpeg .heic .heif`; videos: `.mp4 .mov`. A single folder path without a file extension is auto-detected as batch mode |
| `-d, --dir <path>` | Directory to scan (batch mode); files sharing the same base name auto-pair. A path can also be passed as the positional argument |
| `-r, --recursive` | Include subdirectories when scanning |
| `--pairing <method>` | Pairing strategy (batch only): `name` — by filename (default); `cid` — Apple ContentIdentifier UUID; `vivo` — vivo camera ID |
| `--key-timestamp <time>` | Key photo position on the video timeline (single-pair only). Accepts seconds (`2.500`), `mm:ss` (`1:30.500`), `hh:mm:ss` (`0:01:30.500`); default follows the source video |

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
| `-p, --protocol <p>` | Target protocol (default `motion photo`): `micro video` (v1), `motion photo` (v2), `oppo`, `vivo`, `samsung`, `huawei`. Run `lpb protocols` for the full matrix. Multi-word names also work without spaces (no quotes needed): `microvideo`, `motionphoto` |
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
| `{frame}` | Cover frame number (1-based; cover command only) |

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
| Batch (folder / `-d`) | A subfolder inside the input folder, named `{input_folder}_<protocol>` | `lpb merge ./MyPhotos -p motionphoto` → `./MyPhotos/MyPhotos_motionphoto/` |

- Folder/file names are English: `MyPhotos_huawei/`, `IMG_001huawei.jpg`.
- Single-pair files are named `{source_name}<protocol_suffix>` by default (e.g. `IMG_001motionphoto.jpg`) so they never overwrite the source photo.
- Batch files keep their source names — the protocol suffix lives in the **folder** name instead.
- `--dry-run` prints the resolved output path and creates **no** folders.

#### `--all-variants` — Generate every protocol × format combo

Generates all 14 supported combinations (7 protocols × their available formats) in one command. Ideal for developer QA and testing.

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

#### `--key-timestamp` — Set the key photo position in the video

Sets **where on the video timeline the key photo (cover) belongs**. Default: follows the source video's own timeline.

```powershell
# Cover is at 2.500 seconds into the video
lpb merge photo.jpg video.mp4 -p huawei --key-timestamp 2.500 -y

# mm:ss and hh:mm:ss forms are also accepted
lpb merge photo.jpg video.mp4 -p motionphoto --key-timestamp 1:30.500 -y
```

- Time formats: seconds (`2.500`), `mm:ss` (`1:30.500`), `hh:mm:ss` (`0:01:30.500`).
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

> **Note:** omitted `-n`: single-pair defaults to `suffix`, batch to `keep`. An explicit `-n` always wins.

#### After-Completion Actions

| Action | Command |
|--------|---------|
| Archive source files | `lpb merge -d ./Photos -p motionphoto --after "move:./Archived" -y` |
| Recycle source files | `lpb merge -d ./Photos -p motionphoto --after recycle -y` |
| Leave source files unchanged (default) | `lpb merge -d ./Photos -p motionphoto --after none -y` |

Only source files from **successfully** merged pairs are affected.

---

### `split` — Split single-file live photos

The reverse of `merge`: splits single-file live photos (an image with an appended video) back into a separate photo and video. Supports two operating modes:

| Mode | Arguments | Use case |
|------|-----------|----------|
| Single file | `<file>` (auto-detected by extension) | Split one single-file live photo |
| Batch folder | `<path>` (auto-detected: no extension) or `-d` | Split every single-file live photo in a directory |

#### Examples

| Goal | Command |
|------|---------|
| Split a single file (photo + video next to the source) | `lpb split photo.jpg` |
| Batch split a folder, auto-confirm | `lpb split ./MyPhotos -y` (folder auto-detected; `-d` also works) |
| Convert the video to JPG+MP4 (H.264) | `lpb split photo.jpg -f jpg+mp4` |
| Preview without processing | `lpb split -d ./MyPhotos --dry-run` |
| Only split vivo live photos | `lpb split -d ./MyPhotos --pairing vivo -y` |
| Overwrite existing outputs | `lpb split photo.jpg -w` |
| Export all neutral media variants | `lpb split photo.jpg --all-variants` |

> **Note:** Wildcards (`*.jpg`) are not supported. Pass a folder (`-d`) or list files explicitly.

---

#### Full Option Reference

**Input**

| Option | Description |
|--------|-------------|
| `<file>` | One single-file live photo to split: `.jpg .jpeg .heic .heif` (image with an appended video), or a folder path — a path without a file extension is auto-detected as a folder (batch mode) |
| `-d, --dir <path>` | Folder with single-file live photos (batch mode); all detected live photos are split. A path can also be passed as the positional argument |
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
| `-p, --protocol <p>` | Target protocol (default `none`). Only `none` is currently available and exports protocol-free neutral media |
| `-f, --format <f>` | Output format (default: `keep`): `keep` (no conversion), `jpg+mov` (H.265), `heic+mov` (H.265), `jpg+mp4` (H.264) |
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
| `{frame}` | Cover frame number (1-based; cover command only) |

**Execution**

| Option | Description |
|--------|-------------|
| `-j, --parallel <n>` | How many files to process at once (default: CPU core count, max 5) |
| `-y, --yes` | Skip confirmation prompts. Useful for scripts / automation |
| `--dry-run` | Preview: show what would be done, don't actually process files |
| `-v, --verbose` | Show per-file status messages instead of summary only |
| `--all-variants` | Export all split variants (single-file mode only). Emits 4 protocol-free neutral media variants. Output goes to `{output}/split_{name}_All_Variants/` |

#### Default Output Location

When `-o` is omitted, output never lands in the terminal's current directory — it follows the **input**:

| Mode | Default output | Example |
|------|----------------|---------|
| Single file | The **source file's own directory** | `lpb split photo.jpg` → photo + video next to the source |
| Batch (folder / `-d`) | A subfolder inside the input folder, named `{folder}_split` | `lpb split ./MyPhotos` → `./MyPhotos/MyPhotos_split/` |

- The image keeps the source base name and extension; the video keeps the source video's container (`.mov` or `.mp4`).
- Splitting in place: when the image name would collide with the source file, it is auto-renamed (`photo.jpg` → `photo (2).jpg`); pass `-w` to overwrite instead.
- Batch files keep their source names — they land in the separate `{folder}_split/` subfolder.
- `--dry-run` prints the resolved output paths and creates **no** folders.

#### `--all-variants` — Export every split variant

From one single-file live photo, generate all rebuilt neutral split variants in one command. Single-file mode only — batch (`-d`) is rejected.

| Variant | Output pair |
|---------|-------------|
| No protocol (keep original) | `none_keep.<img-ext>` + `none_keep.<vid-ext>` |
| No protocol (JPG+MOV) | `none_jpg+mov.JPG` + `none_jpg+mov.MOV` |
| No protocol (HEIC+MOV) | `none_heic+mov.HEIC` + `none_heic+mov.MOV` |
| No protocol (JPG+MP4) | `none_jpg+mp4.JPG` + `none_jpg+mp4.MP4` |

```powershell
# Default: writes to {source_dir}/split_{name}_All_Variants/
lpb split photo.jpg --all-variants

# Specify output directory
lpb split photo.jpg --all-variants -o ./Out
```

Files are named `{protocol}_{format}` (lowercase CLI values, e.g. `-p none -f jpg+mov` → `none_jpg+mov`); the original name goes into the **folder** name only; no spaces in any name. For the keep variant the image keeps the source extension and the video keeps the source container (`.MOV` / `.MP4`). `-p` / `-f` / `-n` / `-w` / `--after` are ignored; `-j` still controls parallelism.

#### Protocol × Format Matrix

In the Rebuilt Native pipeline, split currently operates in neutral media mode (`none`):

| Protocol | Keep | JPG + MOV | HEIC + MOV | JPG + MP4 |
|---|---|---|---|---|
| `none` (split only) | ✅ | ✅ | ✅ | ✅ |

Omitted `--format` defaults to `keep`.

#### Pairing Filter

`--pairing` restricts splitting to one protocol; others are skipped.

| Value | Protocol |
|-------|----------|
| `all` | No filter (default) |
| `v1` | Google Micro Video (v1) |
| `v2` | Google Motion Photo (v2) |
| `oppo` | OPPO O-Live Photo |
| `vivo` | vivo Live Photo |
| `samsung` | Samsung Motion Photo |
| `huawei` | HUAWEI Moving Photo |

#### Naming Templates

Split supports only `keep` (default) and `custom:TEMPLATE` (no `suffix`). The template names both image and video, each keeping its own extension.

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

---

### `cover` — Change the cover frame (Key Photo)

Change the cover frame (Key Photo) of an existing live photo without re-encoding the video. Alias `keyphoto` (aligns with Apple terminology).

Auto-detects single-file (Huawei/V2/OPPO/Samsung/Fusion) and dual-file (Apple HEIC+MOV, Vivo old dual-file) formats.

#### Modes

`cover` has three modes:

| Mode | Command | What happens |
|------|---------|--------------|
| Read-only view | `lpb cover photo.jpg` | Shows protocol, total frames, duration and current cover frame. **No files are created or modified.** Also works with `--json` for scripts. |
| Preview (dry-run) | `lpb cover photo.jpg --at 2.5 --dry-run` or `--frame 10 --dry-run` | Shows the same info plus the new cover position and output path, then stops. **No files are created or modified.** |
| Execute | `lpb cover photo.jpg --at 2.5 -y` or `--frame 10 -y` | Writes a new live photo file with the selected cover frame. |

`--at` and `--frame` are two ways to specify the **same thing: the new cover position**. They are mutually exclusive because giving both would be ambiguous (a time and a frame number can disagree). Neither one is "check" or "execute" by itself — execution is controlled by `--dry-run` / confirmation / `--json`.

> **OPPO/OnePlus note**: OPPO Gallery locates the cover by timestamp and always lands one frame later. When you use `--frame` or `--at` on an OPPO/OnePlus live photo, the CLI automatically writes a timestamp one frame earlier so OPPO Gallery displays, exports and stores the exact frame you selected.

#### Usage Examples

| Goal | Command |
|------|---------|
| View total frames, duration & current cover (no changes) | `lpb cover photo.jpg` |
| Change cover to 2.5s into the video | `lpb cover photo.jpg --at 2.5 -y` |
| Dual-file Apple Live Photo | `lpb cover photo.heic video.mov --at 2.5 -y` |
| Frame number mode (1-based, 10 = the 10th frame) | `lpb cover photo.jpg --frame 10 -y` |
| Preview without changes | `lpb cover photo.jpg --at 2.5 --dry-run` |
| Workflow: inspect first, then set frame 10 | `lpb cover photo.jpg` → `lpb cover photo.jpg --frame 10 -y` |
| Alias `keyphoto` | `lpb keyphoto photo.jpg --at 2.5 -y` |
| Custom naming template | `lpb cover photo.jpg --at 2.5 -n "custom:{name}_frame{frame}" -y` |
| Overwrite existing output | `lpb cover photo.jpg --at 2.5 -y -w` |
| Custom output directory | `lpb cover photo.jpg --at 2.5 -o ./Output -y` |
| JSON script mode | `lpb cover photo.jpg --at 2.5 --json` |

#### Full Option Reference

**Input**

| Option | Description |
|--------|-------------|
| `<file>` | Live photo file path. Single-file auto-detects protocol; dual-file auto-pairs (Apple CID / filename) |
| `<image> <video>` | Explicit image+video pair for dual-file live photos (order auto-detected) |
| `--at <time>` | New cover position by **video time**. Accepts seconds (`2.500`), mm:ss (`1:30.500`), hh:mm:ss (`0:01:30.500`). Mutually exclusive with `--frame` |
| `--frame <number>` | New cover position by **frame number, 1-based** (1 = first frame, 10 = the 10th frame). Mutually exclusive with `--at` |

**Output**

| Option | Description |
|--------|-------------|
| `-o, --output <dir>` | Output directory (default: source file's directory) |
| `-n, --naming <rule>` | Output filename rule. Default: `suffix` (appends `_cover{frame}`). `keep` or `custom:TEMPLATE` |
| `-w, --overwrite` | Overwrite existing files (default: auto-rename) |

**Execution**

| Option | Description |
|--------|-------------|
| `-y, --yes` | Skip confirmation prompts |
| `--dry-run` | Preview: show current cover info, the new cover position and output path, without modifying files |
| `-v, --verbose` | Show detailed progress |
| `--json` | JSON output (implies `--yes`) |

#### Naming Template

The command generates `{source_name}_cover{frame}` by default (e.g. `IMG_1234_cover46.jpg`), customizable with `-n`:

| Token | Meaning | Example |
|-------|---------|---------|
| `{name}` | Source filename | `IMG_1234` |
| `{protocol}` | Protocol suffix | `huawei` |
| `{date}` | Current date | `20260820` |
| `{date:format}` | Custom date format | `{date:yyyy-MM-dd}` → `2026-08-20` |
| `{time}` | Current time | `143022` |
| `{exif_date}` | Capture date (from file) | `20260803` |
| `{exif_time}` | Capture time (from file) | `100530` |
| `{frame}` | **Cover frame number (1-based)** | `46` |
| `{counter}` | Auto-increment | `1` |
| `{counter:D3}` | Zero-padded counter | `001` |

**Naming Template Quick Reference**

| Goal | Template | Output Example |
|------|----------|----------------|
| Default (with frame) | `-n "custom:{name}_cover{frame}"` | `IMG_1234_cover46.jpg` |
| Keep original name | `-n keep` | `IMG_1234.jpg` |
| With date | `-n "custom:{name}_{protocol}_{date}_{frame}"` | `IMG_1234_huawei_20260820_46.jpg` |
| With timestamp | `-n "custom:{name}_at{frame}_{time}"` | `IMG_1234_at46_143022.jpg` |

#### Default Output Location

| Mode | Default Output Directory | Example |
|------|------------------------|---------|
| Single-file | Source file's directory | `lpb cover photo.jpg --at 2.5` → `./photo_cover46.jpg` |
| Dual-file | Image's directory | `lpb cover photo.heic --at 2.5` → `./photo_cover46.HEIC` + `.MOV` |

---

### `repair` — Repair live photo metadata

Analyzes and fixes four kinds of metadata problems on existing live photo files: image rotation, embedded thumbnails, HEIC orientation, and video rotation. Images: `.jpg .jpeg .heic .heif`; videos: `.mov .mp4`.

| Mode | Arguments | Use case |
|------|-----------|----------|
| Single file | `<file>` (auto-detected by extension) | Fix one image or video |
| Batch folder | `<path>` (auto-detected: no extension) or `-d` | Every media file in a directory |

#### Examples

| Goal | Command |
|------|---------|
| Fix a single file | `lpb repair photo.jpg` |
| Batch fix a folder, auto-confirm | `lpb repair ./MyPhotos -y` (folder auto-detected; `-d` also works) |
| Preview without writing | `lpb repair -d ./MyPhotos --dry-run` |
| Only fix image rotation | `lpb repair -d ./Photos --no-thumbnail --no-heic --no-video -y` |
| Repair files from all devices | `lpb repair -d ./MyPhotos --all-devices -y` |
| Also copy intact files | `lpb repair -d ./MyPhotos --copy-perfect -y` |

> **Note:** Wildcards (`*.jpg`) are not supported. Pass a folder (`-d`) or list files explicitly.

---

#### Full Option Reference

**Input**

| Option | Description |
|--------|-------------|
| `<file>` | A single image or video to repair. Images: `.jpg .jpeg .heic .heif`; videos: `.mov .mp4`. A folder path without a file extension is auto-detected as batch mode |
| `-d, --dir <path>` | Directory to scan (batch mode). Every media file is analyzed; only files that need a fix are repaired. A path can also be passed as the positional argument |
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

> **Note:** all four fixes are on by default; pass `--no-heic` to disable HEIC orientation (matches the GUI default).

#### Default Output Location

Repair never overwrites the source files. When `-o` is omitted:

| Mode | Default output | Example |
|------|----------------|---------|
| Single file | `{name}_repaired{ext}` in the source file's directory | `IMG_001.jpg` → `IMG_001_repaired.jpg` |
| Batch (folder / `-d`) | `{input}/{input}_repaired/`, keeping source names | `lpb repair ./MyPhotos` → `./MyPhotos/MyPhotos_repaired/` |

#### Apple Live Photo Filter

Default: only **Apple Live Photos** (identified by the `ContentIdentifier` UUID in both still and paired video) are repaired; others are skipped. Pass `--all-devices` to repair every device.

#### Script Mode (JSON Output)

With `--json`, `repair` prints a single UTF-8 JSON document to stdout (no colors or prompts). `--json` implies `--yes` (skips confirmation).

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

JSON is UTF-8 — read stdout as UTF-8 in scripts (e.g. Python `json.loads(sys.stdin.buffer.read().decode("utf-8"))`).

---

### `--info` / `--version` — Show version and environment

`lpb --version` (`-v`) prints the version on one line; `lpb --info` prints install details (build date, runtime, platform, channel, location), the log directory and current log file, bundled tool versions, and repository/feedback links. Both run instantly with no network; output is colorized in interactive terminals, plain text when redirected or `NO_COLOR` is set.

Note: root-level `-v` means `--version`; inside subcommands (e.g. `lpb merge -v`) it keeps its subcommand meaning (`--verbose`).

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
