import os
import json
import subprocess
import shutil
from pathlib import Path

# --- 终端颜色配置 ---
C_CYAN = '\033[96m'
C_YELLOW = '\033[93m'
C_GREEN = '\033[92m'
C_RED = '\033[91m'
C_WHITE = '\033[97m'
C_RESET = '\033[0m'

EXIFTOOL_PATH = 'exiftool'
JPEGTRAN_PATH = 'jpegtran' 

def check_dependencies():
    missing = [tool for tool in [EXIFTOOL_PATH, JPEGTRAN_PATH] if not shutil.which(tool)]
    if missing:
        print(f"\n{C_RED}[致命错误] 缺少核心依赖：{', '.join(missing)}{C_RESET}")
        return False
    return True

def get_jpg_metadata(jpg_path):
    cmd = [
        EXIFTOOL_PATH, '-j',
        '-ImageWidth', '-ImageHeight',
        '-Orientation', '-ThumbnailImage',
        str(jpg_path)
    ]
    try:
        res = subprocess.run(cmd, capture_output=True, text=True, errors='ignore')
        return json.loads(res.stdout)[0]
    except Exception:
        return None

def lossless_fix_and_strip(jpg_path, data):
    """物理翻转 + 抹平标签 + 剥离缩图"""
    ori_str = str(data.get('Orientation', ''))
    
    angle = None
    if '90 CW' in ori_str: angle = '90'
    elif '270 CW' in ori_str or '90 CCW' in ori_str: angle = '270'
    elif '180' in ori_str: angle = '180'
        
    if not angle: return False, "未检测到标准旋转标签"

    temp_jpg = jpg_path.with_name(f"temp_fix_{jpg_path.name}")
    
    try:
        # 1. 无损翻转
        subprocess.run([JPEGTRAN_PATH, '-copy', 'all', '-rotate', angle, '-outfile', str(temp_jpg), str(jpg_path)], 
                       stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=True)
        shutil.move(str(temp_jpg), str(jpg_path))

        # 2. 清洗标签 + 剥离缩图
        w, h = data.get('ImageWidth'), data.get('ImageHeight')
        cmd_exif = [
            EXIFTOOL_PATH, 
            '-Orientation=1', '-n', 
            '-ThumbnailImage=',  # 核心：直接清空缩略图数据
            '-overwrite_original'
        ]
        if angle in ['90', '270'] and w and h:
            cmd_exif.extend([f'-ExifImageWidth={h}', f'-ExifImageHeight={w}'])
            
        cmd_exif.append(str(jpg_path))
        subprocess.run(cmd_exif, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=True)
        
        return True, f"物理翻转 {angle}° + 垃圾缩图已清除"
    except Exception as e:
        if temp_jpg.exists(): os.remove(temp_jpg)
        return False, f"处理异常: {str(e)}"

def strip_thumbnail_only(jpg_path):
    """仅剥离缩略图（针对底层正常但带有垃圾缩图的文件）"""
    try:
        cmd = [
            EXIFTOOL_PATH, 
            '-ThumbnailImage=', 
            '-overwrite_original', 
            str(jpg_path)
        ]
        subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=True)
        return True, "仅执行缩图剥离（底层矩阵已是完美状态）"
    except Exception as e:
        return False, f"清理异常: {str(e)}"

def main():
    if os.name == 'nt': os.system('') 

    print(f"\n{C_CYAN}" + "="*75)
    print(f"  🍎 苹果实况照片缩略图大清洗工具 (无损重构 + 瘦身版) {C_WHITE}By LengxiQwQ")
    print(f"{C_CYAN}" + "="*75 + C_RESET)
    
    if not check_dependencies(): return
    
    # --- 修复 PowerShell 拖拽路径的防呆逻辑 ---
    raw_input = input(f"\n{C_YELLOW}请输入要执行大清洗的图片文件夹路径:\n>> {C_RESET}").strip()
    
    if raw_input.startswith("& "):
        raw_input = raw_input[2:].strip()
        
    folder = raw_input.strip('\"').strip('\'')
    p = Path(folder)
    
    if not p.exists() or not p.is_dir():
        print(f"\n{C_RED}[!] 致命错误：路径无效。{C_RESET}")
        print(f"{C_YELLOW}系统识别到的实际路径为: {folder}{C_RESET}")
        print(f"{C_RED}请检查路径是否存在，程序已终止。{C_RESET}")
        return
    # ----------------------------------------

    jpgs = [f for f in p.iterdir() if f.suffix.lower() in ['.jpg', '.jpeg'] and not f.name.startswith('._')]
    print(f"\n{C_WHITE}开始执行地毯式扫描与大清洗...{C_RESET}\n")
    
    stats = {'fixed': 0, 'stripped': 0, 'skipped': 0}
    
    for jpg in jpgs:
        data = get_jpg_metadata(jpg)
        if not data: continue
            
        w = data.get('ImageWidth', 0)
        h = data.get('ImageHeight', 0)
        ori = str(data.get('Orientation', 'Horizontal (normal)'))
        has_thumb = 'ThumbnailImage' in data
        
        # 状况A：底层歪了（万恶之源） -> 执行最高级别的全套重构
        if w > h and 'Rotate' in ori:
            print(f"{C_YELLOW}[🎯 重构并剥离]{C_RESET} {jpg.name}")
            success, msg = lossless_fix_and_strip(jpg, data)
            if success:
                print(f"  └─ {C_GREEN}{msg}{C_RESET}")
                stats['fixed'] += 1
            else:
                print(f"  └─ {C_RED}{msg}{C_RESET}")
                
        # 状况B：底层是正的，但是藏了缩略图（定时炸弹） -> 执行物理剥离
        elif has_thumb:
            print(f"{C_YELLOW}[🗑️ 瘦身大清洗]{C_RESET} {jpg.name}")
            success, msg = strip_thumbnail_only(jpg)
            if success:
                print(f"  └─ {C_GREEN}{msg}{C_RESET}")
                stats['stripped'] += 1
            else:
                print(f"  └─ {C_RED}{msg}{C_RESET}")
                
        # 状况C：原生竖向且没有缩图（最纯净的状态） -> 直接跳过
        else:
            print(f"{C_CYAN}[✨ 完美跳过]{C_RESET} {jpg.name} (已是极简完美形态，无需处理)")
            stats['skipped'] += 1

    print(f"\n{C_CYAN}" + "="*75 + C_RESET)
    print(f"  🎉 {C_GREEN}清洗任务结束！你的文件现在无比纯净。{C_RESET}")
    print(f"  ├─ 矩阵重构 + 清除缩图: {C_GREEN}{stats['fixed']}{C_RESET} 张")
    print(f"  ├─ 仅清除垃圾内置缩图:  {C_GREEN}{stats['stripped']}{C_RESET} 张")
    print(f"  └─ 已经是完美原生形态:  {C_CYAN}{stats['skipped']}{C_RESET} 张")
    print(f"{C_CYAN}" + "="*75 + C_RESET)

if __name__ == '__main__':
    main()