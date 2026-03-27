import os
import json
import subprocess
from pathlib import Path

# --- 终端颜色 ---
C_CYAN = '\033[96m'
C_YELLOW = '\033[93m'
C_GREEN = '\033[92m'
C_RED = '\033[91m'
C_WHITE = '\033[97m'
C_MAGENTA = '\033[95m'
C_RESET = '\033[0m'

EXIFTOOL_PATH = 'exiftool'
FFPROBE_PATH = 'ffprobe'

def get_img_info(path, ext):
    """提取图像全量元数据 (修复大小写判定Bug)"""
    cmd = [
        EXIFTOOL_PATH, '-j', 
        '-Model', '-Software',
        '-File:ImageWidth', '-File:ImageHeight', 
        '-EXIF:ExifImageWidth', '-EXIF:ExifImageHeight',
        '-Orientation', '-ThumbnailImage', 
        str(path)
    ]
    try:
        res = subprocess.run(cmd, capture_output=True, text=True, errors='ignore')
        data = json.loads(res.stdout)[0]
        
        model = str(data.get('Model', '未知设备'))
        sw = str(data.get('Software', '未知系统'))
        
        # 物理像素 (File)
        fw = data.get('ImageWidth', 'N/A')
        fh = data.get('ImageHeight', 'N/A')
        # 声明像素 (EXIF)
        ew = data.get('ExifImageWidth', 'N/A')
        eh = data.get('ExifImageHeight', 'N/A')
        
        # 原始旋转标签
        ori = str(data.get('Orientation', 'Horizontal (normal)'))
        
        # --- 状态判定逻辑 (修复点) ---
        # 使用 .lower() 确保匹配不论大小写都能通过
        is_normal_ori = ('normal' in ori.lower() or '1' in ori)
        has_thumb = "ThumbnailImage" in data
        
        # 物理宽高压扁判定 (横胖为黄，竖长为绿)
        try:
            is_vertical = int(fw) <= int(fh)
        except:
            is_vertical = True
            
        c_dim = C_GREEN if is_vertical else C_YELLOW
        c_ori = C_GREEN if is_normal_ori else C_YELLOW
        c_thumb = C_RED if has_thumb else C_GREEN
        thumb_str = "有" if has_thumb else "无"
        
        # 判定是否为“完美形态” (竖向 + 正常标签 + 无补丁)
        is_perfect = is_vertical and is_normal_ori and not has_thumb
        
        info = (f"📷 {C_MAGENTA}{ext:<4}{C_RESET} | "
                f"📏 底层物理像素:{c_dim}{fw}x{fh}{C_RESET} | "
                f"🏷️ EXIF标签数据:{ew}x{eh} | "
                f"🔄 旋转标签:{c_ori}{ori}{C_RESET} | "
                f"🖼️ 缩图:{c_thumb}{thumb_str}{C_RESET}")
        return model, sw, info, is_perfect
    except Exception:
        return "未知", "未知", f"📷 {C_RED}{ext} 读取失败{C_RESET}", False

def get_mov_info(path):
    """提取视频全量元数据"""
    cmd = [FFPROBE_PATH, '-v', 'quiet', '-print_format', 'json', '-show_streams', str(path)]
    try:
        res = subprocess.run(cmd, capture_output=True, text=True, errors='ignore')
        data = json.loads(res.stdout)
        
        v_stream = next((s for s in data.get('streams', []) if s.get('codec_type') == 'video'), {})
        w = v_stream.get('width', 'N/A')
        h = v_stream.get('height', 'N/A')
        codec = v_stream.get('codec_name', 'unknown').upper()
        
        rotation = "0"
        tags = v_stream.get('tags', {})
        if 'rotate' in tags:
            rotation = tags['rotate']
        else:
            for sd in v_stream.get('side_data_list', []):
                if sd.get('side_data_type') == 'Display Matrix':
                    rot = sd.get('rotation', 0)
                    if rot != 0:
                        rotation = str(rot)
                        
        return f"🎬 {C_MAGENTA}MOV {C_RESET} | 📏 物理像素:{C_WHITE}{w}x{h}{C_RESET} | 🎞️ 编码:{codec} | 🔄 旋转矩阵:{C_CYAN}{rotation}°{C_RESET}", rotation
    except Exception:
        return f"🎬 {C_RED}MOV 读取失败{C_RESET}", "0"

def format_filename(name, max_len=30):
    if len(name) <= max_len:
        return name.ljust(max_len)
    return f"{name[:15]}...{name[-12:]}".ljust(max_len)

def main():
    if os.name == 'nt': os.system('') 
    
    print(f"\n{C_CYAN}" + "━"*125)
    print(f"  🍎 苹果实况照片 (Live Photo) 全量元数据诊断报告 {C_WHITE}By LengxiQwQ{C_RESET}")
    print(f"{C_CYAN}" + "━"*125 + C_RESET)
    
    raw_input = input(f"\n{C_YELLOW}请输入包含样本的文件夹路径:\n>> {C_RESET}").strip()
    if raw_input.startswith("& "): raw_input = raw_input[2:].strip()
    folder = raw_input.strip('\"').strip('\'')
    p = Path(folder)
    
    if not p.exists() or not p.is_dir():
        print(f"\n{C_RED}[!] 路径无效！{C_RESET}"); return

    all_files = [f for f in p.iterdir() if f.is_file() and not f.name.startswith('._')]
    imgs = {f.stem.lower(): f for f in all_files if f.suffix.lower() in ['.jpg', '.jpeg', '.heic']}
    movs = {f.stem.lower(): f for f in all_files if f.suffix.lower() in ['.mov', '.mp4']}
    stems = sorted(list(set(imgs.keys()).union(set(movs.keys())))) 
    
    # 统计计数器
    stats = {'total': len(stems), 'perfect': 0, 'imperfect': 0, 'miss_img': 0, 'miss_mov': 0, 'rotated_mov': 0}
    
    print(f"\n{C_WHITE}正在深度解析 {len(stems)} 组样本...{C_RESET}\n")
    
    for stem in stems:
        img_p, mov_p = imgs.get(stem), movs.get(stem)
        img_name = format_filename(img_p.name) if img_p else f"{C_RED}缺失图片文件{C_RESET}".ljust(30)
        mov_name = format_filename(mov_p.name) if mov_p else f"{C_RED}缺失视频文件{C_RESET}".ljust(30)
        
        model, sw, img_info, is_p = "未知", "未知", "", False
        mov_info, mov_rot = "", "0"
        
        if img_p:
            model, sw, img_info, is_p = get_img_info(img_p, img_p.suffix.upper()[1:])
            if is_p: stats['perfect'] += 1
            else: stats['imperfect'] += 1
        else: stats['miss_img'] += 1

        if mov_p:
            mov_info, mov_rot = get_mov_info(mov_p)
            if mov_rot != "0" and mov_rot != 0: stats['rotated_mov'] += 1
        else: stats['miss_mov'] += 1

        print(f"📦 {C_CYAN}{img_name} {C_WHITE}&{C_CYAN} {mov_name}{C_RESET} | 📱 {C_WHITE}{model} (iOS {sw}){C_RESET}")
        print(f"  ├─ {img_info}")
        print(f"  └─ {mov_info}")
        print() 
    
    # --- 统计报告 ---
    print(f"{C_CYAN}" + "━"*125 + C_RESET)
    print(f"                  {C_WHITE}Statistics Report | 统计数据报告{C_RESET}")
    print(f"{C_CYAN}" + "━"*125 + C_RESET)
    print(f"  Total Samples     | 样本总组数:      {C_WHITE}{stats['total']}{C_RESET}\tgroups")
    print(f"{C_GREEN}  Perfect Images    | 完美形态图片:    {stats['perfect']}{C_RESET}\tfiles (原生竖向+无补丁)")
    print(f"{C_YELLOW}  Imperfect Images  | 建议修复图片:    {stats['imperfect']}{C_RESET}\tfiles (压扁/带标签/带补丁)")
    print(f"{C_CYAN}  Rotated Videos    | 带有矩阵视频:    {stats['rotated_mov']}{C_RESET}\tfiles (Matrix != 0°)")
    if stats['miss_img'] > 0 or stats['miss_mov'] > 0:
        print(f"{C_RED}  Missing Files     | 缺失文件数:      IMG:{stats['miss_img']} / MOV:{stats['miss_mov']}{C_RESET}")
    print(f"{C_CYAN}" + "━"*125 + C_RESET + "\n")

if __name__ == '__main__':
    main()