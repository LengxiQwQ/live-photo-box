# CLI Automated Test Report

## lpb --version

**Exit Code**: 0

### Stdout
`
Live Photo Box CLI v2.2.1
`

## lpb --info

**Exit Code**: 0

### Stdout
`
Live Photo Box CLI v2.2.1 — environment

Build date : 2026-08-28
Runtime    : .NET 9.0.19 (X64)
Platform   : Microsoft Windows 10.0.26200 (X64)
Channel    : Portable (CLI-only)
Location   : D:\Projects\live-photo-box\LivePhotoBox.CLI\bin\Debug\net9.0-windows10.0.19041.0
Log dir    : C:\Users\LengxiQwQ\AppData\Local\LivePhotoBox\Logs\CLI
Log file   : cli-20260828-181554773-35188.log

External tools:
exiftool  13.59   D:\Projects\live-photo-box\LivePhotoBox\Tools\exiftool.exe
ffmpeg    n8.0.1  D:\Projects\live-photo-box\LivePhotoBox\Tools\ffmpeg.exe
jpegtran  n/a     D:\Projects\live-photo-box\LivePhotoBox\Tools\jpegtran.exe
heif-dec  1.23.1  D:\Projects\live-photo-box\LivePhotoBox\Tools\heif-dec.exe
heif-enc  1.23.1  D:\Projects\live-photo-box\LivePhotoBox\Tools\heif-enc.exe

Repository : https://github.com/lengxiqwq/live-photo-box
Feedback   : https://github.com/lengxiqwq/live-photo-box/issues

© 2026 LengxiQwQ · Licensed under GPL-3.0
`

## lpb protocols

**Exit Code**: 0

### Stdout
`
Merge — protocol × format compatibility

Protocol                 JPEG + MP4  JPEG + MOV  HEIC + MP4  HEIC + MOV  HEIC + MP4 (H.265)  
──────────────────────── ──────────  ──────────  ──────────  ──────────  ──────────────────  
Google Micro Video (v1)    ✅            ✅            ✖️            ✖️            ✖️                  
Google Motion Photo (v2)   ✅            ✅            ✖️            ✅            ✖️                  
OPPO O-Live Photo          ✅            ✖️            ✖️            ✖️            ✖️                  
vivo Live Photo            ✅            ✖️            ✖️            ✖️            ✖️                  
Samsung Motion Photo       ✅            ✖️            ✅            ✖️            ✖️                  
HUAWEI Moving Photo        ✅            ✖️            ✅            ✖️            ✅                  

✅ = supported   ✖️ = not supported

Protocol indices: micro video=1  motion photo=2  oppo=3  vivo=4  samsung=5  huawei=6
Format indices:   jpg+mp4=0  jpg+mov=1  heic+mp4=2  heic+mov=3  heic+mp4-h265=4


Merge — devices & availability

Protocol                   Devices                                  Status
────────────────────────   ──────────────────────────────────────   ──────────
Google Micro Video (v1)    Windows / Xiaomi (legacy MIUI) / Pixel   ✅ Supported
Google Motion Photo (v2)   Windows / Xiaomi / Pixel                 ✅ Supported
OPPO O-Live Photo          Windows / Xiaomi / OPPO                  ✅ Supported
vivo Live Photo            Windows / vivo (≥ X300)                  🟡 In testing
Samsung Motion Photo       Windows / Samsung                        🟡 In testing
HUAWEI Moving Photo        HUAWEI / Honor                           ✅ Supported

Split — devices & availability

Protocol            Devices         Status
─────────────────   ─────────────   ──────────
None (split only)   Any device      ✅ Supported
Apple Live Photo    iPhone / iPad   ✅ Supported
vivo Live Photo     vivo (≤ X200)   🟡 In testing

Split — protocol × format compatibility

Protocol          keep          JPG + MOV     HEIC + MOV    JPG + MP4     
───────────────── ───────────   ───────────   ───────────   ───────────   
None (split only)   ✅              ✅              ✅              ✅            
Apple Live Photo    ✖️              ✅              ✅              ✖️            
vivo Live Photo     ✖️              ✖️              ✖️              ✅            
Split protocol indices: none=0  apple=1  vivo=2
Split format indices:   keep=0  jpg+mov=1  heic+mov=2  jpg+mp4=3

Repair — metadata fixes (no protocol needed)

Fixes rotation, embedded thumbnails, HEIC orientation, and video rotation.
Apple Live Photos only (identified by ContentIdentifier UUID).
`

## lpb protocols --json

**Exit Code**: 0

### Stdout
`
{
  "protocols": [
    {
      "index": 1,
      "name": "MicroVideo",
      "displayName": "Google Micro Video (v1)",
      "devices": "Windows / Xiaomi (legacy MIUI) / Pixel",
      "status": "Supported",
      "formats": [
        "JPEG \u002B MP4",
        "JPEG \u002B MOV"
      ]
    },
    {
      "index": 2,
      "name": "MotionPhoto",
      "displayName": "Google Motion Photo (v2)",
      "devices": "Windows / Xiaomi / Pixel",
      "status": "Supported",
      "formats": [
        "JPEG \u002B MP4",
        "JPEG \u002B MOV",
        "HEIC \u002B MOV"
      ]
    },
    {
      "index": 3,
      "name": "OPPO_OLive",
      "displayName": "OPPO O-Live Photo",
      "devices": "Windows / Xiaomi / OPPO",
      "status": "Supported",
      "formats": [
        "JPEG \u002B MP4"
      ]
    },
    {
      "index": 4,
      "name": "vivo_LivePhoto",
      "displayName": "vivo Live Photo",
      "devices": "Windows / vivo (\u2265 X300)",
      "status": "In testing",
      "formats": [
        "JPEG \u002B MP4"
      ]
    },
    {
      "index": 5,
      "name": "Samsung_MotionPhoto",
      "displayName": "Samsung Motion Photo",
      "devices": "Windows / Samsung",
      "status": "In testing",
      "formats": [
        "JPEG \u002B MP4",
        "HEIC \u002B MP4"
      ]
    },
    {
      "index": 6,
      "name": "HUAWEI_MovingPhoto",
      "displayName": "HUAWEI Moving Photo",
      "devices": "HUAWEI / Honor",
      "status": "Supported",
      "formats": [
        "JPEG \u002B MP4",
        "HEIC \u002B MP4",
        "HEIC \u002B MP4 (H.265)"
      ]
    }
  ],
  "split": [
    {
      "index": 0,
      "name": "None (split only)",
      "devices": "Any device",
      "status": "Supported",
      "formats": [
        "keep",
        "jpg\u002Bmov",
        "heic\u002Bmov",
        "jpg\u002Bmp4"
      ]
    },
    {
      "index": 1,
      "name": "Apple Live Photo",
      "devices": "iPhone / iPad",
      "status": "Supported",
      "formats": [
        "jpg\u002Bmov",
        "heic\u002Bmov"
      ]
    },
    {
      "index": 2,
      "name": "vivo Live Photo",
      "devices": "vivo (\u2264 X200)",
      "status": "In testing",
      "formats": [
        "jpg\u002Bmp4"
      ]
    }
  ]
}
`

## lpb merge D:\Projects\live-photo-box\designs\各个机型测试\苹果-双文件.JPG D:\Projects\live-photo-box\designs\各个机型测试\苹果-双文件.MOV -p huawei -y --dry-run

**Exit Code**: 0

### Stdout
`
Image     : 苹果-双文件.JPG
Video     : 苹果-双文件.MOV
Protocol  : HUAWEI Moving Photo (HUAWEI / Honor)
Format    : JPEG + MP4
Output    : D:\Projects\live-photo-box\designs\各个机型测试
Key photo : auto (from source video)
File      : 苹果-双文件huawei.jpg
[DRY RUN] Would merge 1 pair.
`

## lpb merge -d D:\Projects\live-photo-box\designs\各个机型测试 -p motionphoto -y --dry-run

**Exit Code**: 0

### Stdout
`
Protocol  : Google Motion Photo (v2) (Windows / Xiaomi / Pixel)
Format    : JPEG + MP4
Pairing   : name
Output    : D:\Projects\live-photo-box\designs\各个机型测试\各个机型测试_motionphoto
Scanning  : D:\Projects\live-photo-box\designs\各个机型测试 ... 
3 filename pairs, 0 meta pairs, 10 standalone images, 0 standalone videos

[DRY RUN] Would merge 3 pairs:
#1  vivo双文件.jpg  +  vivo双文件.mp4
#2  苹果-双文件.JPG  +  苹果-双文件.MOV
#3  苹果双文件.HEIC  +  苹果双文件.MOV
`

## lpb split D:\Projects\live-photo-box\designs\各个机型测试\华为-Mate80.jpg -y --dry-run

**Exit Code**: 0

### Stdout
`
Filename  : 华为-Mate80.jpg
Protocol  : None (split only)
Format    : keep original
Output    : D:\Projects\live-photo-box\designs\各个机型测试
[DRY RUN] Would split 1 file.
`

## lpb split -d D:\Projects\live-photo-box\designs\各个机型测试 -y --dry-run

**Exit Code**: 0

### Stdout
`
Protocol  : None (split only)
Format    : keep original
Output    : D:\Projects\live-photo-box\designs\各个机型测试\各个机型测试_split
Scanning  : D:\Projects\live-photo-box\designs\各个机型测试 ... 
10 single-file live photos found

[DRY RUN] Would split 10 files:
#1  oppo.jpg
#2  vivo.jpg
#3  一加-改了封面照片.jpg
#4  一加.jpg
#5  三星.heic
#6  三星.jpg
#7  华为-Mate80.jpg
#8  华为Mate80.heic
#9  小米.jpg
#10  红米老款-GV1.JPG
`

## lpb repair D:\Projects\live-photo-box\designs\各个机型测试\华为-Mate80.jpg --dry-run

**Exit Code**: 0

### Stdout
`
Filename  : 华为-Mate80.jpg
Fixes     : rotation, thumbnail, heic-orientation, video-rotation
Devices   : Apple only
Output    : D:\Projects\live-photo-box\designs\各个机型测试
File      : 华为-Mate80_repaired.jpg
Skipped: non-Apple device
`

## lpb repair -d D:\Projects\live-photo-box\designs\各个机型测试 -y --dry-run

**Exit Code**: 0

### Stdout
`
Fixes     : rotation, thumbnail, heic-orientation, video-rotation
Devices   : Apple only
Output    : D:\Projects\live-photo-box\designs\各个机型测试\各个机型测试_repaired
Scanning  : D:\Projects\live-photo-box\designs\各个机型测试 ... 
17 media files found
Apple detection: 4 / 17 Apple files (non-Apple files will be skipped)
Need repair: 0  Skipped: 17  Errors: 0
Nothing to do.
`

## lpb cover D:\Projects\live-photo-box\designs\各个机型测试\华为-Mate80.jpg --at 2.5 --dry-run

**Exit Code**: 0

### Stdout
`
D:\Projects\live-photo-box\LivePhotoBox.CLI\Commands\CoverCommand.cs(87,36): warning CS8604: “string? Enumerable.FirstOrDefault<string>(IEnumerable<string> source, Func<string, bool> predicate)”中的形参“source”可能传入 null 引用实参。 [D:\Projects\live-photo-box\LivePhotoBox.CLI\LivePhotoBox.CLI.csproj]
Photo           : 华为-Mate80.jpg
Protocol        : HUAWEI Moving Photo (Single-file)
Size            : 8.9 MB
Duration        : 2.9s / 86 frames
Current cover   : frame 23 (0.733s)
New cover       : frame 76 (2.500s)
[DRY RUN] Would change cover. No files were modified.
`

## lpb cover D:\Projects\live-photo-box\designs\各个机型测试\华为-Mate80.jpg --frame 10 --dry-run

**Exit Code**: 0

### Stdout
`
Photo           : 华为-Mate80.jpg
Protocol        : HUAWEI Moving Photo (Single-file)
Size            : 8.9 MB
Duration        : 2.9s / 86 frames
Current cover   : frame 23 (0.733s)
New cover       : frame 10 (0.300s)
[DRY RUN] Would change cover. No files were modified.
`

## lpb update-check

**Exit Code**: 0

### Stdout
`
Checking GitHub ... OK
You are running the latest version.
`

