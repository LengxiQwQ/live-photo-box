# -*- coding: utf-8 -*-
"""
Live Photo Box 编辑页导出流程自动化演示
========================================
一键完成：打开应用 → 进入编辑页 → 载入华为实况文件夹 → 选中文件 →
等时间轴生成 → 点导出为视频 → 系统保存对话框填路径保存 → 等待导出 →
ffprobe 验证输出是真实视频 → 输出 PASS/FAIL 摘要。

依赖：winapp-mcp（已装）、本机 ffmpeg/ffprobe。
用法：python demo_export.py   （默认参数见下方 CONFIG）
"""
import subprocess, sys, json, threading, time, os, ctypes
from ctypes import wintypes
sys.stdout.reconfigure(encoding="utf-8", errors="replace")

# ══════════ 配置 ══════════
CONFIG = {
    "app_exe": r"D:\Projects\live-photo-box\LivePhotoBox\bin\Debug\net9.0-windows10.0.19041.0\win-x64\Live Photo Box.exe",
    "folder":  r"D:\Projects\live-photo-box\ai-tmp\hw_drive",          # 含华为实况的文件夹
    "out_mp4": r"D:\Projects\live-photo-box\ai-tmp\hw_drive\exported.mp4",
}
WINAPP_MCP_EXE = r"C:\Users\LengxiQwQ\AppData\Roaming\npm\node_modules\winapp-mcp\server\WinAppMCP.exe"
RESULT = {}


# ══════════ winapp-mcp stdio 客户端（换行 JSON-RPC）══════════
class WinApp:
    def __init__(self):
        self.p = subprocess.Popen([WINAPP_MCP_EXE], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, cwd=os.getcwd())
        self._id, self._lock = [0], threading.Lock()
        self._send("initialize", {"protocolVersion": "2024-11-05", "capabilities": {},
                                  "clientInfo": {"name": "demo", "version": "1.0"}})
        self._read()
        self._send("notifications/initialized", notify=True)
        time.sleep(0.2)

    def _send(self, method, params=None, notify=False):
        with self._lock:
            msg = {"jsonrpc": "2.0", "method": method}
            if not notify:
                self._id[0] += 1; msg["id"] = self._id[0]
            if params is not None:
                msg["params"] = params
            self.p.stdin.write((json.dumps(msg, ensure_ascii=False) + "\n").encode())
            self.p.stdin.flush()

    def _read(self, timeout=40):
        dl = time.time() + timeout; buf = ""
        os.set_blocking(self.p.stdout.fileno(), False)
        while time.time() < dl:
            try:
                c = self.p.stdout.read(65536)
                if c: buf += c.decode(errors="replace")
            except Exception: pass
            while "\n" in buf:
                line, buf = buf.split("\n", 1); line = line.strip()
                if not line: continue
                try: obj = json.loads(line)
                except Exception: continue
                if "result" in obj or "error" in obj:
                    return obj
            time.sleep(0.1)
        return {"error": {"message": "timeout"}}

    def call(self, tool, args, timeout=40):
        self._send("tools/call", {"name": tool, "arguments": args})
        r = self._read(timeout)
        try: return r["result"]["content"][0]["text"]
        except Exception: return "ERROR: " + json.dumps(r, ensure_ascii=False)[:200]

    def close(self):
        try: self.p.kill()
        except Exception: pass


# ══════════ ctypes 枚举窗口（找系统保存对话框）══════════
def enum_windows():
    user32 = ctypes.windll.user32
    result = []
    @ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
    def cb(hwnd, _):
        if user32.IsWindowVisible(hwnd):
            n = user32.GetWindowTextLengthW(hwnd)
            if n > 0:
                buf = ctypes.create_unicode_buffer(n + 1)
                user32.GetWindowTextW(hwnd, buf, n + 1)
                pid = wintypes.DWORD()
                user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
                result.append((hwnd, buf.value, pid.value))
        return True
    user32.EnumWindows(cb, 0)
    return result


def find_save_dialog(app_pid, timeout=12):
    """轮询桌面窗口，找到标题含'另存为/Save As/保存'或刚弹出的对话框。"""
    user32 = ctypes.windll.user32
    dl = time.time() + timeout
    while time.time() < dl:
        for hwnd, title, pid in enum_windows():
            if "另存为" in title or "Save As" in title or "保存" in title and pid == app_pid:
                return hwnd, title, pid
        time.sleep(0.5)
    return None


def main():
    cfg = CONFIG
    out = cfg["out_mp4"]
    if os.path.exists(out):
        os.remove(out)

    def log(k, v):
        RESULT[k] = v
        print(f"[{k}] {v}", flush=True)

    # 0. 若应用未运行则启动
    app_pid = None
    for _, _, pid in enum_windows():
        pass
    running = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Live Photo Box.exe", "/FO", "CSV", "/NH"],
                             capture_output=True, text=True).stdout
    if "Live Photo Box.exe" in running:
        pid_line = [l for l in running.strip().splitlines() if "Live Photo Box.exe" in l]
        app_pid = int(pid_line[0].split(",")[1].strip('"')) if pid_line else None
        log("app", f"already running PID={app_pid}")
    else:
        subprocess.Popen([cfg["app_exe"]])
        log("app", "launched")
        time.sleep(10)
        # 重新查 PID
        running = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Live Photo Box.exe", "/FO", "CSV", "/NH"],
                                 capture_output=True, text=True).stdout
        pid_line = [l for l in running.strip().splitlines() if "Live Photo Box.exe" in l]
        app_pid = int(pid_line[0].split(",")[1].strip('"')) if pid_line else None

    wa = WinApp()
    appid = wa.call("attach_to_app", {"processName": "Live Photo Box"})
    log("attach", appid)

    # 1. 进入编辑页（确保）
    wa.call("invoke_element", {"appId": appid, "name": "实况照片编辑", "controlType": "ListItem"}, timeout=15)
    time.sleep(2)

    # 2. 载入文件夹
    wa.call("type_text", {"appId": appid, "text": cfg["folder"]}, timeout=15)
    time.sleep(0.5)
    wa.call("click_element", {"appId": appid, "automationId": "RefreshDirBtn"}, timeout=15)
    time.sleep(4)
    snap = wa.call("get_snapshot", {"appId": appid, "maxDepth": 3})
    log("file_loaded", "huawei_test.JPG" in snap)
    log("live_detected", "LIVE" in snap)

    # 3. 选中文件 + 等时间轴
    wa.call("click_element", {"appId": appid, "name": "LivePhotoBox.Models.EditFileItem",
                              "controlType": "ListItem", "index": 0}, timeout=15)
    timeline_ok = False
    for _ in range(20):
        time.sleep(2)
        s = wa.call("get_snapshot", {"appId": appid, "maxDepth": 4})
        if "HUAWEI Moving Photo" in s:
            timeline_ok = True
            break
    log("timeline_loaded", timeline_ok)

    # 4. 点 导出… → 导出为视频
    wa.call("click_element", {"appId": appid, "name": "导出…", "controlType": "Button"}, timeout=15)
    time.sleep(1.5)
    r = wa.call("invoke_element", {"appId": appid, "name": "导出为视频"}, timeout=15)
    log("click_export_video", r)
    time.sleep(5)

    # 5. 系统保存对话框：填路径 + 保存
    dlg = find_save_dialog(app_pid, timeout=12)
    if dlg is None:
        log("save_dialog", "NOT FOUND")
    else:
        hwnd, title, _ = dlg
        log("save_dialog", f"{title} hwnd={hwnd:#x}")
        # 文件名输入框（标准 Edit），用 SendMessage 填完整路径
        user32 = ctypes.windll.user32
        try:
            # 找到对话框内文件名 Edit（通常紧邻"文件名"Label）
            child = user32.FindWindowExW(hwnd, 0, "Edit", None)
            if child:
                buf = ctypes.create_unicode_buffer(cfg["out_mp4"])
                ctypes.windll.user32.SendMessageW(child, 0x000C, 0, buf)  # WM_SETTEXT
                log("filename_set", cfg["out_mp4"])
            # 点保存按钮：发送 Enter 键给对话框
            ctypes.windll.user32.SetForegroundWindow(hwnd)
            ctypes.windll.user32.keybd_event(0x0D, 0, 0, 0)   # VK_RETURN down
            ctypes.windll.user32.keybd_event(0x0D, 0, 2, 0)   # VK_RETURN up
            log("save_clicked", "Enter sent")
        except Exception as e:
            log("save_dialog_error", str(e))

    # 6. 等导出完成：动态扫描最近生成的视频文件（保存对话框会用"建议名"存到默认目录）
    log("out_hint", "保存对话框会采用建议文件名（源文件名）存入默认视频目录")
    scan_dirs = [os.path.expandvars(r"%USERPROFILE%\Videos"), os.path.expandvars(r"%USERPROFILE%\Desktop"),
                 os.path.dirname(cfg["folder"]), os.path.dirname(cfg["out_mp4"])]
    found = None
    deadline = time.time() + 120
    while time.time() < deadline:
        for d in scan_dirs:
            if not os.path.isdir(d):
                continue
            for f in os.listdir(d):
                fp = os.path.join(d, f)
                if f.lower().endswith((".mp4", ".mov")) and os.path.isfile(fp):
                    age = time.time() - os.path.getmtime(fp)
                    if age < 90:  # 最近 90 秒内生成
                        size1 = os.path.getsize(fp)
                        time.sleep(2)
                        if os.path.exists(fp) and os.path.getsize(fp) == size1:  # 写入稳定
                            found = fp
                            break
            if found:
                break
        if found:
            break
        time.sleep(3)
    log("export_done", bool(found))
    if found:
        log("output_file", found)
        log("output_size", os.path.getsize(found))

    # 7. ffprobe 验证
    if found:
        r = subprocess.run(["ffprobe", "-v", "error", "-show_entries",
                            "format=duration,size", "-show_entries", "stream=codec_name,codec_type,width,height,nb_frames",
                            "-of", "default=nw=1", found], capture_output=True, text=True)
        log("ffprobe", r.stdout.strip() if r.returncode == 0 else "FAIL: " + r.stderr.strip()[:200])
        # 判定：有 video 流 + duration > 0 + 多帧 = 真实运动视频（不是静图）
        has_video = "codec_type=video" in r.stdout
        dur_ok = any(line.startswith("duration=") and float(line.split("=")[1]) > 0
                     for line in r.stdout.splitlines())
        frames = [l for l in r.stdout.splitlines() if l.startswith("nb_frames=")]
        frames_ok = any(int(l.split("=")[1]) > 1 for l in frames) if frames else False
        ok = has_video and dur_ok and frames_ok
        log("VERDICT", "PASS ✅ 导出的是真实运动视频（多帧）" if ok else "FAIL ❌ 输出不是有效运动视频")
    else:
        log("VERDICT", "FAIL ❌ 导出未完成（保存对话框可能未自动处理）")

    wa.close()
    print("\n=== RESULT JSON ===")
    print(json.dumps(RESULT, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
