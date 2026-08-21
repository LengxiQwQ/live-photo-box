# -*- coding: utf-8 -*-
"""
Apple HEIC+MOV → 华为实况（JPEG + HEIC），全部参数从源文件自动检测。
"""
import subprocess, os, struct, re, sys, json as _json
sys.stdout.reconfigure(encoding='utf-8', errors='replace')

TOOLS = r"C:\Program Files\Live Photo Box\Tools"
FFMPEG   = os.path.join(TOOLS, "ffmpeg.exe")
HEIF_DEC = os.path.join(TOOLS, "heif-dec.exe")
HEIF_ENC = os.path.join(TOOLS, "heif-enc.exe")
EXIFTOOL = os.path.join(TOOLS, "exiftool.exe")
FFPROBE  = "ffprobe"

# ═══ 用法: python convert_h264_corrected.py <HEIC> <MOV> ═══
if len(sys.argv) >= 3:
    SRC_HEIC = sys.argv[1]
    SRC_MOV  = sys.argv[2]
else:
    SRC_HEIC = r"C:\Users\LengxiQwQ\Desktop\苹果.HEIC"
    SRC_MOV  = r"C:\Users\LengxiQwQ\Desktop\苹果.MOV"
    print(f"(使用默认源，可传参: python {os.path.basename(__file__)} <HEIC路径> <MOV路径>)")

BASE     = r"C:\Users\LengxiQwQ\Desktop"
WORK     = r"D:\Projects\live-photo-box\ai-tmp\huawei_final"
os.makedirs(WORK, exist_ok=True)
OUT_STEM = os.path.splitext(os.path.basename(SRC_HEIC))[0]

def _i(s, d=0):
    try: return int(s)
    except: return d
def _f(s, d=0.0):
    try: return float(s)
    except: return d

def run(cmd, desc=""):
    print(f"  [{desc}] ...")
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0: print(f"  WARN: {r.stderr[:300]}")
    return r

def probe_video(path):
    info = {"codec": "?", "w": 0, "h": 0, "fps": 30.0, "dur": 0.0, "n": 0, "rot": 0}
    # 基本流信息
    r = run([FFPROBE, "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=codec_name,width,height,r_frame_rate,duration,nb_frames",
        "-of", "csv=p=0", path], "")
    parts = r.stdout.strip().split(",")
    if len(parts) >= 6:
        info["codec"] = parts[0]; info["w"] = _i(parts[1]); info["h"] = _i(parts[2])
        fs = parts[3]; info["dur"] = _f(parts[4]); info["n"] = _i(parts[5])
        if "/" in str(fs):
            n0, d0 = fs.split("/"); info["fps"] = _f(n0) / _f(d0) if _f(d0) != 0 else 30.0
        else:
            info["fps"] = _f(fs) or 30.0
    if info["fps"] <= 0: info["fps"] = 30.0
    # rotation: 先试 stream_tags，再试 side_data json
    r2 = run([FFPROBE, "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream_tags=rotate", "-of", "csv=p=0", path], "")
    rt = r2.stdout.strip()
    if rt and rt not in ("", "0", "N/A"): info["rot"] = _i(rt)
    if info["rot"] == 0:
        r3 = run([FFPROBE, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream_side_data=rotation", "-of", "json", path], "")
        try:
            sd = _json.loads(r3.stdout)
            for sl in sd.get("streams", []):
                for se in sl.get("side_data_list", []):
                    tr = _i(se.get("rotation", 0))
                    if tr != 0: info["rot"] = tr
        except: pass
    # 兜底
    if info["dur"] <= 0:
        r4 = run([FFPROBE, "-v", "error", "-show_entries", "format=duration",
            "-of", "csv=p=0", path], ""); info["dur"] = _f(r4.stdout.strip())
    if info["n"] <= 0 and info["dur"] > 0:
        info["n"] = int(round(info["dur"] * info["fps"]))
    return info

# ═══ 探测 ═══
print("=== 源文件探测 ===")
vsrc = probe_video(SRC_MOV)
rot = vsrc["rot"]
if abs(rot) in (90, 270):
    disp_w, disp_h = vsrc["h"], vsrc["w"]
else:
    disp_w, disp_h = vsrc["w"], vsrc["h"]
print(f"  MOV: {vsrc['codec']} 物理{vsrc['w']}×{vsrc['h']} 旋转{rot}° → 显示{disp_w}×{disp_h}  "
      f"{vsrc['fps']:.1f}fps {vsrc['n']}帧 {vsrc['dur']:.3f}s")

# HEIC
raw = os.path.join(WORK, "cover_raw.jpg")
run([HEIF_DEC, "-o", raw, SRC_HEIC], "HEIC解码")
rwh = run([FFPROBE, "-v", "error", "-select_streams", "v:0",
    "-show_entries", "stream=width,height", "-of", "csv=p=0", raw], "").stdout.strip().split(",")
hw, hh = int(rwh[0]), int(rwh[1])
orient = "横屏" if hw >= hh else "竖屏"
print(f"  HEIC: {hw}×{hh} ({orient})")

# ═══ Step 1: MOV → H.264 MP4（有旋转则物理转置）═══
print("\n=== Step 1: H.264 编码 ===")
if abs(rot) in (90, 270):
    t = "transpose=1" if rot == 90 else "transpose=2"
    enc_w, enc_h = disp_w, disp_h
    vf = f"{t},scale={enc_w}:{enc_h}:flags=lanczos,setsar=1,format=yuv420p"
    tag = f"{enc_w}×{enc_h} (转置{rot}°)"
else:
    enc_w, enc_h = vsrc["w"], vsrc["h"]
    vf = f"scale={enc_w}:{enc_h}:flags=lanczos,setsar=1,format=yuv420p"
    tag = f"{enc_w}×{enc_h}"

mp4_path = os.path.join(WORK, "video_h264.mp4")
ff_args = [FFMPEG, "-y", "-v", "error", "-apply_cropping", "0", "-i", SRC_MOV,
    "-map", "0:V:0", "-map", "0:a:0",
    "-c:v", "libx264", "-profile:v", "high", "-level:v", "5.0", "-pix_fmt", "yuv420p",
    "-vf", vf, "-x264-params", "bframes=0:ref=1",
    "-c:a", "aac", "-b:a", "128k", "-ar", "44100", "-ac", "2",
    "-brand", "mp42", "-movflags", "+faststart", mp4_path]
run(ff_args, tag)
mp4 = open(mp4_path, "rb").read(); mp4_sz = len(mp4)
tf, fps = vsrc["n"], vsrc["fps"]
print(f"  MP4: {mp4_sz:,}B, {enc_w}×{enc_h}, {tf}帧, {fps:.1f}fps")

# ═══ Step 2: 封面帧 ═══
cf = int(tf * 0.75)
print(f"\n=== Step 2: 封面帧 {cf}/{tf} (75%) ===")

# ═══ Step 3: 封面缩放 ═══
# 封面等比例缩放到：长边 = 视频长边
vmax = max(enc_w, enc_h)
ratio = hw / hh
if hw >= hh:
    cw, ch = vmax, int(round(vmax / ratio))
else:
    cw, ch = int(round(vmax * ratio)), vmax
cw += cw % 2; ch += ch % 2
cover_path = os.path.join(WORK, "cover_final.jpg")
run([FFMPEG, "-y", "-v", "error", "-i", raw,
    "-vf", f"scale={cw}:{ch}:flags=lanczos", "-q:v", "2", cover_path],
    f"{hw}×{hh} → {cw}×{ch}")
cover = open(cover_path, "rb").read()
print(f"  封面: {cw}×{ch}, {len(cover):,}B")

# ═══ Step 4: 尾串 ═══
v6 = f"v6_f{cf:<2}".encode(); assert len(v6) == 6
pq = f"{cf}:{tf}".encode()
live = f"LIVE_{mp4_sz + 20}".encode()
tail = bytearray(60)
tail[0:6] = v6
for i in range(6, 20): tail[i] = 0x20
tail[20:20+len(pq)] = pq
for i in range(20+len(pq), 40): tail[i] = 0x20
tail[40:40+len(live)] = live
for i in range(40+len(live), 60): tail[i] = 0x20
print(f"\n=== Step 4: 尾串 [{bytes(tail).decode('ascii')}] ===")

# ═══ Step 5: JPEG ═══
jpeg_out = os.path.join(BASE, f"{OUT_STEM}_华为_H264.jpg")
with open(jpeg_out, "wb") as f:
    f.write(cover); f.write(mp4); f.write(bytes(tail))
run([EXIFTOOL, "-overwrite_original",
    "-Make=HUAWEI", "-Model=Mate 80 Pro Max", jpeg_out], "JPEG加EXIF")
print(f"=== Step 5: JPEG → {os.path.getsize(jpeg_out):,}B ===")

# ═══ Step 6: HEIC ═══
heic_still = os.path.join(WORK, "still.heic")
r = run([HEIF_ENC, "-o", heic_still, "-q", "90", cover_path], "heif-enc HEIC封面")
if r.returncode == 0 and os.path.exists(heic_still) and os.path.getsize(heic_still) > 100:
    sd = bytearray(open(heic_still, "rb").read())
    fsz = struct.unpack(">I", sd[0:4])[0]; sd[fsz-4:fsz] = b"tmap"; sd = bytes(sd)
else:
    print("  heif-enc 失败，用源 HEIC 做容器"); sd = open(SRC_HEIC, "rb").read()
heic_out = os.path.join(BASE, f"{OUT_STEM}_华为_H264.heic")
with open(heic_out, "wb") as f:
    f.write(sd); f.write(mp4); f.write(bytes(tail))
print(f"=== Step 6: HEIC → {os.path.getsize(heic_out):,}B ===")

# ═══ Step 7: 验证 ═══
print("\n" + "=" * 64)
print("验证")
print("=" * 64)
print(f"  源: MOV {vsrc['codec']} {vsrc['w']}×{vsrc['h']} 旋转{rot}° → 显示{disp_w}×{disp_h}  |  HEIC {hw}×{hh}({orient})")
print(f"  输出: 视频H.264 {enc_w}×{enc_h} {tf}帧  |  封面{cw}×{ch}  |  封面帧{cf}/{tf}")
for label, path in [("JPEG", jpeg_out), ("HEIC", heic_out)]:
    d = open(path, "rb").read()
    tb = d[-60:]
    vf_ = re.search(rb'v6_f(\d+)', tb); lv = re.search(rb'LIVE_(\d+)', tb)
    lv_val = int(lv.group(1)) if lv else -1
    mp4_start = d.rfind(b'ftyp') - 4; real_mp4 = len(d) - 60 - mp4_start
    ok = "OK" if lv_val == real_mp4 + 20 else f"FAIL({real_mp4+20})"
    tc = os.path.join(WORK, f"_vc_{label[0]}.jpg")
    tv = os.path.join(WORK, f"_vv_{label[0]}.mp4")
    open(tc, "wb").write(d[:mp4_start]); open(tv, "wb").write(d[mp4_start:len(d)-60])
    cv = probe_video(tc); vv = probe_video(tv)
    print(f"  {label}: 封面{cv['w']}×{cv['h']}  视频{vv['w']}×{vv['h']} {vv['codec']} {vv['n']}帧  fYY=f{vf_.group(1).decode() if vf_ else '?'} LIVE_={lv_val} {ok}")
print(f"\n输出: {BASE}")
