import os
import time
import json
import shutil
import subprocess
import threading
from pathlib import Path
import traceback
from concurrent.futures import ThreadPoolExecutor, as_completed

# -------------------------------
# 终端颜色配置区 (ANSI Escape Codes)
# -------------------------------
C_GREEN = '\033[92m'
C_YELLOW = '\033[93m'
C_RED = '\033[91m'
C_CYAN = '\033[96m'
C_WHITE = '\033[97m'
C_RESET = '\033[0m'
# -------------------------------

# -------------------------------
# 工具路径配置区 (确保这些工具已加入系统环境变量)
# -------------------------------
FFMPEG_PATH = 'ffmpeg'
FFPROBE_PATH = 'ffprobe'
EXIFTOOL_PATH = 'exiftool'
# -------------------------------

def check_dependencies():
    """检查系统是否安装了必要的依赖"""
    missing = [tool for tool in [FFMPEG_PATH, FFPROBE_PATH, EXIFTOOL_PATH] if not shutil.which(tool)]
    if missing:
        print(f"\n{C_RED}[Fatal Error | 致命错误]{C_RESET}")
        print(f"Missing dependencies | 缺少必要依赖项: {', '.join(missing)}")
        print(f"Please install them and add to PATH | 请安装上述工具并添加到系统环境变量。")
        return False
    return True

def get_best_hevc_encoder():
    """自动检测系统支持的硬件编码器"""
    return get_best_video_encoder('hevc')

def get_best_video_encoder(codec_name):
    """自动检测系统支持的硬件编码器"""
    codec_name = (codec_name or 'hevc').lower()
    try:
        res = subprocess.run([FFMPEG_PATH, '-encoders'], capture_output=True, text=True, errors='ignore')
        encoders = res.stdout
        if codec_name in ['h264', 'avc', 'avc1']:
            if 'h264_nvenc' in encoders: return 'h264_nvenc', 'NVIDIA GPU (NVENC)'
            if 'h264_qsv' in encoders: return 'h264_qsv', 'Intel GPU (QuickSync)'
            if 'h264_amf' in encoders: return 'h264_amf', 'AMD GPU (AMF)'
            return 'libx264', 'Software CPU (libx264)'
        if 'hevc_nvenc' in encoders: return 'hevc_nvenc', 'NVIDIA GPU (NVENC)'
        if 'hevc_qsv' in encoders: return 'hevc_qsv', 'Intel GPU (QuickSync)'
        if 'hevc_amf' in encoders: return 'hevc_amf', 'AMD GPU (AMF)'
    except Exception:
        pass
    return ('libx264', 'Software CPU (libx264)') if codec_name in ['h264', 'avc', 'avc1'] else ('libx265', 'Software CPU (libx265)')

def get_video_info(file_path):
    """提取色彩空间、HDR 标签、色深及判定矩阵"""
    cmd = [FFPROBE_PATH, '-v', 'quiet', '-print_format', 'json', '-show_streams', str(file_path)]
    try:
        result = subprocess.run(cmd, capture_output=True, text=True, encoding='utf-8', errors='ignore')
        info = json.loads(result.stdout)
    except Exception:
        return None

    video_stream = next((s for s in info.get('streams', []) if s.get('codec_type') == 'video'), None)
    if not video_stream: return None

    codec_name = video_stream.get('codec_name', 'hevc')
    pix_fmt = video_stream.get('pix_fmt', '')
    
    needs_fix = False
    for side_data in video_stream.get('side_data_list', []):
        if side_data.get('side_data_type') == 'Display Matrix':
            matrix_str = side_data.get('displaymatrix', '')
            lines = [line for line in matrix_str.strip().split('\n') if ':' in line]
            if len(lines) >= 2:
                try:
                    row0 = [int(x) for x in lines[0].split(':')[1].split()]
                    row1 = [int(x) for x in lines[1].split(':')[1].split()]
                    a, b, c, d = row0[0], row0[1], row1[0], row1[1]
                    if a != 65536 or b != 0 or c != 0 or d != 65536:
                        needs_fix = True 
                except Exception: pass
                
    return {
        'codec': codec_name, 'needs_fix': needs_fix, 'pix_fmt': pix_fmt,
        'color_space': video_stream.get('color_space'),
        'color_transfer': video_stream.get('color_transfer'),
        'color_primaries': video_stream.get('color_primaries')
    }

def fix_abnormal_video(input_path, output_path, v_info, encoder):
    """重构物理像素与显示矩阵 (Matrix Rebuild)"""
    cmd = [FFMPEG_PATH, '-y', '-i', str(input_path)]
    source_codec = str(v_info.get('codec', '')).lower()
    is_h264 = source_codec in ['h264', 'avc', 'avc1']
    is_10bit = '10' in v_info['pix_fmt']
    target_pix_fmt = 'p010le' if is_10bit and encoder not in ['libx264'] else ('yuv420p10le' if is_10bit else 'yuv420p')
    cmd.extend(['-pix_fmt', target_pix_fmt])
    if v_info['color_primaries']: cmd.extend(['-color_primaries', v_info['color_primaries']])
    if v_info['color_transfer']: cmd.extend(['-color_trc', v_info['color_transfer']])
    if v_info['color_space']: cmd.extend(['-colorspace', v_info['color_space']])
    cmd.extend(['-c:v', encoder, '-tag:v', 'avc1' if is_h264 else 'hvc1'])
    if encoder == 'hevc_nvenc': cmd.extend(['-preset', 'p4', '-cq', '17', '-b:v', '0'])
    elif encoder == 'hevc_qsv': cmd.extend(['-global_quality', '17'])
    elif encoder == 'hevc_amf': cmd.extend(['-qp_i', '17', '-qp_p', '17'])
    elif encoder == 'h264_nvenc': cmd.extend(['-preset', 'p4', '-cq', '17', '-b:v', '0'])
    elif encoder == 'h264_qsv': cmd.extend(['-global_quality', '17'])
    elif encoder == 'h264_amf': cmd.extend(['-qp_i', '17', '-qp_p', '17'])
    else: cmd.extend(['-crf', '17', '-preset', 'medium'])
    cmd.extend(['-map_metadata', '0', '-metadata:s:v:0', 'rotate=', '-c:a', 'copy', str(output_path)])
    try:
        subprocess.run(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=True)
        return True
    except subprocess.CalledProcessError:
        return False

def merge_livephoto(photo_path, video_path, output_path, basename, mode):
    """合并 JPG 与视频数据"""
    temp_xmp = output_path.parent / f"temp_meta_{threading.get_ident()}_{basename}.xmp"
    video_size = os.path.getsize(video_path)
    try:
        shutil.copy2(photo_path, output_path)
        if mode == '1':
            xmp_command = [
                EXIFTOOL_PATH, "-XMP-GCamera:MicroVideo=1", "-XMP-GCamera:MicroVideoVersion=1",
                f"-XMP-GCamera:MicroVideoOffset={video_size}", "-XMP-GCamera:MicroVideoPresentationTimestampUs=0",
                str(output_path), "-overwrite_original"
            ]
            subprocess.run(xmp_command, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=True)
        else:
            xmp_content = f"""<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>
<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
<rdf:Description rdf:about="" xmlns:GCamera="http://ns.google.com/photos/1.0/camera/" xmlns:Container="http://ns.google.com/photos/1.0/container/" xmlns:Item="http://ns.google.com/photos/1.0/container/item/"
GCamera:MotionPhoto="1" GCamera:MotionPhotoVersion="1" GCamera:MotionPhotoPresentationTimestampUs="0">
<Container:Directory><rdf:Seq><rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="image/jpeg" Item:Semantic="Primary" Item:Length="0" Item:Padding="0"/></rdf:li>
<rdf:li rdf:parseType="Resource"><Container:Item Item:Mime="video/mp4" Item:Semantic="MotionPhoto" Item:Length="{video_size}" Item:Padding="0"/></rdf:li>
</rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end="w"?>"""
            with open(temp_xmp, "w", encoding="utf-8") as f:
                f.write(xmp_content)
            subprocess.run([EXIFTOOL_PATH, f"-xmp<={temp_xmp}", str(output_path), "-overwrite_original"], 
                           stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=True)
        with open(video_path, 'rb') as f_vid, open(output_path, 'ab') as f_out:
            f_out.write(f_vid.read())
        return True
    except Exception:
        return False
    finally:
        if temp_xmp.exists(): os.remove(temp_xmp)

def format_filename(name, max_len=28):
    """格式化文件名，保持对齐 (已缩减 7 个预留位)"""
    if len(name) > max_len:
        return name[:max_len-3] + "..."
    return name.ljust(max_len)

def safe_move(src, dst):
    """安全移动文件"""
    try:
        if src.exists():
            if dst.exists(): os.remove(dst)
            shutil.move(str(src), str(dst))
    except Exception:
        pass

def process_single_pair(args):
    """后台处理工作线程 (已移除日志图标)"""
    jpg_path, video_path, basename, mode_choice, target_path, final_dir, backup_dir, failed_pair_dir, hw_encoder, hw_name = args
    output_name = f"MVIMG_{basename}.jpg" if mode_choice == '1' else f"{basename}.MP.jpg"
    output_path = final_dir / output_name

    v_info = get_video_info(video_path)
    if not v_info:
        safe_move(jpg_path, failed_pair_dir / jpg_path.name)
        safe_move(video_path, failed_pair_dir / video_path.name)
        return (basename, 'failed', f"{C_RED}FAILED Error   | 解析失败 (已归档至文件夹4){C_RESET}")
        
    if v_info['needs_fix']:
        temp_v = target_path / f"temp_fix_{threading.get_ident()}_{video_path.name}"
        hdr_tag = "[HDR]" if '10' in v_info['pix_fmt'] else ""
        encoder, hw_name = get_best_video_encoder(v_info.get('codec'))
        if fix_abnormal_video(video_path, temp_v, v_info, encoder):
            if merge_livephoto(jpg_path, temp_v, output_path, basename, mode_choice):
                safe_move(jpg_path, backup_dir / jpg_path.name)
                safe_move(video_path, backup_dir / video_path.name)
                detail = f"{C_YELLOW}MATRIX Rebuilt | 矩阵重构完成 {hdr_tag}{C_RESET}"
                res = (basename, 'success_front', detail)
            else:
                safe_move(jpg_path, failed_pair_dir / failed_pair_dir / jpg_path.name)
                safe_move(video_path, failed_pair_dir / video_path.name)
                res = (basename, 'failed', f"{C_RED}FAILED Error   | 合成失败{C_RESET}")
        else:
            safe_move(jpg_path, failed_pair_dir / failed_pair_dir / jpg_path.name)
            safe_move(video_path, failed_pair_dir / video_path.name)
            res = (basename, 'failed', f"{C_RED}FAILED Error   | 重构报错{C_RESET}")
        if temp_v.exists(): os.remove(temp_v)
        return res
    else:
        if merge_livephoto(jpg_path, video_path, output_path, basename, mode_choice):
            safe_move(jpg_path, backup_dir / jpg_path.name)
            safe_move(video_path, backup_dir / video_path.name)
            detail = f"{C_GREEN}NATIVE Matched | 原生匹配完成{C_RESET}"
            return (basename, 'success_rear', detail)
        else:
            safe_move(jpg_path, failed_pair_dir / failed_pair_dir / jpg_path.name)
            safe_move(video_path, failed_pair_dir / video_path.name)
            return (basename, 'failed', f"{C_RED}FAILED Error   | 原生合成失败{C_RESET}")

def process_livephotos():
    if os.name == 'nt': os.system('') 

    # --- 标题栏 ---
    print("\n" + f"{C_CYAN}="*75 + C_RESET)
    print(f"{C_CYAN}  Apple Live Photo Converter | 苹果实况照片转换工具{C_RESET}  {C_WHITE}By LengxiQwQ{C_RESET}")
    print(f"{C_CYAN}-"*75 + C_RESET)
    print(f"{C_WHITE}  Protocol | 协议: Google V2  |  Color | 色彩: HDR Passthrough{C_RESET}")
    print(f"{C_WHITE}  Engine   | 并发: Multi-thread |  Accel | 加速: HW Encoding{C_RESET}")
    print(f"{C_CYAN}="*75 + C_RESET)

    if not check_dependencies(): return

    # --- 输入路径 ---
    target_dir = input(f"\n{C_CYAN}[1/3] Path | 路径:{C_RESET} Drag folder or input path | 请拖入文件夹或输入路径:\n >> ").strip().strip('"').strip("'")
    target_path = Path(target_dir)
    if not target_path.exists() or not target_path.is_dir():
        print(f"\n{C_RED}[!] Error: Invalid directory. | 错误：无效的路径。程序退出。{C_RESET}")
        return
    
    # --- 选择版本 ---
    print(f"\n{C_CYAN}[2/3] Protocol | 协议版本:{C_RESET}")
    print(f"  [1] {C_WHITE}Google Micro Video (V1){C_RESET} | Legacy Mode (MVIMG_ prefix) | 传统模式 (MVIMG_前缀)")
    print(f"  [2] {C_WHITE}Google Motion Photo (V2){C_RESET} | Modern Mode (.MP suffix)   | 现代模式 (.MP 后缀)")
    mode_choice = input(f"\n >> Enter option | 请输入选项 (1/2) [{C_CYAN}Default 2{C_RESET}]: ").strip() or '2'
    
    # --- 并发设置 ---
    workers_input = input(f"\n{C_CYAN}[3/3] Tasks | 并发任务:{C_RESET} Enter concurrent workers | 请输入并发数 [{C_CYAN}Default 5{C_RESET}]: ").strip()
    max_workers = int(workers_input) if workers_input.isdigit() else 5

    hw_encoder, hw_name = get_best_hevc_encoder()
    print(f"\n{C_CYAN}[System Info | 系统信息]{C_RESET}")
    print(f"  - Active Encoder | 激活编码器: {C_GREEN}{hw_name}{C_RESET}")
    print(f"  - Thread Limit   | 并发限制:   {C_GREEN}{max_workers}{C_RESET}")
    print(f"{C_CYAN}-"*75 + C_RESET)

    start_time = time.time() 

    final_dir = target_path / "1.已生成的实况照片"
    backup_dir = target_path / "2.原文件备份"
    standalone_dir = target_path / "3.独立视频"
    failed_pair_dir = target_path / "4.配对失败"
    for d in [final_dir, backup_dir, standalone_dir, failed_pair_dir]: d.mkdir(exist_ok=True)

    all_files = [f for f in target_path.iterdir() if f.is_file() and not f.name.startswith('._')]
    video_map = {f.stem.lower(): f for f in all_files if f.suffix.lower() in ['.mov', '.mp4']}
    jpg_files = {f.stem.lower(): f for f in all_files if f.suffix.lower() in ['.jpg', '.jpeg']}
    
    queue_args = []
    paired_stems = set()
    for stem, jpg_p in jpg_files.items():
        if stem in video_map:
            vid_p = video_map[stem]
            queue_args.append((jpg_p, vid_p, jpg_p.stem, mode_choice, target_path, final_dir, backup_dir, failed_pair_dir, hw_encoder, hw_name))
            paired_stems.add(stem)

    total_pairs = len(queue_args)
    stats = {'total_pairs': total_pairs, 'success_rear': 0, 'success_front': 0, 'failed': 0, 'standalone': 0}
    
    # 只处理未配对的视频，不再处理未配对的图片
    for stem, f_path in video_map.items():
        if stem not in paired_stems:
            safe_move(f_path, standalone_dir / f_path.name)
            stats['standalone'] += 1

    print(f"Scan complete | 扫描完成: Found {C_GREEN}{total_pairs}{C_RESET} pairs. Processing...\n")

    idx_width = len(f"[{total_pairs}/{total_pairs}]")
    completed_count = 0
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        futures = [executor.submit(process_single_pair, args) for args in queue_args]
        for future in as_completed(futures):
            completed_count += 1
            basename, res_type, detail_msg = future.result()
            if res_type in stats: stats[res_type] += 1
            
            idx_str = f"[{completed_count}/{total_pairs}]".ljust(idx_width)
            disp_name = format_filename(basename, max_len=28)
            print(f"{idx_str}  {disp_name} | {detail_msg}")

    # --- 统计报告 ---
    print("\n" + "="*65)
    print("                  Statistics Report | 统计报告")
    print("="*65)
    print(f"  Total Time        | 总耗时:          {round(time.time()-start_time, 2)}\ts")
    print(f"  Total Pairs       | 总配对数:        {stats['total_pairs']}\tpairs")
    print(f"{C_GREEN}  Native Matched    | 原生匹配:        {stats['success_rear']}\tpairs{C_RESET}")
    print(f"{C_YELLOW}  Matrix Rebuilt    | 矩阵重构:        {stats['success_front']}\tpairs{C_RESET}")
    print(f"{C_RED}  Failed Task       | 处理失败归档:    {stats['failed']}\tpairs{C_RESET}")
    print(f"{C_CYAN}  Isolated/Unpaired | 独立文件隔离:    {stats['standalone']}\tfiles{C_RESET}")
    print("="*65 + "\n")

if __name__ == "__main__":
    try:
        process_livephotos()
    except KeyboardInterrupt:
        print(f"\n{C_YELLOW}[!] Interrupted by user. | 用户手动取消。{C_RESET}")
    except Exception:
        print(f"\n{C_RED}[x] Unexpected Error | 发生非预期错误:{C_RESET}")
        traceback.print_exc()