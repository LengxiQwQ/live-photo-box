#!/usr/bin/env python3
"""Verify a single exported variant's XMP marker, structure and content.

Checks (per file type):
  - JPG/MP4/MOV: LivePhotoBox Version/Timestamp + dc:subject history via exiftool
  - HEIC: exiftool first; falls back to byte-level checks for HUAWEI layouts
    (uuid XMP box + iinf/iloc item-count consistency + box-chain integrity)
  - All: heif-dec decodes HEICs; embedded video extracts and ffprobe plays;
    exiftool reports no injection-introduced "Truncated" warning
  - Optional: expected Source= key inside the history entry
  - --skip-video: skip the embedded-video check (split outputs are dual-file,
    the image carries no embedded video)

Usage:
  python verify_variants.py <file> [--expected-source <Key>]
                             [--exiftool <path>] [--heif-dec <path>] [--ffprobe <path>]
                             [--tmp <dir>]

Exit code 0 = PASS, 1 = FAIL. Prints one result line to stdout.
"""

import argparse
import json
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile


XMP_UUID = bytes([0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
                  0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC])


def run(cmd, timeout=120):
    """Run a command, return (returncode, stdout_text, stderr_text)."""
    try:
        p = subprocess.run(cmd, capture_output=True, timeout=timeout)
        out = p.stdout.decode("utf-8", "replace")
        err = p.stderr.decode("utf-8", "replace")
        return p.returncode, out, err
    except FileNotFoundError:
        return -1, "", f"tool not found: {cmd[0]}"
    except subprocess.TimeoutExpired:
        return -1, "", "timeout"


def exiftool_tags(path, tags, exiftool):
    """Read exiftool tags as {short_name: value} (first occurrence)."""
    args = [exiftool, "-s", "-s"]
    for t in tags:
        args.append(f"-{t}")
    args.append(path)
    rc, out, _ = run(args, timeout=60)
    result = {}
    for line in out.splitlines():
        if ":" in line:
            key, _, val = line.partition(":")
            result.setdefault(key.strip(), val.strip())
    return result


def exiftool_warnings(path, exiftool):
    """Return a list of exiftool Warning tag values."""
    rc, out, _ = run([exiftool, "-s", "-s", "-Warning", path], timeout=60)
    return [ln.split(":", 1)[1].strip() for ln in out.splitlines() if ":" in ln]


def find_meta(blob):
    """Return (meta_pos, meta_size) of the first top-level meta box."""
    p = 0
    while p + 8 <= len(blob):
        sz = struct.unpack(">I", blob[p:p + 4])[0]
        if blob[p + 4:p + 8] == b"meta":
            return p, sz
        if sz < 8 or p + sz > len(blob):
            break
        p += sz
    return -1, 0


def meta_box_chain(blob, meta_pos, meta_size):
    """Walk inner meta boxes; return list of (type, size) or None if broken."""
    end = meta_pos + meta_size
    q = meta_pos + 12  # meta header 8 + FullBox(version/flags) 4
    boxes = []
    while q + 8 <= end:
        sz = struct.unpack(">I", blob[q:q + 4])[0]
        typ = blob[q + 4:q + 8]
        printable = all(32 <= b < 127 for b in typ)
        if sz < 8 or q + sz > end or not printable:
            return None
        boxes.append((typ.decode("latin1"), sz))
        q += sz
    return boxes


def iloc_iinf_counts(blob, meta_pos, meta_size):
    """Return (iloc_count, iinf_count) or None if either is missing."""
    end = meta_pos + meta_size
    q = meta_pos + 12
    iloc = iinf = None
    while q + 8 <= end:
        sz = struct.unpack(">I", blob[q:q + 4])[0]
        typ = blob[q + 4:q + 8]
        if sz < 8 or q + sz > end:
            break
        if typ == b"iloc":
            iloc = struct.unpack(">H", blob[q + 14:q + 16])[0]
        elif typ == b"iinf":
            iinf = struct.unpack(">H", blob[q + 12:q + 14])[0]
        q += sz
    if iloc is None or iinf is None:
        return None
    return iloc, iinf


def count_uuid_boxes(blob):
    """Count Adobe XMP uuid boxes (usertype occurrences)."""
    count = 0
    start = 0
    while True:
        idx = blob.find(XMP_UUID, start)
        if idx < 0:
            break
        count += 1
        start = idx + 1
    return count


def count_mime_xmp_items(blob, meta_pos, meta_size):
    """Count iinf infe items with item_type 'mime' and rdf+xml content type.

    Returns None when iinf cannot be parsed (caller treats as a problem).
    """
    end = meta_pos + meta_size
    q = meta_pos + 12
    iinf = None
    while q + 8 <= end:
        sz = struct.unpack(">I", blob[q:q + 4])[0]
        if blob[q + 4:q + 8] == b"iinf":
            iinf = q
            break
        if sz < 8 or q + sz > end:
            break
        q += sz
    if iinf is None:
        return None
    count = struct.unpack(">H", blob[iinf + 12:iinf + 14])[0]
    ip = iinf + 14  # 8 header + version/flags(4) + item count(2)
    xmp_count = 0
    for _ in range(count):
        if ip + 24 > end:
            break
        infe_size = struct.unpack(">I", blob[ip:ip + 4])[0]
        if infe_size < 24:
            if infe_size < 8:
                break
            ip += infe_size
            continue
        ver = blob[ip + 8]
        if ver == 2:
            item_type_pos, name_pos = 16, 20
        elif ver == 3:
            item_type_pos, name_pos = 14, 22
        else:
            ip += infe_size
            continue
        if blob[ip + item_type_pos:ip + item_type_pos + 4] == b"mime":
            nz = blob.find(b"\x00", ip + name_pos, ip + infe_size)
            if nz >= 0 and nz + 1 < ip + infe_size:
                ct = blob[nz + 1:ip + infe_size].decode("utf-8", "replace").lower()
                if ct.startswith("application/rdf+xml"):
                    xmp_count += 1
        ip += infe_size
    return xmp_count


def check_single_xmp(blob, meta_pos, meta_size):
    """Detect dual-XMP: more than one XMP mime item or more than one uuid box.

    Our outputs store XMP either as one mime item (exiftool path) or as one
    mime item whose data lives in a uuid box (HUAWEI injector path). More than
    one of either means a stale copy survived (dual-XMP regression).
    """
    problems = []
    uuid_count = count_uuid_boxes(blob)
    if uuid_count > 1:
        problems.append(f"{uuid_count} XMP uuid boxes (expected 0 or 1)")
    mime_count = count_mime_xmp_items(blob, meta_pos, meta_size)
    if mime_count is None:
        problems.append("iinf not found (cannot count XMP mime items)")
    elif mime_count != 1:
        problems.append(f"{mime_count} XMP mime items (expected exactly 1)")
    return problems


def check_heic_bytes(path):
    """Byte-level HEIC checks for HUAWEI layouts (exiftool cannot read them)."""
    problems = []
    try:
        with open(path, "rb") as f:
            blob = f.read()
    except OSError as exc:
        return [f"read failed: {exc}"]

    # XMP history text must be physically present.
    if b"LivePhotoBox:" not in blob:
        problems.append("no LivePhotoBox history bytes found")
    if XMP_UUID not in blob:
        problems.append("XMP uuid box (Adobe usertype) not found")

    meta_pos, meta_size = find_meta(blob)
    if meta_pos < 0:
        problems.append("meta box not found")
        return problems

    # 单 XMP 检测（无论 exiftool 能否读取都要执行，抓住双 XMP 回归）。
    problems += check_single_xmp(blob, meta_pos, meta_size)

    boxes = meta_box_chain(blob, meta_pos, meta_size)
    if boxes is None:
        problems.append("meta inner box chain is broken")
    else:
        types = [t for t, _ in boxes]
        if "uuid" not in types:
            problems.append("XMP uuid box not inside meta")
        counts = iloc_iinf_counts(blob, meta_pos, meta_size)
        if counts is not None and counts[0] != counts[1]:
            problems.append(f"iloc({counts[0]}) != iinf({counts[1]}) item counts")

    # Top-level chain must continue past meta with a valid mdat (main image).
    after = meta_pos + meta_size
    if after + 8 <= len(blob):
        sz = struct.unpack(">I", blob[after:after + 4])[0]
        typ = blob[after + 4:after + 8]
        if sz < 8 or after + sz > len(blob) or typ != b"mdat":
            problems.append("top-level box chain broken right after meta")
    return problems


def extract_video(blob, path):
    """Extract the embedded video payload; return bytes or None."""
    # Use the LAST LIVE_ marker: single-file sources that already carry a tail
    # (e.g. a HUAWEI live HEIC used as a merge image source) end up with two
    # tails; the real one is at the end of the file.
    live = blob.rfind(b"LIVE_")
    end = live if live > 0 else len(blob)
    is_heic = path.lower().endswith((".heic", ".heif"))
    # First ftyp >= 0x1000 avoids XMP/EXIF noise; HEIC containers have their
    # own ftyp at offset 4, so skip to the second one for HEIC.
    start = 0x1000
    ftyp = blob.find(b"ftyp", start)
    if is_heic:
        second = blob.find(b"ftyp", ftyp + 4) if ftyp >= 0 else -1
        if second > 0:
            ftyp = second
    if ftyp < 0:
        return None
    return blob[ftyp - 4:end]


def ffprobe_ok(video_bytes, ffprobe, tmp_dir):
    """Write video bytes to a temp file and probe with ffprobe."""
    if not video_bytes or len(video_bytes) < 64:
        return False, "empty/too small video payload"
    p = os.path.join(tmp_dir, "probe.mp4")
    with open(p, "wb") as f:
        f.write(video_bytes)
    args = [ffprobe, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=codec_name,width,height,duration",
            "-of", "csv=p=0", p]
    rc, out, err = run(args, timeout=60)
    if rc != 0:
        return False, (err or out).strip().replace("\n", " | ")[:200]
    return True, out.strip()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path")
    ap.add_argument("--expected-source", default=None,
                    help="expected Source= key inside the history entry")
    ap.add_argument("--expected-action", default=None,
                    help="expected LivePhotoBox:{action}@ history entry")
    ap.add_argument("--exiftool", default="exiftool")
    ap.add_argument("--heif-dec", default="heif-dec")
    ap.add_argument("--ffprobe", default="ffprobe")
    ap.add_argument("--tmp", default=None)
    ap.add_argument("--skip-video", action="store_true",
                    help="skip embedded-video extraction/playback check")
    args = ap.parse_args()

    path = args.path
    problems = []
    notes = []
    ext = os.path.splitext(path)[1].lower()

    if not os.path.isfile(path):
        print(f"FAIL {path} | file not found")
        return 1

    # 1. XMP marker via exiftool (all types).
    tags = exiftool_tags(path, ["XMP-LivePhotoBox:Version",
                                "XMP-LivePhotoBox:Timestamp",
                                "XMP-dc:Subject"], args.exiftool)
    version = tags.get("Version")
    timestamp = tags.get("Timestamp")
    subject = tags.get("Subject", "")
    has_history = "LivePhotoBox:" in subject

    if ext in (".jpg", ".jpeg", ".mp4", ".mov", ".m4v"):
        if not version or not re.fullmatch(r"\d+\.\d+\.\d+", version):
            problems.append(f"missing/bad Version ({version!r})")
        if not timestamp:
            problems.append("missing Timestamp")
        if not has_history:
            problems.append("missing LivePhotoBox history in dc:subject")
        if args.expected_source and f"Source={args.expected_source}" not in subject:
            problems.append(
                f"Source={args.expected_source} not found in: {subject[:160]}")
        if args.expected_action and f"LivePhotoBox:{args.expected_action}@" not in subject:
            problems.append(
                f"expected action {args.expected_action} not found in: {subject[:160]}")
    elif ext in (".heic", ".heif"):
        if version and timestamp and has_history:
            notes.append("XMP read via exiftool")
            if args.expected_source and f"Source={args.expected_source}" not in subject:
                problems.append(
                    f"Source={args.expected_source} not found in: {subject[:160]}")
            if args.expected_action and f"LivePhotoBox:{args.expected_action}@" not in subject:
                problems.append(
                    f"expected action {args.expected_action} not found in: {subject[:160]}")
            # exiftool 可读的 HEIC 也要做单 XMP 字节检测。
            try:
                with open(path, "rb") as f:
                    blob = f.read()
                meta_pos, meta_size = find_meta(blob)
                if meta_pos >= 0:
                    problems += check_single_xmp(blob, meta_pos, meta_size)
            except OSError as exc:
                problems.append(f"read failed: {exc}")
        else:
            # HUAWEI-style layout: exiftool cannot read it, verify bytes.
            problems += check_heic_bytes(path)
            notes.append("XMP verified byte-level (exiftool cannot read HUAWEI meta)")
            # For Apple-sourced Huawei merges exiftool works; only use byte-level
            # fallback when exiftool found nothing at all.
            if not version and not timestamp and not has_history:
                pass
            else:
                # Partial read: report what is missing.
                if not version:
                    problems.append("missing Version")
                if not timestamp:
                    problems.append("missing Timestamp")
                if not has_history:
                    problems.append("missing LivePhotoBox history")
    else:
        problems.append(f"unsupported extension {ext}")

    # 2. heif-dec decode for HEIC.
    if ext in (".heic", ".heif"):
        tmp_dir = args.tmp or tempfile.mkdtemp(prefix="lpb_verify_")
        try:
            out_png = os.path.join(tmp_dir, "dec.png")
            rc, out, err = run([args.heif_dec, path, "-o", out_png], timeout=120)
            if rc != 0:
                problems.append(f"heif-dec failed: {(err or out).strip()[:160]}")
            else:
                notes.append("heif-dec OK")
        finally:
            if not args.tmp:
                shutil.rmtree(tmp_dir, ignore_errors=True)

    # 3. Embedded video extraction + ffprobe (single-file JPG/HEIC only).
    if ext in (".jpg", ".jpeg", ".heic", ".heif") and not args.skip_video:
        try:
            with open(path, "rb") as f:
                blob = f.read()
            video = extract_video(blob, path)
            if video is None:
                problems.append("no embedded video (ftyp) found")
            else:
                tmp_dir = args.tmp or tempfile.mkdtemp(prefix="lpb_verify_")
                try:
                    ok, detail = ffprobe_ok(video, args.ffprobe, tmp_dir)
                    if ok:
                        notes.append(f"video OK ({detail})")
                    else:
                        problems.append(f"embedded video not playable: {detail}")
                finally:
                    if not args.tmp:
                        shutil.rmtree(tmp_dir, ignore_errors=True)
        except OSError as exc:
            problems.append(f"read failed: {exc}")

    # 4. Injection-introduced Truncated warnings.
    for w in exiftool_warnings(path, args.exiftool):
        low = w.lower()
        if "truncated" in low:
            # HUAWEI devices carry a tail region exiftool reports as
            # "Unknown trailer with truncated 'XX 20 20' data"; that is
            # inherent to the source structure, not our injection.
            if low.startswith("unknown trailer with truncated"):
                notes.append("HUAWEI tail warning (inherent): " + w[:80])
            else:
                problems.append("injection-related warning: " + w[:120])

    if problems:
        print(f"FAIL {os.path.basename(path)} | {'; '.join(dict.fromkeys(problems))}")
        return 1
    print(f"PASS {os.path.basename(path)} | {'; '.join(notes)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
