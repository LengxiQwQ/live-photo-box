#!/bin/bash
# ══════════════════════════════════════════════════════════════════════
# FFmpeg 编译脚本 — 严格遵循 docs/外部工具定制编译指南.md
# 新增 GIF 编码器/muxer/palettegen/paletteuse（仅 3 处改动标记 ↓NEW）
# 在 MSYS2 MINGW64 shell 中运行: bash 此脚本路径
# ══════════════════════════════════════════════════════════════════════
set -e

SRC_DIR=/c/Users/LengxiQwQ/Downloads/ffmpeg-src
OUT_DIR=/c/Users/LengxiQwQ/Downloads/ffmpeg-out
TOOLS_DIR="/d/Projects/live-photo-box/Live Photo Box/Tools"

# ── 步骤 1: 下载源码 ──
echo "=== [1/8] 克隆 FFmpeg n8.0.1 ==="
cd /c/Users/LengxiQwQ/Downloads
rm -rf ffmpeg-src 2>/dev/null
git clone --depth 1 --branch n8.0.1 https://github.com/FFmpeg/FFmpeg.git ffmpeg-src
# 如果 GitHub 不行，换官方源:
# git clone --depth 1 --branch n8.0.1 https://git.ffmpeg.org/ffmpeg.git ffmpeg-src
cd ffmpeg-src

# ── 步骤 2: configure（≡ 文档 2.2 节 + ↓NEW: gif, palettegen, paletteuse）──
echo "=== [2/8] Configure ==="
./configure \
    --prefix=$OUT_DIR \
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
    --enable-encoder=libx264,libx265,aac,mjpeg,gif \
    --enable-encoder=h264_amf,hevc_amf,h264_nvenc,hevc_nvenc,h264_qsv,hevc_qsv \
    --enable-decoder=h264,hevc,mjpeg,mpeg1video,mpeg2video,mpeg4 \
    --enable-decoder=aac,mp3,mp2,pcm_s16le,pcm_s24le,pcm_f32le,pcm_s16be \
    --enable-muxer=mp4,mov,mp3,image2,null,mjpeg,gif \
    --enable-demuxer=mov,mp4,mpegts,matroska,avi,image2,aac,mp3,wav,mjpeg \
    --enable-parser=h264,hevc,mpegaudio,aac,mjpeg,mpeg4video,mpegvideo \
    --enable-bsf=h264_mp4toannexb,hevc_mp4toannexb,aac_adtstoasc,extract_extradata,filter_units \
    --enable-filter=scale,transpose,hflip,vflip,crop,null,anull,format,setsar,fps,setpts,settb \
    --enable-filter=trim,atrim,aformat,aresample,pad,buffer,buffersink,palettegen,paletteuse \
    --enable-protocol=file,pipe \
    --enable-hwaccel=h264_d3d11va,h264_dxva2,hevc_d3d11va,hevc_dxva2 \
    --extra-cflags="-O2" \
    --extra-ldflags="-s -static -static-libgcc -static-libstdc++"

# ── 步骤 3: 修复动态引用（≡ 文档 2.2 节关键步骤）──
echo "=== [3/8] 移除 -lgcc_s ==="
sed -i 's/-lgcc_s//g' ffbuild/config.mak

# ── 步骤 4: 编译（≡ 文档 2.2 节）──
echo "=== [4/8] make -j20 ==="
make -j20

# ── 步骤 5: 验证（≡ 文档 2.3 节 + GIF 验证）──
echo "=== [5/8] 验证 ==="
echo "--- 零外部 DLL ---"
objdump -p ffmpeg.exe | grep "DLL Name"
echo "--- 编码器 ---"
./ffmpeg.exe -hide_banner -encoders 2>&1 | grep -E "libx264|libx265|amf|nvenc|qsv|mjpeg|gif"
echo "--- 滤镜 ---"
./ffmpeg.exe -hide_banner -filters 2>&1 | grep -E "scale|transpose|palette"
echo "--- GIF 封装器 ---"
./ffmpeg.exe -hide_banner -muxers 2>&1 | grep gif

# ── 步骤 6: UPX 压缩（≡ 文档 A 节）──
echo "=== [6/8] UPX 压缩 ==="
upx --lzma --best ffmpeg.exe
ls -lh ffmpeg.exe

# ── 步骤 7: 备份旧版 + 部署（≡ 文档 B 节）──
echo "=== [7/8] 备份 + 部署 ==="
BACKUP="${TOOLS_DIR}/ffmpeg-backup-$(date +%Y%m%d-%H%M%S).exe"
mkdir -p "${TOOLS_DIR}"
if [ -f "${TOOLS_DIR}/ffmpeg.exe" ]; then
    cp "${TOOLS_DIR}/ffmpeg.exe" "${BACKUP}"
    echo "备份: ${BACKUP}"
fi
cp ffmpeg.exe "${TOOLS_DIR}/ffmpeg.exe"
ls -lh "${TOOLS_DIR}/ffmpeg.exe"

# ── 步骤 8: 清除 MSBuild 缓存（≡ 文档 故障排查节）──
echo "=== [8/8] 清除 MSBuild 缓存 ==="
rm -rf "/d/Projects/live-photo-box/Live Photo Box/obj"
rm -rf "/d/Projects/live-photo-box/Live Photo Box/bin"

echo ""
echo "═══════════════════════════════════════"
echo "  完成! ffmpeg.exe 已部署到 Tools/"
echo "  编译产物大小: $(du -h ffmpeg.exe | cut -f1)"
echo "═══════════════════════════════════════"
