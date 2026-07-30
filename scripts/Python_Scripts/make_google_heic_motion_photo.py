#!/usr/bin/env python3
import argparse
import os
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path


def run_checked(args):
    result = subprocess.run(args, capture_output=True)
    if result.returncode != 0:
        stderr = result.stderr.decode("utf-8", errors="replace")
        stdout = result.stdout.decode("utf-8", errors="replace")
        raise RuntimeError(f"Command failed: {args}\nSTDOUT:\n{stdout}\nSTDERR:\n{stderr}")
    return result.stdout


def write_google_xmp(exiftool, image_path, video_size, timestamp_us, primary_mime, video_mime):
    # ExifTool's public tag is ContainerDirectory, but this writes the Google
    # "Container:Directory" RDF structure required by the Motion Photo spec.
    directory = (
        f"[{{Item={{Mime={primary_mime},Semantic=Primary,Length=0,Padding=8}}}},"
        f"{{Item={{Mime={video_mime},Semantic=MotionPhoto,Length={video_size},Padding=0}}}}]"
    )
    run_checked([
        str(exiftool),
        "-overwrite_original",
        "-struct",
        "-XMP-GCamera:MotionPhoto=1",
        "-XMP-GCamera:MotionPhotoVersion=1",
        f"-XMP-GCamera:MotionPhotoPresentationTimestampUs={timestamp_us}",
        f"-XMP-GContainer:ContainerDirectory={directory}",
        str(image_path),
    ])


def append_mpvd(output_path, video_path):
    video_size = video_path.stat().st_size
    box_size = video_size + 8
    if box_size > 0xFFFFFFFF:
        raise ValueError("This simple mpvd writer only supports videos under 4 GiB")
    with output_path.open("ab") as out_file, video_path.open("rb") as video_file:
        out_file.write(struct.pack(">I4s", box_size, b"mpvd"))
        shutil.copyfileobj(video_file, out_file, length=1024 * 1024)


def validate_tail(output_path, video_size):
    file_size = output_path.stat().st_size
    mpvd_offset = file_size - video_size - 8
    with output_path.open("rb") as f:
        f.seek(mpvd_offset)
        header = f.read(8)
    size, fourcc = struct.unpack(">I4s", header)
    if fourcc != b"mpvd":
        raise ValueError(f"Expected mpvd at {mpvd_offset}, got {fourcc!r}")
    if size != video_size + 8:
        raise ValueError(f"Expected mpvd size {video_size + 8}, got {size}")
    return mpvd_offset, file_size


def main():
    parser = argparse.ArgumentParser(description="Create a Google HEIC Motion Photo with embedded MOV in mpvd.")
    parser.add_argument("--image", required=True, type=Path)
    parser.add_argument("--video", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--exiftool", required=True, type=Path)
    parser.add_argument("--timestamp-us", default="-1")
    parser.add_argument("--primary-mime", default="image/heic")
    parser.add_argument("--video-mime", default="video/quicktime")
    args = parser.parse_args()

    image = args.image.resolve()
    video = args.video.resolve()
    output = args.output.resolve()
    exiftool = args.exiftool.resolve()

    if not image.exists():
        raise FileNotFoundError(image)
    if not video.exists():
        raise FileNotFoundError(video)
    if not exiftool.exists():
        raise FileNotFoundError(exiftool)

    output.parent.mkdir(parents=True, exist_ok=True)
    video_size = video.stat().st_size

    temp_root = Path(os.environ.get("TMP", output.parent)).resolve()
    with tempfile.TemporaryDirectory(prefix="motion_photo_", dir=str(temp_root)) as temp_dir:
        temp_dir = Path(temp_dir)
        temp_output = temp_dir / output.name
        shutil.copy2(image, temp_output)
        write_google_xmp(
            exiftool,
            temp_output,
            video_size,
            args.timestamp_us,
            args.primary_mime,
            args.video_mime,
        )
        append_mpvd(temp_output, video)
        mpvd_offset, file_size = validate_tail(temp_output, video_size)
        if output.exists():
            output.unlink()
        shutil.move(str(temp_output), str(output))

    print(f"output={output}")
    print(f"video_size={video_size}")
    print(f"mpvd_offset={mpvd_offset}")
    print(f"file_size={file_size}")


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        sys.exit(1)
