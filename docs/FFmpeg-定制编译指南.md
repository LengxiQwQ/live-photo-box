# FFmpeg 定制编译指南

> 将 ffmpeg.exe 从 gyan.dev full_build 的 **97 MB** 瘦身至 **5.8 MB**（缩减 94%），  
> 精确保留项目实际所需的全部功能，零冗余。

**最后编译**: 2026-07-03  
**FFmpeg 版本**: n8.0.1  
**编译环境**: MSYS2 + MinGW-w64 (GCC 16.1.0)

---

## 目录

1. [项目使用的 FFmpeg 功能清单](#1-项目使用的-ffmpeg-功能清单)
2. [编译环境搭建](#2-编译环境搭建)
3. [完整编译流程](#3-完整编译流程)
4. [UPX 压缩](#4-upx-压缩)
5. [部署到项目](#5-部署到项目)
6. [版本升级](#6-版本升级)
7. [故障排查](#7-故障排查)

---

## 1. 项目使用的 FFmpeg 功能清单

以下按 C# 服务文件逐一列出每个 ffmpeg 参数及其对应的编译组件。

### 1.1 ThumbnailService.cs — 视频缩略图提取

**文件**: `Live Photo Box/Services/ThumbnailService.cs`

```bash
# 实际命令（第 408-410 行，第 686-688 行）
ffmpeg [-hwaccel cuda|qsv|d3d11va|vaapi] -i "{video}" -vframes 1 \
  -vf "scale=80:-1:force_original_aspect_ratio=decrease" \
  -q:v 2 "{thumb.jpg}" -y -loglevel error
```

| 参数 | 功能 | 编译要求 |
|------|------|---------|
| `-hwaccel cuda` | NVIDIA GPU 硬件解码加速 | `cuda` hwaccel |
| `-hwaccel qsv` | Intel QSV 硬件解码加速 | `qsv` hwaccel |
| `-hwaccel d3d11va` | AMD GPU 硬件解码加速 | `d3d11va` hwaccel |
| `-hwaccel vaapi` | VAAPI 硬件解码加速 | `vaapi` hwaccel |
| `-vframes 1` | 抽取第 1 帧 | 无（内置） |
| `scale=...` | 缩放到 80px 宽，保持比例 | `--enable-filter=scale` |
| `-q:v 2` | JPEG 质量参数 | `--enable-encoder=mjpeg` |
| `.jpg` 输出 | 输出为 JPEG 图片 | `--enable-muxer=image2` |

### 1.2 VideoTranscodeService.cs — 视频转码 & Remux

**文件**: `Live Photo Box/Services/VideoTranscodeService.cs`

#### A. Remux 模式（流拷贝，第 185-186 行）

```bash
ffmpeg -y -i "{input}" -c copy -map 0:V:0 -map 0:a:0? \
  -map_metadata 0 [-movflags +faststart] "{output}"
```

| 参数 | 功能 | 编译要求 |
|------|------|---------|
| `-c copy` | 流拷贝（不重新编码） | 对应的 muxer/demuxer |
| `-map 0:V:0` | 选择第 1 个视频轨（大写 V = 排除封面/缩略图轨） | 无（内置 stream specifier） |
| `-map 0:a:0?` | 选择第 1 个音频轨（`?` = 没有也不报错） | 无（内置） |
| `-map_metadata 0` | 复制元数据（时间、GPS 等） | 无（内置） |
| `-movflags +faststart` | moov atom 前置，支持流式播放 | mp4/mov muxer 特性 |

#### B. 转码模式（第 957-977 行 BuildFFmpegArguments）

```bash
# MP4 输出
ffmpeg -apply_cropping 0 -y -i "{input}" \
  -map 0:V:0 -map 0:a:0? -map_metadata 0 \
  -threads {N} \
  -vf "{filter}" \
  -pix_fmt yuv420p \
  -c:v {encoder} {params} \
  {audio_args} \
  -movflags +faststart "{output}"

# MOV 输出（增加 -tag:v hvc1 用于 Apple 兼容）
ffmpeg -apply_cropping 0 -y -i "{input}" \
  -map 0:V:0 -map 0:a:0? -map_metadata 0 \
  -threads {N} \
  -vf "{filter}" \
  -c:v {encoder} {params} -tag:v hvc1 \
  -c:a copy \
  -movflags +faststart "{output}"
```

| 参数 | 功能 | 编译要求 |
|------|------|---------|
| `-apply_cropping 0` | 禁用 FFmpeg 自动裁切（手动用 crop 滤镜补偿） | h264/hevc 解码器选项 |
| `-threads {N}` | 编码线程数 | `--enable-pthreads` |
| `-vf setsar=1,...` | 设置像素宽高比 + 组合滤镜链 | `--enable-filter=setsar` |
| `-vf crop=...` | HEVC conformance window 裁剪 | `--enable-filter=crop` |
| `-pix_fmt yuv420p` | YUV 4:2:0 像素格式（H.264 输出强制） | 无（内置） |
| `-c:v {encoder}` | 视频编码器（见下表） | 见下表 |
| `-c:a copy` | 音频流拷贝 | 无 |
| `-c:a aac -b:a {N}k` | 音频重编码为 AAC | `--enable-encoder=aac` |
| `-tag:v hvc1` | MOV 输出 HEVC 的 Apple 兼容标签 | mov muxer 特性 |
| `-movflags +faststart` | 同上 | mp4/mov muxer 特性 |

**视频编码器列表**（项目支持的 8 种编码器）：

| 编码器 | 类型 | 编译要求 |
|--------|------|---------|
| `libx264` | CPU H.264 | `--enable-libx264` |
| `libx265` | CPU HEVC | `--enable-libx265` |
| `h264_amf` | AMD GPU H.264 | `--enable-amf` |
| `hevc_amf` | AMD GPU HEVC | `--enable-amf` |
| `h264_nvenc` | NVIDIA GPU H.264 | `--enable-nvenc` + ffnvcodec 头文件 |
| `hevc_nvenc` | NVIDIA GPU HEVC | 同上 |
| `h264_qsv` | Intel GPU H.264 | `--enable-libvpl` |
| `hevc_qsv` | Intel GPU HEVC | 同上 |

#### C. 编码器检测（第 91、650 行）

```bash
ffmpeg -hide_banner -encoders
ffmpeg -version
```

### 1.3 LivePhotoRepairService.cs — 视频修复

**文件**: `Live Photo Box/Services/LivePhotoRepairService.cs`

```bash
# 第 1152-1201 行 RunRepairFFmpegAsync
ffmpeg -apply_cropping 0 -y -i "{source}" \
  -map 0:v:0 -map 0:a:0? -map_metadata 0 \
  -threads {N} \
  -vf "{transform_filter},setsar=1" \
  -c:v {encoder} \
  -pix_fmt yuv420p \
  -fflags +genpts \
  -c:a aac -b:a 192k \
  -movflags +faststart \
  [-tag:v hvc1] "{output}"
```

| 参数 | 功能 | 编译要求 |
|------|------|---------|
| `transpose=1` | 顺时针旋转 90° | `--enable-filter=transpose` |
| `transpose=1,transpose=1` | 旋转 180° | 同上 |
| `transpose=2` | 逆时针旋转 90°（270°） | 同上 |
| `hflip` | 水平翻转 | `--enable-filter=hflip` |
| `vflip` | 垂直翻转 | `--enable-filter=vflip` |
| `-fflags +genpts` | 重新生成时间戳（修复损坏文件） | 无（内置 input flag） |
| `-c:a aac -b:a 192k` | 音频强制重编码为 AAC | `--enable-encoder=aac` |

#### 视频变换滤镜构造（第 977-1060 行 BuildVideoTransformFilter）

| 变换类型 | ffmpeg 滤镜 |
|---------|------------|
| FlipVertical | `vflip` |
| FlipHorizontal | `hflip` |
| Rotate90 | `transpose=1` |
| Rotate180 | `transpose=1,transpose=1` |
| Rotate270 | `transpose=2` |

### 1.4 完整组件汇总

#### 编码器 (9 个)

```
libx264  h264_amf  h264_nvenc  h264_qsv
libx265  hevc_amf  hevc_nvenc  hevc_qsv
mjpeg    aac
```

#### 解码器 (15 个)

```
h264  hevc  mjpeg  mpeg1video  mpeg2video  mpeg4
aac   mp3   mp2    pcm_s16le   pcm_s24le   pcm_f32le  pcm_s16be
```

#### 滤镜 (16 个)

```
scale  transpose  hflip  vflip  crop  null  anull
format  setsar  fps  setpts  settb  trim  atrim
aformat  aresample  pad  buffer  buffersink
```

#### 封装器

```
Muxer:   mp4  mov  mp3  image2  null  mjpeg
Demuxer: mov  mp4  mpegts  matroska  avi  image2  aac  mp3  wav  mjpeg
```

#### 硬件加速

```
cuda  vaapi  dxva2  qsv  d3d11va  d3d12va  amf
```

#### 比特流滤镜 & 协议

```
BSF:       h264_mp4toannexb  hevc_mp4toannexb  aac_adtstoasc  extract_extradata  filter_units
Protocol:  file  pipe
```

---

## 2. 编译环境搭建

### 2.1 安装 MSYS2

从 https://github.com/msys2/msys2-installer/releases 下载最新安装器，安装到 `C:\msys64`。

或使用命令行静默安装：

```powershell
# 下载安装器
curl -L -o msys2-installer.exe "https://github.com/msys2/msys2-installer/releases/download/<date>/msys2-x86_64-<date>.exe"
# 静默安装
.\msys2-installer.exe in --confirm-command --accept-messages --root C:/msys64
```

### 2.2 安装编译依赖

打开 **MINGW64** shell（`C:\msys64\mingw64.exe`），运行：

```bash
# 首次更新系统
pacman -Syu

# 安装编译工具链和所有依赖
pacman -S --needed --noconfirm \
    mingw-w64-x86_64-toolchain \
    mingw-w64-x86_64-nasm \
    mingw-w64-x86_64-libx264 \
    mingw-w64-x86_64-x264 \
    mingw-w64-x86_64-x265 \
    mingw-w64-x86_64-libvpl \
    mingw-w64-x86_64-amf-headers \
    mingw-w64-x86_64-ffnvcodec-headers \
    mingw-w64-x86_64-upx \
    git make diffutils pkgconf
```

**依赖说明**：

| 包名 | 用途 |
|------|------|
| `mingw-w64-x86_64-toolchain` | GCC + binutils + WinSDK 头文件 |
| `mingw-w64-x86_64-nasm` | x86 汇编器（加速编解码器） |
| `mingw-w64-x86_64-libx264` | H.264 编码库（含头文件和 .a 静态库） |
| `mingw-w64-x86_64-x265` | HEVC 编码库（含头文件和 .a 静态库） |
| `mingw-w64-x86_64-libvpl` | Intel oneVPL（QSV 硬件编码） |
| `mingw-w64-x86_64-amf-headers` | AMD AMF SDK 头文件 |
| `mingw-w64-x86_64-ffnvcodec-headers` | NVIDIA NVENC SDK 头文件 |
| `mingw-w64-x86_64-upx` | UPX 压缩工具 |
| `git` | 拉取 FFmpeg 源码 |

> **注意**: `libvpl` 是 C++ 库，链接时需要 `-lstdc++`，configure 中已通过 `--extra-libs="-lstdc++"` 处理。

---

## 3. 完整编译流程

以下命令在 **MINGW64** shell 中执行。

### 3.1 克隆 FFmpeg 源码

```bash
# 进入工作目录（可自定义）
cd /c/Users/LengxiQwQ/Downloads

# 浅克隆指定版本（--depth 1 仅拉取最新提交，节省时间和磁盘）
git clone --depth 1 --branch n8.0.1 https://git.ffmpeg.org/ffmpeg.git ffmpeg-src
cd ffmpeg-src
```

### 3.2 配置（Configure）

```bash
./configure \
    --prefix=/c/Users/LengxiQwQ/Downloads/ffmpeg-out \
    --target-os=mingw32 \
    --arch=x86_64 \
    --enable-gpl \
    --enable-version3 \
    --enable-static \
    --disable-shared \
    --disable-debug \
    --disable-doc \
    --enable-stripping \
    --enable-ffmpeg \
    --disable-ffplay \
    --disable-ffprobe \
    --disable-avdevice \
    --disable-network \
    --disable-everything \
    --enable-libx264 \
    --enable-libx265 \
    --enable-amf \
    --enable-libvpl \
    --enable-nvenc \
    --enable-d3d11va \
    --enable-dxva2 \
    --enable-pthreads \
    --disable-w32threads \
    --pkg-config-flags="--static" \
    --extra-libs="-lstdc++" \
    --enable-encoder=libx264,libx265,aac,mjpeg \
    --enable-encoder=h264_amf,hevc_amf,h264_nvenc,hevc_nvenc,h264_qsv,hevc_qsv \
    --enable-decoder=h264,hevc,mjpeg,mpeg1video,mpeg2video,mpeg4 \
    --enable-decoder=aac,mp3,mp2,pcm_s16le,pcm_s24le,pcm_f32le,pcm_s16be \
    --enable-muxer=mp4,mov,mp3,image2,null,mjpeg \
    --enable-demuxer=mov,mp4,mpegts,matroska,avi,image2,aac,mp3,wav,mjpeg \
    --enable-parser=h264,hevc,mpegaudio,aac,mjpeg,mpeg4video,mpegvideo \
    --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,aac_adtstoasc,extract_extradata,filter_units \
    --enable-filter=scale,transpose,hflip,vflip,crop,null,anull,format,setsar,fps,setpts,settb \
    --enable-filter=trim,atrim,aformat,aresample,pad,buffer,buffersink \
    --enable-protocol=file,pipe \
    --enable-hwaccel=h264_d3d11va,h264_dxva2,hevc_d3d11va,hevc_dxva2 \
    --extra-cflags="-O2" \
    --extra-ldflags="-s -static"
```

**Configure 参数说明**：

| 参数 | 说明 |
|------|------|
| `--disable-everything` | **核心策略**：先关闭一切，再精确启用所需组件 |
| `--enable-static --disable-shared` | 静态链接，产出单文件 exe，无需附带 DLL |
| `--enable-gpl --enable-version3` | GPL 许可证（x264/x265 要求） |
| `--disable-avdevice` | 禁用设备捕获（无 lavfi 测试源等） |
| `--disable-network` | 禁用网络协议（桌面工具只用本地文件） |
| `--enable-stripping` | 编译时去除调试符号 |
| `--extra-cflags="-O2"` | GCC -O2 优化 |
| `--extra-ldflags="-s -static"` | 链接时 strip + 静态链接 |
| `--pkg-config-flags="--static"` | pkg-config 返回静态库的链接参数 |
| `--extra-libs="-lstdc++"` | 链接 C++ 标准库（libvpl 是 C++ 库） |

### 3.3 编译

```bash
# 并行编译（-j20 按 CPU 核心数调整）
make -j20

# 验证编码器
./ffmpeg.exe -hide_banner -encoders 2>&1 | grep "^ V"
```

### 3.4 验证

```bash
# 验证全部编码器
./ffmpeg.exe -hide_banner -encoders 2>&1 | grep -E "libx264|libx265|amf|nvenc|qsv|mjpeg"

# 验证全部滤镜
./ffmpeg.exe -hide_banner -filters 2>&1 | grep -E "scale|transpose|hflip|vflip|crop|setsar"

# 验证硬件加速
./ffmpeg.exe -hide_banner -hwaccels 2>&1

# 预期输出：
# V....D libx264    V....D h264_amf    V....D h264_nvenc    V..... h264_qsv
# V....D libx265    V....D hevc_amf    V....D hevc_nvenc    V..... hevc_qsv
# VFS..D mjpeg
```

---

## 4. UPX 压缩

编译产物约 36 MB，UPX 可压至 **~5.8 MB**（压缩率 ~84%）。

```bash
# 安装 UPX（如果还没装）
pacman -S --needed mingw-w64-x86_64-upx

# 极限压缩
upx --lzma --best ffmpeg.exe
```

**压缩前后对比**：

| 阶段 | 大小 | 说明 |
|------|------|------|
| 原始 gyan.dev full_build | 97 MB | 全量编译（上千个编解码器/滤镜/协议） |
| 定制编译（未压缩） | ~36 MB | `--disable-everything` + 精确启用 |
| 定制编译 + UPX | **5.8 MB** | UPX LZMA 极限压缩 |

> **关于 UPX 的注意事项**：
> - 启动时约 0.1 秒解压延迟（对用户无感知）
> - 部分杀毒软件可能误报 UPX 压缩的可执行文件，如有需要可在 Windows Defender 中添加排除项
> - 如需发布未压缩版本，跳过 UPX 步骤即可，36 MB 仍比原版小 63%

---

## 5. 部署到项目

```bash
# 备份原版
cp "/d/Projects/live-photo-box/Live Photo Box/Tools/ffmpeg.exe" \
   "/d/Projects/live-photo-box/Live Photo Box/Tools/ffmpeg-full-backup.exe"

# 替换为瘦身版
cp ffmpeg.exe "/d/Projects/live-photo-box/Live Photo Box/Tools/ffmpeg.exe"
```

**项目中的文件位置**:
- `Live Photo Box/Tools/ffmpeg.exe` — ffmpeg 主程序（5.8 MB）
- `Live Photo Box/Tools/` — 其他外部工具（exiftool, jpegtran 等）
- `Live Photo Box/Services/ExternalToolLocator.cs` — 搜索 `Tools/ffmpeg.exe`
- `.gitignore` 第 369 行忽略了 `Tools/` 目录（二进制不提交到 git）

---

## 6. 版本升级

当需要升级到新版本 FFmpeg 时：

### 6.1 更新 MSYS2 依赖

```bash
pacman -Syu
```

### 6.2 拉取新版本源码

```bash
cd /c/Users/LengxiQwQ/Downloads/ffmpeg-src
git fetch --tags
# 查看可用版本
git tag -l 'n*' | tail -20
# 切换到新版本
git checkout nX.Y.Z
```

### 6.3 重新编译

```bash
make clean    # 或 make distclean（完全清理）
# 重新运行 ./configure ...（参数同上）
make -j20
upx --lzma --best ffmpeg.exe
```

### 6.4 验证兼容性

编译后务必重新执行 [第 3.4 节验证步骤](#34-验证)，确保新版本的编码器名、滤镜名没有变化。

> **已知风险**：FFmpeg 大版本升级可能改变编码器名或移除某些选项。升级后应在项目中对各功能做冒烟测试（转码、缩略图、修复旋转）。

### 6.5 更新项目中的二进制

```bash
cp ffmpeg.exe "/d/Projects/live-photo-box/Live Photo Box/Tools/ffmpeg.exe"
```

---

## 7. 故障排查

### libvpl 检测失败

```
ERROR: libvpl >= 2.6 not found
```

原因：libvpl 是 C++ 库，configure 测试链接时缺少 C++ 标准库。

解决：添加 `--extra-libs="-lstdc++"` 到 configure 命令。

### x264 检测失败

```
ERROR: x264 not found using pkg-config
```

原因：配置静态链接时 pkg-config 默认返回动态库参数。

解决：添加 `--pkg-config-flags="--static"`。

### 某个功能不可用

如果项目中某个功能（转码、缩略图等）突然失败，检查：

1. `ffmpeg.exe -encoders` — 目标编码器是否存在
2. `ffmpeg.exe -filters` — 目标滤镜是否存在
3. `ffmpeg.exe -hwaccels` — 目标硬件加速是否存在
4. 对比本文档第 1 节的功能清单，确认对应组件已启用

### 编译产物过大

- 检查是否不小心链接了多余的库（configure 输出中的 `External libraries` 列表）
- 确保 `--disable-everything` 后没有自动连带的多余组件
- UPX 压缩是最后的瘦身手段，90%+ 的情况都能压到 6 MB 以内

---

## 附录：与对比项目的差异

对比项目 [LivePhotoTools](https://github.com/YuleBest/LivePhotoTools) 使用标准 ARM64 Linux 静态 ffmpeg（51 MB），**未做任何瘦身**。

| | gyan.dev full_build | LivePhotoTools | **本项目（定制编译）** |
|---|---|---|---|
| 平台 | Windows x64 | Android ARM64 | Windows x64 |
| 大小 | 97 MB | 51 MB | **5.8 MB** |
| 编码器数 | 100+ | 全部内置 | **9 个（精确匹配需求）** |
| 音量 | 全功能（Whisper、AV1、游戏音频……） | 标准编译 | **仅 Live Photo 相关功能** |
| 压缩 | 无 | 无 | **UPX LZMA 极限压缩** |

---

> **维护提示**：每次修改项目中 ffmpeg 的调用参数时，请同步更新本文档第 1 节的功能清单。
