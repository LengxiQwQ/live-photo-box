"""
Apple Live Photo → Huawei Live Photo 转换脚本
==============================================
支持输出: JPEG 格式 (.jpg) 和 HEIC 格式 (.heic)

华为格式 = [静态图] + [MP4] + [60B尾部]

60B尾部布局:
  [0:6]   v6_f{封面帧号}      — 编辑器进度条读这个
  [6:20]  空格 (0x20)
  [20:28] PPP:QQQQ           — 封面帧号:总帧数
  [28:40] 空格
  [40:52] LIVE_{MP4+20}      — MP4字节数+20
  [52:60] 空格

用法:
  python convert_apple_to_huawei.py in.heic in.mov -o out.jpg
  python convert_apple_to_huawei.py in.heic in.mov -o out.heic
  python convert_apple_to_huawei.py in.heic in.mov -o out.jpg -f 40   # 封面帧40
  python convert_apple_to_huawei.py in.heic in.mov -o out.jpg --cover-from-video  # 从视频抽封面
"""

import subprocess, os, struct, argparse, shutil


def build_tail(cover_frame, total_frames, mp4_byte_size):
    """构建 60 字节华为尾部"""
    tail = bytearray(60)
    v6 = f"v6_f{cover_frame}".encode()
    tail[0:len(v6)] = v6
    for i in range(6, 20):   tail[i] = 0x20
    pq = f"{cover_frame}:{total_frames}".encode()
    for i in range(20, 28):  tail[i] = 0x20    # 先填满空格，避免残留 \x00
    tail[20:20 + len(pq)] = pq
    for i in range(28, 40):  tail[i] = 0x20
    live = f"LIVE_{mp4_byte_size + 20}".encode()
    tail[40:40 + len(live)] = live
    for i in range(40 + len(live), 60): tail[i] = 0x20
    return bytes(tail)


def convert_mov_to_mp4(src_mov, out_mp4, ffmpeg):
    """MOV → MP4: 复制视频流, AAC 音频, 不加 faststart (moov 必须在末尾)"""
    subprocess.run([ffmpeg, "-y", "-v", "error",
        "-i", src_mov,
        "-map", "0:v:0", "-map", "0:a:0",
        "-c:v", "copy",
        "-c:a", "aac", "-b:a", "128k",
        out_mp4], check=True)
    return open(out_mp4, "rb").read()


def get_frame_count(mp4_path):
    """获取视频总帧数"""
    r = subprocess.run(["ffprobe", "-v", "quiet",
        "-select_streams", "v:0",
        "-show_entries", "stream=nb_frames",
        "-of", "default=noprint_wrappers=1:nokey=1",
        mp4_path], capture_output=True, text=True)
    s = r.stdout.strip()
    return int(s) if s.isdigit() else 86


def extract_video_frame(mp4_path, frame_num, out_jpg, ffmpeg):
    """从视频中抽取指定帧为 JPEG"""
    fps = 30  # 近似
    t = frame_num / fps
    subprocess.run([ffmpeg, "-y", "-v", "error",
        "-ss", str(t), "-i", mp4_path,
        "-vframes", "1", out_jpg], check=True)
    return open(out_jpg, "rb").read()


def add_huawei_exif(path):
    """写入 HUAWEI EXIF 标记"""
    subprocess.run(["exiftool", "-overwrite_original",
        "-Make=HUAWEI", "-Model=Mate 80 Pro Max",
        path], check=True, capture_output=True)


def get_project_root():
    return os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def convert(heic_path, mov_path, output_path, cover_frame=None,
            cover_from_video=False):
    root = get_project_root()
    TOOLS = os.path.join(root, "Live Photo Box", "Tools")
    FFMPEG = os.path.join(TOOLS, "ffmpeg.exe")
    HEIF_DEC = os.path.join(TOOLS, "heif-dec.exe")
    HEIF_ENC = os.path.join(TOOLS, "heif-enc.exe")

    work = os.path.join(os.path.dirname(output_path) or ".", ".convert_tmp")
    os.makedirs(work, exist_ok=True)

    is_heic_output = output_path.lower().endswith(".heic")

    # 1. MOV → MP4
    mp4_path = os.path.join(work, "video.mp4")
    mp4 = convert_mov_to_mp4(mov_path, mp4_path, FFMPEG)

    # 2. 帧数
    total_frames = get_frame_count(mp4_path)
    if cover_frame is None:
        cover_frame = total_frames // 2  # 默认中间帧
    cover_frame = max(0, min(cover_frame, total_frames - 1))

    # 3. 封面图
    if cover_from_video:
        # 从视频抽帧当封面
        cover_jpg = os.path.join(work, "cover.jpg")
        cover_data = extract_video_frame(mp4_path, cover_frame, cover_jpg, FFMPEG)
    else:
        # 从 HEIC 提静态图当封面
        cover_jpg = os.path.join(work, "cover.jpg")
        subprocess.run([HEIF_DEC, "-o", cover_jpg, heic_path], check=True)
        cover_data = open(cover_jpg, "rb").read()

    if is_heic_output:
        # HEIC 输出: 把封面 JPEG 编码为 HEIC 容器
        heic_cover = os.path.join(work, "cover.heic")
        subprocess.run([HEIF_ENC, "-o", heic_cover, "-q", "90", cover_jpg],
                       check=True)
        still_data = bytearray(open(heic_cover, "rb").read())
        # 在 ftyp 末尾追加 tmap compatible brand
        ftyp_sz = struct.unpack(">I", still_data[0:4])[0]
        still_data[ftyp_sz - 4:ftyp_sz] = b"tmap"
        still_data = bytes(still_data)
    else:
        # JPEG 输出: 直接用 JPEG
        still_data = cover_data

    # 4. 构建尾部
    tail = build_tail(cover_frame, total_frames, len(mp4))

    # 5. 拼接: 静态图 + MP4 + 尾部
    with open(output_path, "wb") as f:
        f.write(still_data)
        f.write(mp4)
        f.write(tail)

    # 6. 加 EXIF (JPEG 输出时)
    if not is_heic_output:
        add_huawei_exif(output_path)

    # 清理
    for f in os.listdir(work):
        try: os.unlink(os.path.join(work, f))
        except: pass
    try: os.rmdir(work)
    except: pass

    final_size = os.path.getsize(output_path)
    print(f"[OK] {output_path}")
    print(f"     Size: {final_size:,} bytes")
    print(f"     Cover: frame {cover_frame}/{total_frames}")
    print(f"     LIVE_: {len(mp4) + 20} (MP4 {len(mp4)} + 20)")
    print(f"     Format: {'HEIC' if is_heic_output else 'JPEG'}")
    return output_path


if __name__ == "__main__":
    p = argparse.ArgumentParser(description="Apple Live Photo → Huawei 实况照片")
    p.add_argument("heic", help="Apple HEIC 文件路径")
    p.add_argument("mov",  help="Apple MOV 文件路径")
    p.add_argument("-o", "--output", default="output.jpg", help="输出路径 (.jpg 或 .heic)")
    p.add_argument("-f", "--frame", type=int, help="封面帧序号 (默认: 视频中间)")
    p.add_argument("--cover-from-video", action="store_true",
                   help="从视频抽帧当封面 (默认: 用 HEIC 静态图)")
    args = p.parse_args()

    convert(args.heic, args.mov, args.output, args.frame, args.cover_from_video)
