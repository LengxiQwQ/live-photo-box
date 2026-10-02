#!/usr/bin/env python3
"""Independent P5-R4 corpus and color-policy preflight.

This module deliberately does not import or invoke LivePhotoBox readers. Its
JPEG marker and HEIF primary-property parsers are offline evidence tools only.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import shutil
import struct
import subprocess
import sys
from datetime import datetime, timezone
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any

import numpy
import PIL
from PIL import Image, ImageCms
import skimage
from skimage.color import deltaE_ciede2000, rgb2lab

from source_lock import SourceLockError, verify_offline_lock


EXPECTED_PROFILE = "P5-R4-v1"
EXPECTED_PYTHON = {"pillow": "12.1.1", "littleCms": "2.17", "numpy": "2.5.0", "scikitImage": "0.26.0"}
EXPECTED_TOOLS = {"exiftool": "13.55", "ffmpeg": "8.0.1", "ffprobe": "8.0.1", "heifConvert": "1.23.2"}
EXPECTED_ROUTES = {
    "P5R4_AppleP3JpegToHeic",
    "P5R4_UnprofiledJpegToHeic",
    "P5R4_HuaweiHeicPrimaryToJpeg",
    "P5R4_TenBitHeicToJpeg",
    "P5R4_TenBitHeicPassthrough",
}
EXPECTED_RENDER_PIPELINE_ID = "P5R4-RGB24-LCMS-v1"
EXPECTED_RENDER_THRESHOLDS = {
    "meanDeltaE00Maximum": 2.0,
    "p95DeltaE00Maximum": 5.0,
    "p99DeltaE00Maximum": 9.0,
}
EXPECTED_THRESHOLD_STATUS = "frozen-before-formal-conversion; must not be adjusted after observing route outputs"
EXPECTED_TESTS = {
    "P5R4_AppleP3JpegToHeic",
    "P5R4_UnprofiledJpegToHeic",
    "P5R4_HuaweiHeicPrimaryToJpeg",
    "P5R4_TenBitHeicToJpeg",
    "P5R4_TenBitHeicPassthrough",
    "P5R4_TargetProfileTransformUnsupported",
    "P5R4_AmbiguousColorMetadata",
}
EXPECTED_OUTPUT_LAYOUT = {
    "apple-p3-strict": (0, ".heic"),
    "apple-p3-best-effort": (1, ".heic"),
    "unprofiled-jpeg-strict": (0, ".heic"),
    "unprofiled-jpeg-best-effort": (1, ".heic"),
    "huawei-primary-strict": (0, ".jpg"),
    "huawei-primary-best-effort": (1, ".jpg"),
    "ten-bit-jpeg-strict": (0, ".jpg"),
    "ten-bit-jpeg-best-effort": (0, ".jpg"),
    "ten-bit-passthrough-strict": (1, ".heic"),
    "ten-bit-passthrough-best-effort": (1, ".heic"),
    "nclx-only-no-transform": (0, ".jpg"),
    "ambiguous-color-output": (0, ".jpg"),
}
_LAST_REPORT: dict[str, Any] = {}
_PRIOR_RENDER_REPORTS: list[tuple[str, dict[str, Any]]] = []
XMP_COLOR_INTERPRETATION_NAMES = {
    "colorspace", "colorprimaries", "primaries", "transfercharacteristics",
    "transferfunction", "matrixcoefficients", "gamma", "whitepoint",
    "chromaticities", "iccprofile", "icccolorprofile", "outputprofile",
    "outputcolorspace", "colorprofile", "profiledescription", "colormode",
    "colorencoding", "colorrenderingintent",
}


class ValidationError(RuntimeError):
    pass


def _u16(data: bytes, offset: int) -> int:
    return struct.unpack_from(">H", data, offset)[0]


def _u32(data: bytes, offset: int) -> int:
    return struct.unpack_from(">I", data, offset)[0]


def _boxes(data: bytes, start: int = 0, end: int | None = None) -> list[dict[str, Any]]:
    """Parse one ISO-BMFF box level with strict parent bounds."""
    if end is None:
        end = len(data)
    result: list[dict[str, Any]] = []
    pos = start
    while pos < end:
        if end - pos < 8:
            raise ValidationError(f"Truncated ISO-BMFF box header at offset {pos}.")
        size32 = _u32(data, pos)
        box_type = data[pos + 4 : pos + 8].decode("latin-1")
        header_size = 8
        if size32 == 1:
            if end - pos < 16:
                raise ValidationError(f"Truncated largesize box header at offset {pos}.")
            size = struct.unpack_from(">Q", data, pos + 8)[0]
            header_size = 16
        elif size32 == 0:
            size = end - pos
        else:
            size = size32
        if box_type == "uuid":
            header_size += 16
        if size < header_size or pos + size > end:
            raise ValidationError(f"Invalid {box_type!r} box size {size} at offset {pos}.")
        result.append({
            "type": box_type,
            "start": pos,
            "size": size,
            "header": header_size,
            "payload_start": pos + header_size,
            "payload_end": pos + size,
        })
        pos += size
    if pos != end:
        raise ValidationError("ISO-BMFF child boxes do not end at their parent boundary.")
    return result


def _one(items: list[dict[str, Any]], box_type: str, parent: str) -> dict[str, Any]:
    found = [box for box in items if box["type"] == box_type]
    if len(found) != 1:
        raise ValidationError(f"Expected exactly one {box_type!r} box under {parent}; found {len(found)}.")
    return found[0]


def _fullbox(data: bytes, box: dict[str, Any]) -> tuple[int, int, int]:
    p = box["payload_start"]
    if box["payload_end"] - p < 4:
        raise ValidationError(f"Truncated fullbox {box['type']}.")
    version = data[p]
    flags = int.from_bytes(data[p + 1 : p + 4], "big")
    return version, flags, p + 4


def parse_heif(data: bytes) -> dict[str, Any]:
    # Huawei Moving Photo stores a complete still HEIF followed by an MP4 and
    # its documented 60-byte v6_fXX/LIVE_ trailer. Parse only the first HEIF
    # top-level box sequence; recognize the video boundary by the second
    # top-level ftyp whose major brand is mp42, never by searching raw bytes.
    top: list[dict[str, Any]] = []
    pos = 0
    ftyp_count = 0
    live_photo_video: dict[str, Any] | None = None
    while pos < len(data):
        if len(data) - pos < 8:
            raise ValidationError(f"Truncated top-level ISO-BMFF box header at offset {pos}.")
        size32 = _u32(data, pos)
        box_type = data[pos + 4 : pos + 8].decode("latin-1")
        header_size = 8
        if size32 == 1:
            if len(data) - pos < 16:
                raise ValidationError(f"Truncated largesize box header at offset {pos}.")
            size = struct.unpack_from(">Q", data, pos + 8)[0]
            header_size = 16
        elif size32 == 0:
            size = len(data) - pos
        else:
            size = size32
        if box_type == "uuid":
            header_size += 16
        if size < header_size or pos + size > len(data):
            raise ValidationError(f"Invalid top-level {box_type!r} box size {size} at offset {pos}.")
        box = {
            "type": box_type,
            "start": pos,
            "size": size,
            "header": header_size,
            "payload_start": pos + header_size,
            "payload_end": pos + size,
        }
        if box_type == "ftyp":
            ftyp_count += 1
            major_brand = data[box["payload_start"] : box["payload_start"] + 4]
            if ftyp_count == 2:
                if major_brand != b"mp42":
                    raise ValidationError(f"Unexpected second top-level ftyp major brand: {major_brand!r}.")
                if len(data) - pos < 60:
                    raise ValidationError("Huawei Moving Photo MP4 suffix is shorter than its documented 60-byte trailer.")
                trailer = data[-60:]
                if not re.fullmatch(rb"v6_f[0-9]{2} {14}[0-9]{3}:[0-9]{4} {12}LIVE_[0-9]{7} {8}", trailer):
                    raise ValidationError("Second ftyp is mp42 but the Huawei 60-byte tail does not match its documented field layout.")
                mp4_size = len(data) - pos - 60
                live_marker = trailer[40:52].decode("ascii")
                if int(live_marker[5:]) != mp4_size + 20:
                    raise ValidationError("Huawei LIVE_ tail length does not match the primary-boundary MP4 length + 20 rule.")
                live_photo_video = {
                    "protocol": "Huawei Moving Photo second top-level ftyp major=mp42 + 60-byte v6_fXX/LIVE_ tail",
                    "videoStart": pos,
                    "videoByteSize": mp4_size,
                    "tailBytes": 60,
                    "videoSha256": hashlib.sha256(data[pos : pos + mp4_size]).hexdigest().upper(),
                    "tailSha256": hashlib.sha256(trailer).hexdigest().upper(),
                    "liveMarker": live_marker,
                }
                break
        top.append(box)
        pos += size

    if not top or top[0]["type"] != "ftyp":
        raise ValidationError("HEIF image prefix must begin with ftyp.")
    _one(top, "ftyp", "file")
    meta = _one(top, "meta", "file")
    meta_children = _boxes(data, meta["payload_start"] + 4, meta["payload_end"])
    pitm = _one(meta_children, "pitm", "meta")
    pitm_version, _, p = _fullbox(data, pitm)
    if pitm_version == 0:
        primary_id = _u16(data, p)
    elif pitm_version == 1:
        primary_id = _u32(data, p)
    else:
        raise ValidationError(f"Unsupported pitm version {pitm_version}.")

    iprp = _one(meta_children, "iprp", "meta")
    iprp_children = _boxes(data, iprp["payload_start"], iprp["payload_end"])
    ipco = _one(iprp_children, "ipco", "iprp")
    ipma_boxes = [box for box in iprp_children if box["type"] == "ipma"]
    if len(ipma_boxes) != 1:
        raise ValidationError(f"Expected one ipma property-association box; found {len(ipma_boxes)}.")

    property_boxes = _boxes(data, ipco["payload_start"], ipco["payload_end"])
    primary_associations: list[dict[str, Any]] = []
    for ipma in ipma_boxes:
        version, flags, p = _fullbox(data, ipma)
        entry_count = _u32(data, p)
        p += 4
        large_indices = bool(flags & 1)
        for _ in range(entry_count):
            item_id = _u16(data, p) if version < 1 else _u32(data, p)
            p += 2 if version < 1 else 4
            association_count = data[p]
            p += 1
            for _ in range(association_count):
                if large_indices:
                    raw = _u16(data, p)
                    p += 2
                    essential = bool(raw & 0x8000)
                    property_index = raw & 0x7FFF
                else:
                    raw = data[p]
                    p += 1
                    essential = bool(raw & 0x80)
                    property_index = raw & 0x7F
                if property_index == 0:
                    continue
                if property_index > len(property_boxes):
                    raise ValidationError(f"ipma references out-of-range property index {property_index}.")
                if item_id == primary_id:
                    prop = property_boxes[property_index - 1]
                    primary_associations.append({
                        "index": property_index,
                        "type": prop["type"],
                        "essential": essential,
                        "payload": data[prop["payload_start"] : prop["payload_end"]],
                    })
        if p != ipma["payload_end"]:
            raise ValidationError("ipma entry bytes do not consume the declared box payload.")

    colr: list[dict[str, Any]] = []
    hvc_configurations: list[dict[str, Any]] = []
    orientations: list[dict[str, Any]] = []
    dimensions: list[dict[str, int]] = []
    for association in primary_associations:
        payload = association["payload"]
        kind = association["type"]
        if kind == "colr":
            if len(payload) < 4:
                raise ValidationError("Truncated primary-associated colr property.")
            color_type = payload[:4].decode("latin-1")
            entry: dict[str, Any] = {"propertyIndex": association["index"], "type": color_type}
            if color_type in ("prof", "rICC"):
                icc = payload[4:]
                if not icc:
                    raise ValidationError("Primary-associated ICC property has an empty payload.")
                entry.update({"bytes": len(icc), "sha256": hashlib.sha256(icc).hexdigest(), "iccBytes": icc})
            elif color_type == "nclx":
                if len(payload) < 11:
                    raise ValidationError("Truncated nclx property.")
                entry["nclx"] = {
                    "primaries": _u16(payload, 4),
                    "transfer": _u16(payload, 6),
                    "matrix": _u16(payload, 8),
                    "fullRange": bool(payload[10] & 0x80),
                }
            else:
                entry["unsupportedColorType"] = True
            colr.append(entry)
        elif kind == "hvcC":
            if len(payload) < 19:
                raise ValidationError("Truncated primary-associated hvcC property.")
            depth_luma = 8 + (payload[17] & 0x7)
            depth_chroma = 8 + (payload[18] & 0x7)
            hvc_configurations.append({
                "propertyIndex": association["index"],
                "lumaBitDepth": depth_luma,
                "chromaBitDepth": depth_chroma,
            })
        elif kind == "ispe":
            if len(payload) < 12:
                raise ValidationError("Truncated primary-associated ispe property.")
            dimensions.append({"width": _u32(payload, 4), "height": _u32(payload, 8)})
        elif kind in ("irot", "imir"):
            orientations.append({"type": kind, "value": payload[-1] if payload else None})

    primary_item_type: str | None = None
    for iinf in [box for box in meta_children if box["type"] == "iinf"]:
        version, _, p = _fullbox(data, iinf)
        entry_count = _u16(data, p) if version == 0 else _u32(data, p)
        p += 2 if version == 0 else 4
        entries = _boxes(data, p, iinf["payload_end"])
        if len(entries) != entry_count:
            raise ValidationError("iinf entry count does not match infe boxes.")
        for infe in entries:
            if infe["type"] != "infe":
                raise ValidationError("iinf contains an unexpected child box.")
            infe_version, _, q = _fullbox(data, infe)
            if infe_version < 2:
                raise ValidationError(f"Unsupported infe version {infe_version}.")
            item_id = _u16(data, q)
            if infe_version == 2:
                q += 4  # item_ID + protection_index
            else:
                item_id = _u32(data, q)
                q += 6  # item_ID + protection_index
            if item_id == primary_id:
                primary_item_type = data[q : q + 4].decode("latin-1")
                break

    return {
        "primaryItemId": primary_id,
        "primaryItemType": primary_item_type,
        "primaryAssociatedColr": [
            {k: v for k, v in item.items() if k != "iccBytes"} for item in colr
        ],
        "primaryIccBytes": [item["iccBytes"] for item in colr if "iccBytes" in item],
        "primaryNclx": [item["nclx"] for item in colr if "nclx" in item],
        "primaryHvcC": hvc_configurations,
        "primaryDimensions": dimensions,
        "primaryOrientationProperties": orientations,
        "colorAssociationAmbiguous": len(colr) > 1,
        "embeddedHuaweiLivePhotoVideo": live_photo_video,
    }


def _parse_tiff_color_and_orientation(payload: bytes) -> dict[str, Any]:
    if len(payload) < 8 or payload[:2] not in (b"II", b"MM"):
        raise ValidationError("Malformed EXIF TIFF header in JPEG APP1.")
    endian = "<" if payload[:2] == b"II" else ">"
    if struct.unpack_from(endian + "H", payload, 2)[0] != 42:
        raise ValidationError("Invalid TIFF byte-order marker in JPEG EXIF.")
    ifd0 = struct.unpack_from(endian + "I", payload, 4)[0]
    if ifd0 + 2 > len(payload):
        raise ValidationError("EXIF IFD0 offset is outside its APP1 segment.")

    def entries_at(offset: int) -> dict[int, tuple[int, int, int, bytes]]:
        count = struct.unpack_from(endian + "H", payload, offset)[0]
        table = offset + 2
        if table + count * 12 + 4 > len(payload):
            raise ValidationError("EXIF directory runs past its APP1 segment.")
        out: dict[int, tuple[int, int, int, bytes]] = {}
        for i in range(count):
            p = table + i * 12
            tag, typ = struct.unpack_from(endian + "HH", payload, p)
            n = struct.unpack_from(endian + "I", payload, p + 4)[0]
            value = struct.unpack_from(endian + "I", payload, p + 8)[0]
            out[tag] = (typ, n, value, payload[p + 8 : p + 12])
        return out

    ifd0_entries = entries_at(ifd0)
    orientation = None
    if 0x0112 in ifd0_entries:
        typ, count, _, inline_value = ifd0_entries[0x0112]
        if typ != 3 or count != 1:
            raise ValidationError("EXIF Orientation has an unexpected type/count.")
        orientation = struct.unpack_from(endian + "H", inline_value, 0)[0]
    color_space = None
    exif_pointer = ifd0_entries.get(0x8769)
    if exif_pointer:
        typ, count, offset, _ = exif_pointer
        if typ != 4 or count != 1 or offset + 2 > len(payload):
            raise ValidationError("EXIF ExifIFD pointer is malformed.")
        exif_entries = entries_at(offset)
        color = exif_entries.get(0xA001)
        if color:
            typ, count, _, inline_value = color
            if typ != 3 or count != 1:
                raise ValidationError("EXIF ColorSpace has an unexpected type/count.")
            color_space = struct.unpack_from(endian + "H", inline_value, 0)[0]
    return {"orientation": orientation, "colorSpace": color_space}


def parse_jpeg(data: bytes) -> dict[str, Any]:
    if not data.startswith(b"\xFF\xD8"):
        raise ValidationError("JPEG sample does not begin with SOI.")
    pos = 2
    marker_counts: dict[str, int] = {}
    icc_segments: dict[int, bytes] = {}
    icc_count: int | None = None
    dimensions: dict[str, int] | None = None
    exif_facts: list[dict[str, Any]] = []
    xmp_color_declarations: list[dict[str, str]] = []
    xmp_color_processing_fields: list[dict[str, str]] = []
    xmp_extensions: list[dict[str, Any]] = []
    adobe_transforms: list[int] = []
    scan_count = 0
    found_eoi = False
    in_entropy = False
    while pos < len(data):
        if in_entropy:
            marker_start = data.find(b"\xFF", pos)
            if marker_start < 0:
                raise ValidationError("JPEG entropy-coded scan has no terminating marker.")
            q = marker_start + 1
            while q < len(data) and data[q] == 0xFF:
                q += 1
            if q >= len(data):
                raise ValidationError("Truncated marker after JPEG entropy-coded scan.")
            code = data[q]
            if code == 0x00 or 0xD0 <= code <= 0xD7:
                pos = q + 1
                continue
            in_entropy = False
            pos = marker_start
        if data[pos] != 0xFF:
            raise ValidationError(f"JPEG marker sync lost at offset {pos}.")
        while pos < len(data) and data[pos] == 0xFF:
            pos += 1
        if pos >= len(data):
            raise ValidationError("Truncated JPEG marker.")
        marker = data[pos]
        pos += 1
        marker_name = f"FF{marker:02X}"
        marker_counts[marker_name] = marker_counts.get(marker_name, 0) + 1
        if marker == 0xD9:
            found_eoi = True
            if pos != len(data):
                raise ValidationError("Unexpected bytes follow the JPEG EOI marker.")
            break
        if marker in (0x01, *range(0xD0, 0xD8)):
            continue
        if pos + 2 > len(data):
            raise ValidationError("Truncated JPEG segment length.")
        segment_length = _u16(data, pos)
        if segment_length < 2 or pos + segment_length > len(data):
            raise ValidationError(f"Invalid JPEG segment length for {marker_name}.")
        segment = data[pos + 2 : pos + segment_length]
        pos += segment_length
        if 0xC0 <= marker <= 0xCF and marker not in (0xC4, 0xC8, 0xCC):
            if len(segment) < 6:
                raise ValidationError("Truncated JPEG SOF segment.")
            dimensions = {"bitDepth": segment[0], "height": _u16(segment, 1), "width": _u16(segment, 3)}
        elif marker == 0xE2 and segment.startswith(b"ICC_PROFILE\x00"):
            if len(segment) < 14:
                raise ValidationError("Truncated JPEG ICC_PROFILE APP2 header.")
            sequence, count = segment[12], segment[13]
            if sequence == 0 or count == 0 or sequence > count or (icc_count is not None and icc_count != count):
                raise ValidationError("Invalid or inconsistent JPEG ICC_PROFILE sequence numbers.")
            icc_count = count
            if sequence in icc_segments:
                raise ValidationError(f"Duplicate JPEG ICC_PROFILE sequence {sequence}.")
            icc_segments[sequence] = segment[14:]
        elif marker == 0xE1 and segment.startswith(b"Exif\x00\x00"):
            exif_facts.append(_parse_tiff_color_and_orientation(segment[6:]))
        elif marker == 0xE1 and segment.startswith(b"http://ns.adobe.com/xap/1.0/\x00"):
            xml_bytes = segment[len(b"http://ns.adobe.com/xap/1.0/\x00") :]
            try:
                root = ET.fromstring(xml_bytes)
            except ET.ParseError as ex:
                raise ValidationError("Malformed JPEG XMP APP1 packet.") from ex
            for element in root.iter():
                expanded_name = element.tag
                local = expanded_name.rsplit("}", 1)[-1].lower()
                value = (element.text or "").strip()
                if local in XMP_COLOR_INTERPRETATION_NAMES:
                    xmp_color_declarations.append({"name": expanded_name, "value": value})
                elif any(token in local for token in ("color", "colour", "primar", "transfer", "profile", "gamma")):
                    xmp_color_processing_fields.append({"name": expanded_name, "value": value})
                for attribute, attribute_value in element.attrib.items():
                    expanded_attribute = attribute
                    attribute_name = attribute.rsplit("}", 1)[-1].lower()
                    attribute_text = attribute_value.strip()
                    if attribute_name in XMP_COLOR_INTERPRETATION_NAMES:
                        xmp_color_declarations.append({"name": expanded_attribute, "value": attribute_text})
                    elif any(token in attribute_name for token in ("color", "colour", "primar", "transfer", "profile", "gamma")):
                        xmp_color_processing_fields.append({"name": expanded_attribute, "value": attribute_text})
        elif marker == 0xE1 and segment.startswith(b"http://ns.adobe.com/xmp/extension/\x00"):
            # Extended XMP is intentionally not reconstructed by this preflight
            # parser. Record it and fail closed for the unprofiled sample.
            xmp_extensions.append({"bytes": len(segment), "sha256": hashlib.sha256(segment).hexdigest()})
        elif marker == 0xEE and segment.startswith(b"Adobe") and len(segment) >= 12:
            adobe_transforms.append(segment[11])
        if marker == 0xDA:
            scan_count += 1
            in_entropy = True

    if not found_eoi:
        raise ValidationError("JPEG has no EOI marker.")
    if dimensions is None:
        raise ValidationError("JPEG has no supported SOF segment.")
    icc_bytes = None
    if icc_segments:
        if icc_count is None or set(icc_segments) != set(range(1, icc_count + 1)):
            raise ValidationError("JPEG ICC_PROFILE sequence is incomplete.")
        icc_bytes = b"".join(icc_segments[i] for i in range(1, icc_count + 1))
    if len(exif_facts) > 1:
        raise ValidationError("JPEG contains multiple EXIF APP1 packets; color/orientation authority is ambiguous.")
    return {
        "dimensions": dimensions,
        "iccBytes": icc_bytes,
        "iccBytesLength": len(icc_bytes) if icc_bytes is not None else 0,
        "iccSha256": hashlib.sha256(icc_bytes).hexdigest() if icc_bytes is not None else None,
        "iccSegmentCount": len(icc_segments),
        "exif": exif_facts[0] if exif_facts else {"orientation": None, "colorSpace": None},
        "xmpColorInterpretationDeclarations": xmp_color_declarations,
        "xmpColorProcessingFields": xmp_color_processing_fields,
        "xmpExtensions": xmp_extensions,
        "adobeTransforms": adobe_transforms,
        "scanCount": scan_count,
        "markerCounts": marker_counts,
    }


def _run(command: list[str]) -> tuple[subprocess.CompletedProcess[str], dict[str, Any]]:
    completed = subprocess.run(command, check=False, capture_output=True, text=True, errors="replace")
    return completed, {"argv": command, "exitCode": completed.returncode, "stdout": completed.stdout, "stderr": completed.stderr}


def _write_report(args: argparse.Namespace, report: dict[str, Any]) -> None:
    path = Path(args.workspace).resolve() / "preflight-report.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def _version(command: list[str], expected: str, label: str) -> dict[str, Any]:
    result, record = _run(command)
    if result.returncode != 0:
        raise ValidationError(f"Could not query {label} version: {result.stderr.strip()}")
    line = next((line.strip() for line in result.stdout.splitlines() if line.strip()), "")
    if expected not in line:
        raise ValidationError(f"Expected {label} {expected}, received: {line}")
    record["versionLine"] = line
    return record


def _find_tool(cli_path: str | None, env_name: str, names: tuple[str, ...]) -> str:
    candidate = cli_path or os.environ.get(env_name)
    if candidate:
        resolved = str(Path(candidate).resolve())
        if Path(resolved).is_file():
            return resolved
        raise ValidationError(f"Configured {env_name} does not exist: {candidate}")
    for name in names:
        found = shutil.which(name)
        if found:
            return str(Path(found).resolve())
    raise ValidationError(f"Could not resolve {env_name}; supply its executable path explicitly.")


def _ffprobe_file(ffprobe: str, path: Path) -> dict[str, Any]:
    result, record = _run([
        ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)
    ])
    if result.returncode != 0:
        raise ValidationError(f"FFprobe failed for {path.name}: {result.stderr.strip()}")
    try:
        parsed = json.loads(result.stdout)
    except json.JSONDecodeError as ex:
        raise ValidationError(f"FFprobe returned malformed JSON for {path.name}.") from ex
    return {"record": record, "streams": parsed.get("streams", []), "format": parsed.get("format", {})}


def _select_primary_stream(streams: list[dict[str, Any]], item_id: int | None) -> dict[str, Any]:
    if not streams:
        raise ValidationError("Independent FFprobe found no video/image stream.")
    if item_id is None:
        if len(streams) != 1:
            raise ValidationError(f"JPEG decode is ambiguous: FFprobe found {len(streams)} streams.")
        return streams[0]
    matches = []
    for stream in streams:
        stream_id = stream.get("id")
        try:
            numeric_id = int(stream_id, 0) if isinstance(stream_id, str) else int(stream_id)
        except (TypeError, ValueError):
            continue
        if numeric_id == item_id:
            matches.append(stream)
    if len(matches) != 1:
        raise ValidationError(
            f"Cannot uniquely map FFprobe stream id to HEIF pitm item {item_id}; found {len(matches)} matches."
        )
    return matches[0]


def _decode_to_null(ffmpeg: str, path: Path, stream: dict[str, Any]) -> dict[str, Any]:
    index = stream.get("index")
    if not isinstance(index, int):
        raise ValidationError(f"FFprobe stream for {path.name} has no numeric index.")
    result, record = _run([
        ffmpeg, "-hide_banner", "-v", "error", "-noautorotate", "-i", str(path),
        "-map", f"0:{index}", "-frames:v", "1", "-f", "null", "-",
    ])
    if result.returncode != 0:
        raise ValidationError(f"FFmpeg primary decode failed for {path.name}: {result.stderr.strip()}")
    return record


def _render_primary_rgb24(
    ffmpeg: str,
    path: Path,
    stream: dict[str, Any],
    workspace: Path,
    sample_id: str,
    render_directory: str = "preflight-renders",
) -> dict[str, Any]:
    index = stream.get("index")
    width, height = stream.get("width"), stream.get("height")
    if not isinstance(index, int) or not isinstance(width, int) or not isinstance(height, int):
        raise ValidationError(f"FFprobe primary stream for {path.name} lacks index/dimensions.")
    command = [
        ffmpeg, "-hide_banner", "-v", "error", "-noautorotate", "-i", str(path),
        "-map", f"0:{index}", "-frames:v", "1", "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1",
    ]
    completed = subprocess.run(command, check=False, capture_output=True)
    record = {
        "argv": command,
        "exitCode": completed.returncode,
        "stderr": completed.stderr.decode("utf-8", errors="replace"),
        "stdoutBytes": len(completed.stdout),
        "width": width,
        "height": height,
        "pixelFormat": "rgb24",
        "sha256": hashlib.sha256(completed.stdout).hexdigest().upper(),
    }
    expected_bytes = width * height * 3
    if completed.returncode != 0:
        raise ValidationError(f"FFmpeg RGB24 render failed for {path.name}: {record['stderr'].strip()}")
    if len(completed.stdout) != expected_bytes:
        raise ValidationError(
            f"FFmpeg RGB24 output length mismatch for {path.name}: {len(completed.stdout)} != {expected_bytes}."
        )

    render_dir = workspace / render_directory
    render_dir.mkdir(parents=True, exist_ok=True)
    render_path = render_dir / f"{sample_id}.rgb24"
    try:
        with render_path.open("xb") as output:
            output.write(completed.stdout)
            output.flush()
            os.fsync(output.fileno())
    except FileExistsError:
        previous_hash = hashlib.sha256(render_path.read_bytes()).hexdigest().upper()
        if previous_hash != record["sha256"]:
            raise ValidationError(
                f"Existing deterministic render differs; preserved without overwrite: {render_path}"
            )
        record["existingArtifactReused"] = True
    record["workspaceArtifact"] = str(render_path.relative_to(Path.cwd().resolve()))
    return record


def _decode_heif_primary_png(
    heif_convert: str,
    path: Path,
    workspace: Path,
    sample_id: str,
    source_sha256: str,
    structure: dict[str, Any],
) -> dict[str, Any]:
    render_dir = workspace / "preflight-renders"
    render_dir.mkdir(parents=True, exist_ok=True)
    png_path = render_dir / f"{sample_id}.heif-primary.png"
    command = [heif_convert, "--quiet", "--output", str(png_path), str(path)]
    if png_path.exists():
        current_output_hash = hashlib.sha256(png_path.read_bytes()).hexdigest().upper()
        matching_evidence: tuple[str, dict[str, Any]] | None = None
        for evidence_path, evidence in _PRIOR_RENDER_REPORTS:
            prior_sample = evidence.get("sampleReports", {}).get(sample_id, {})
            prior_decode = prior_sample.get("libheifPrimaryDecode", {})
            prior_conversion = prior_decode.get("conversion", {})
            prior_version = evidence.get("versions", {}).get("heifConvert", {})
            if (
                prior_sample.get("sha256", "").upper() == source_sha256.upper()
                and prior_decode.get("outputSha256", "").upper() == current_output_hash
                and str(prior_version.get("versionLine", "")) == EXPECTED_TOOLS["heifConvert"]
                and prior_conversion.get("exitCode") == 0
            ):
                matching_evidence = (evidence_path, prior_conversion)
                break
        if matching_evidence is None:
            raise ValidationError(
                f"Existing libheif PNG has no matching successful decoder report; preserved without overwrite: {png_path}"
            )
        evidence_path, prior_conversion = matching_evidence
        conversion = dict(prior_conversion)
        conversion["reusedExistingOutput"] = True
        conversion["reusedFromReport"] = evidence_path
    else:
        result, conversion = _run(command)
        if result.returncode != 0:
            raise ValidationError(f"libheif primary decode failed for {path.name}: {result.stderr.strip()}")
    if not png_path.is_file():
        raise ValidationError(f"libheif primary decoder did not create {png_path}.")

    expected = structure.get("primaryDimensions", [])
    if len(expected) != 1:
        raise ValidationError(f"Cannot validate libheif primary dimensions for {sample_id}.")
    try:
        with Image.open(png_path) as image:
            image.load()
            icc_profile = image.info.get("icc_profile")
            if image.format != "PNG" or image.mode != "RGB":
                raise ValidationError(f"libheif primary decode must yield PNG/RGB; got {image.format}/{image.mode}.")
            if image.size != (expected[0]["width"], expected[0]["height"]):
                raise ValidationError(f"libheif default primary dimensions do not match pitm/ispe for {sample_id}.")
            if not icc_profile:
                raise ValidationError(f"libheif primary PNG omitted the ICC payload required for {sample_id}.")
            expected_icc = structure.get("primaryIccBytes", [])
            if len(expected_icc) != 1 or icc_profile != expected_icc[0]:
                raise ValidationError(f"libheif primary PNG ICC bytes do not exactly match the independently parsed primary ICC for {sample_id}.")
            pixels = image.tobytes()
            width, height, mode = image.width, image.height, image.mode
    except OSError as ex:
        raise ValidationError(f"Could not read libheif primary PNG for {sample_id}: {ex}") from ex
    return {
        "conversion": conversion,
        "workspaceArtifact": str(png_path.relative_to(Path.cwd().resolve())),
        "outputSha256": hashlib.sha256(png_path.read_bytes()).hexdigest().upper(),
        "width": width,
        "height": height,
        "mode": mode,
        "decodedRgbBytes": len(pixels),
        "decodedRgbSha256": hashlib.sha256(pixels).hexdigest().upper(),
        "iccBytes": len(icc_profile),
        "iccSha256": hashlib.sha256(icc_profile).hexdigest().upper(),
        "selectedItemRule": "libheif heif-convert default primary image; dimensions and ICC bytes matched to independently parsed pitm/item property graph",
    }


def _exiftool(exiftool: str, path: Path) -> dict[str, Any]:
    result, record = _run([
        exiftool, "-json", "-G1", "-a", "-s", "-FileType", "-ImageWidth", "-ImageHeight", "-Orientation",
        "-BitDepth", "-ColorSpace", "-ColorSpaceData", "-Gamma", "-ColorPrimaries",
        "-TransferCharacteristics", "-MatrixCoefficients", "-ICC_Profile:ProfileDescription", str(path),
    ])
    if result.returncode != 0:
        raise ValidationError(f"ExifTool failed for {path.name}: {result.stderr.strip()}")
    try:
        items = json.loads(result.stdout)
    except json.JSONDecodeError as ex:
        raise ValidationError(f"ExifTool returned malformed JSON for {path.name}.") from ex
    if len(items) != 1:
        raise ValidationError(f"ExifTool returned {len(items)} records for {path.name}.")
    record["facts"] = items[0]
    return record


def _sample_path(root: Path, entry: dict[str, Any]) -> Path:
    path = (root / entry["cachePath"]).resolve()
    if not path.is_file():
        raise ValidationError(f"Missing cached RealSample {entry['id']}: {path}")
    expected_prefix = (root / ".ai-tmp/cache/samples/P5-R4-v1").resolve()
    if expected_prefix not in path.parents:
        raise ValidationError(f"R4 sample escaped deterministic cache directory: {path}")
    return path


def _identity_orientation(metadata: dict[str, Any], container: str, structure: dict[str, Any], sample_id: str) -> None:
    facts = metadata["facts"]
    orientation_values = [value for key, value in facts.items() if "orientation" in key.lower()]
    for value in orientation_values:
        normalized = str(value).strip().lower()
        if normalized not in ("1", "horizontal (normal)", "top-left"):
            raise ValidationError(f"ExifTool reports non-identity/unknown Orientation for {sample_id}: {value!r}.")
    if container == "JPEG":
        value = structure["exif"].get("orientation")
        if value not in (None, 1):
            raise ValidationError(f"Independent JPEG EXIF parser reports non-identity Orientation for {sample_id}: {value}.")
    elif structure.get("primaryOrientationProperties"):
        raise ValidationError(f"HEIF primary has irot/imir properties; identity-only render route blocked for {sample_id}.")


def _local_name(element: ET.Element) -> str:
    return element.tag.rsplit("}", 1)[-1]


def _parse_utc(value: str, label: str) -> datetime:
    try:
        parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as ex:
        raise ValidationError(f"TRX {label} timestamp is malformed: {value!r}.") from ex
    if parsed.tzinfo is None:
        raise ValidationError(f"TRX {label} timestamp has no timezone: {value!r}.")
    return parsed.astimezone(timezone.utc)


def _safe_child(root: Path, relative: str, label: str, must_exist: bool = True) -> Path:
    root = root.resolve()
    candidate = Path(os.path.abspath(root / relative))
    if candidate == root or root not in candidate.parents:
        raise ValidationError(f"{label} escaped its configured workspace: {candidate}.")
    current = root
    for component in candidate.relative_to(root).parts:
        current = current / component
        path = current
        if not path.exists() and not path.is_symlink():
            if must_exist and path == candidate:
                raise ValidationError(f"{label} is missing: {path}.")
            continue
        try:
            details = path.lstat()
        except OSError as ex:
            raise ValidationError(f"Could not inspect {label}: {path}: {ex}.") from ex
        if path.is_symlink() or getattr(details, "st_file_attributes", 0) & 0x400:
            raise ValidationError(f"{label} traverses a symbolic link or reparse point: {path}.")
    resolved = candidate.resolve(strict=False)
    if resolved == root or root not in resolved.parents:
        raise ValidationError(f"{label} resolves outside its configured workspace: {resolved}.")
    if must_exist and not resolved.exists():
        raise ValidationError(f"{label} is missing: {resolved}.")
    return resolved


def _read_current_test_run(root: Path, workspace: Path, test_results_arg: str) -> dict[str, Any]:
    results_root = _safe_child(root, str(workspace.relative_to(root) / "test-results"), "R4 test results directory")
    result_path = _safe_child(root, test_results_arg, "R4 TRX test result")
    if results_root not in result_path.parents or not result_path.is_file():
        raise ValidationError(f"R4 TRX must be a regular file inside {results_root}: {result_path}.")
    try:
        raw = result_path.read_bytes()
        xml_root = ET.fromstring(raw)
    except (OSError, ET.ParseError) as ex:
        raise ValidationError(f"Could not read the R4 TRX test run {result_path}: {ex}.") from ex

    times = [item for item in xml_root.iter() if _local_name(item) == "Times"]
    counters = [item for item in xml_root.iter() if _local_name(item) == "Counters"]
    if len(times) != 1 or len(counters) != 1:
        raise ValidationError("R4 TRX must contain exactly one run Times record and one summary Counters record.")
    start = _parse_utc(times[0].get("start", ""), "start")
    finish = _parse_utc(times[0].get("finish", ""), "finish")
    if finish < start:
        raise ValidationError("R4 TRX run finishes before it starts.")
    count_fields = (
        "total", "executed", "passed", "failed", "error", "timeout", "aborted",
        "inconclusive", "passedButRunAborted", "notRunnable", "notExecuted", "disconnected",
        "warning", "inProgress", "pending",
    )
    counts: dict[str, int] = {}
    for field in count_fields:
        raw_value = counters[0].get(field, "0")
        try:
            counts[field] = int(raw_value)
        except ValueError as ex:
            raise ValidationError(f"R4 TRX counter {field} is not an integer: {raw_value!r}.") from ex
    if counts["total"] != len(EXPECTED_TESTS) or counts["executed"] != len(EXPECTED_TESTS) or counts["passed"] != len(EXPECTED_TESTS):
        raise ValidationError(f"R4 TRX must show all seven filtered tests executed and passed: {counts}.")
    if any(counts[field] != 0 for field in count_fields if field not in ("total", "executed", "passed", "completed")):
        raise ValidationError(f"R4 TRX contains a failure, skip, abort, warning, or pending test: {counts}.")
    if counts["inProgress"] != 0 or counts["pending"] != 0:
        raise ValidationError(f"R4 TRX still has in-progress or pending tests: {counts}.")

    results = [item for item in xml_root.iter() if _local_name(item) == "UnitTestResult"]
    observed: dict[str, str] = {}
    for result in results:
        name = result.get("testName", "").rsplit(".", 1)[-1].split("(", 1)[0]
        if not name:
            raise ValidationError("R4 TRX contains a result with no testName.")
        if name in observed:
            raise ValidationError(f"R4 TRX contains duplicate result rows for {name}.")
        observed[name] = result.get("outcome", "")
    if set(observed) != EXPECTED_TESTS or any(outcome.lower() != "passed" for outcome in observed.values()):
        raise ValidationError(f"R4 TRX result set differs from the seven frozen test methods: {observed}.")

    return {
        "path": str(result_path.relative_to(root)),
        "sha256": hashlib.sha256(raw).hexdigest().upper(),
        "size": len(raw),
        "startUtc": start.isoformat().replace("+00:00", "Z"),
        "finishUtc": finish.isoformat().replace("+00:00", "Z"),
        "counts": counts,
        "testResults": [{"name": name, "outcome": observed[name]} for name in sorted(observed)],
        "zeroSkip": True,
    }


def _route_output_files(
    root: Path,
    test_workspace: Path,
    run: dict[str, Any],
) -> dict[str, list[dict[str, Any]]]:
    output_root = _safe_child(root, str(test_workspace.relative_to(root) / "outputs"), "R4 route output root")
    start = _parse_utc(run["startUtc"], "start").timestamp()
    finish = _parse_utc(run["finishUtc"], "finish").timestamp()
    outputs: dict[str, list[dict[str, Any]]] = {}
    for directory_name, (expected_count, expected_extension) in EXPECTED_OUTPUT_LAYOUT.items():
        route_dir = _safe_child(root, str(output_root.relative_to(root) / directory_name), f"R4 route directory {directory_name}")
        if not route_dir.is_dir():
            raise ValidationError(f"R4 route output path is not a directory: {route_dir}.")
        entries = list(route_dir.iterdir())
        for item in entries:
            details = item.lstat()
            if item.is_symlink() or getattr(details, "st_file_attributes", 0) & 0x400 or not item.is_file():
                raise ValidationError(f"R4 route output contains a directory, link, or non-file entry: {item}.")
        if len(entries) != expected_count:
            raise ValidationError(
                f"R4 route {directory_name} must contain exactly {expected_count} artifact(s); found {len(entries)}."
            )
        artifacts: list[dict[str, Any]] = []
        for path in sorted(entries, key=lambda item: item.name.lower()):
            if path.suffix.lower() != expected_extension:
                raise ValidationError(f"R4 route {directory_name} produced an unexpected extension: {path.name}.")
            modified = path.stat().st_mtime
            if modified < start - 1.0 or modified > finish + 1.0:
                raise ValidationError(
                    f"R4 route artifact is stale or post-run: {path} has mtime {modified}, test run was {start}..{finish}."
                )
            data = path.read_bytes()
            artifacts.append({
                "path": str(path.relative_to(root)),
                "byteSize": len(data),
                "sha256": hashlib.sha256(data).hexdigest().upper(),
                "lastWriteTimeUtc": datetime.fromtimestamp(modified, timezone.utc).isoformat().replace("+00:00", "Z"),
            })
        outputs[directory_name] = artifacts
    return outputs


def _materialized_huawei_primary(root: Path, test_workspace: Path, run: dict[str, Any], source: Path,
    expected_start: int, expected_end: int) -> tuple[Path, bytes, dict[str, Any]]:
    materialized_root = _safe_child(
        root, str(test_workspace.relative_to(root) / "materialized" / "huawei-neutral-primary"),
        "Huawei materialization directory",
    )
    start_time = _parse_utc(run["startUtc"], "start").timestamp()
    finish_time = _parse_utc(run["finishUtc"], "finish").timestamp()
    candidates = []
    for path in materialized_root.glob("primary-*.heic"):
        details = path.lstat()
        if path.is_symlink() or getattr(details, "st_file_attributes", 0) & 0x400 or not path.is_file():
            raise ValidationError(f"Huawei materialized primary is not a regular file: {path}.")
        if start_time - 1.0 <= path.stat().st_mtime <= finish_time + 1.0:
            candidates.append(path)
    if len(candidates) != 1:
        raise ValidationError(f"Expected exactly one Huawei primary materialized by this test run; found {len(candidates)}.")
    path = candidates[0]
    with source.open("rb") as source_stream:
        source_stream.seek(expected_start)
        expected_length = expected_end - expected_start
        expected_bytes = source_stream.read(expected_length)
    if len(expected_bytes) != expected_length:
        raise ValidationError("Huawei source did not provide the complete manifest-bound primary byte range.")
    materialized = path.read_bytes()
    if materialized != expected_bytes:
        raise ValidationError("Materialized Huawei HEIC primary is not byte-identical to the source primary range.")
    report = {
        "path": str(path.relative_to(root)),
        "sourcePath": str(source.relative_to(root)),
        "sourceRange": {"startInclusive": expected_start, "endExclusive": expected_end, "byteLength": expected_length},
        "byteSize": len(materialized),
        "sha256": hashlib.sha256(materialized).hexdigest().upper(),
        "sourceRangeSha256": hashlib.sha256(expected_bytes).hexdigest().upper(),
        "byteIdenticalToSourceRange": True,
    }
    return path, materialized, report


def _unknown_hevc_color_fields(stream: dict[str, Any], route_name: str) -> dict[str, Any]:
    color = {key: stream.get(key) for key in ("color_primaries", "color_transfer", "color_space")}
    for key, value in color.items():
        if value not in (None, "") and str(value).strip().lower() != "unknown":
            raise ValidationError(f"{route_name} HEVC VUI invents {key}={value!r}.")
    return color


def _primary_dimensions(structure: dict[str, Any], label: str) -> tuple[int, int]:
    dimensions = structure.get("primaryDimensions", [])
    if len(dimensions) != 1:
        raise ValidationError(f"{label} must have exactly one independently associated primary ispe.")
    width, height = dimensions[0].get("width"), dimensions[0].get("height")
    if not isinstance(width, int) or not isinstance(height, int) or width <= 0 or height <= 0:
        raise ValidationError(f"{label} has invalid independent primary dimensions: {dimensions[0]!r}.")
    return width, height


def _decode_formal_heif_primary_png(
    heif_convert: str,
    path: Path,
    workspace: Path,
    route_name: str,
    source_sha256: str,
    run_sha256: str,
    structure: dict[str, Any],
) -> tuple[bytes, tuple[int, int], dict[str, Any]]:
    render_dir = workspace / "formal-renders"
    render_dir.mkdir(parents=True, exist_ok=True)
    png_path = render_dir / f"{route_name}-{source_sha256[:16].lower()}-{run_sha256[:12].lower()}.heif-primary.png"
    if png_path.exists() or png_path.is_symlink():
        raise ValidationError(f"Content-addressed formal HEIF render already exists; preserving it: {png_path}.")
    command = [heif_convert, "--quiet", "--output", str(png_path), str(path)]
    result, conversion = _run(command)
    if result.returncode != 0:
        raise ValidationError(f"Pinned libheif primary decode failed for {path.name}: {result.stderr.strip()}")
    if not png_path.is_file() or png_path.is_symlink():
        raise ValidationError(f"Pinned libheif primary decoder did not create a regular PNG: {png_path}.")
    expected_size = _primary_dimensions(structure, route_name)
    try:
        with Image.open(png_path) as image:
            image.load()
            if image.format != "PNG" or image.mode != "RGB":
                raise ValidationError(f"Pinned libheif decode must yield PNG/RGB, got {image.format}/{image.mode}.")
            if image.size != expected_size:
                raise ValidationError(f"Pinned libheif primary dimensions differ from pitm/ispe for {route_name}.")
            pixels = image.tobytes()
            profile = image.info.get("icc_profile")
    except OSError as ex:
        raise ValidationError(f"Could not read pinned libheif PNG for {route_name}: {ex}.") from ex
    expected_profiles = structure.get("primaryIccBytes", [])
    if len(expected_profiles) != 1 or not profile or profile != expected_profiles[0]:
        raise ValidationError(f"Pinned libheif PNG ICC bytes differ from the independent primary association for {route_name}.")
    output_hash = hashlib.sha256(png_path.read_bytes()).hexdigest().upper()
    record = {
        "conversion": conversion,
        "workspaceArtifact": str(png_path.relative_to(Path.cwd().resolve())),
        "outputSha256": output_hash,
        "width": expected_size[0],
        "height": expected_size[1],
        "mode": "RGB",
        "decodedRgbBytes": len(pixels),
        "decodedRgbSha256": hashlib.sha256(pixels).hexdigest().upper(),
        "iccBytes": len(profile),
        "iccSha256": hashlib.sha256(profile).hexdigest().upper(),
        "selectedItemRule": "heif-convert default primary; dimensions and ICC bytes match the independent pitm/item property graph",
    }
    return pixels, expected_size, record


def _icc_to_srgb(image: Image.Image, icc_bytes: bytes, label: str) -> Image.Image:
    if image.mode != "RGB" or not icc_bytes:
        raise ValidationError(f"{label} requires decoded RGB and one complete source ICC profile.")
    try:
        source_profile = ImageCms.ImageCmsProfile(io.BytesIO(icc_bytes))
        destination_profile = ImageCms.createProfile("sRGB")
        return ImageCms.profileToProfile(
            image,
            source_profile,
            destination_profile,
            renderingIntent=ImageCms.Intent.RELATIVE_COLORIMETRIC,
            outputMode="RGB",
            flags=0,
        )
    except (ImageCms.PyCMSError, OSError, ValueError) as ex:
        raise ValidationError(f"LittleCMS could not perform the frozen single ICC-to-sRGB conversion for {label}: {ex}.") from ex


def _compare_profiled_renders(
    source_rgb: bytes,
    source_size: tuple[int, int],
    target_rgb: bytes,
    target_size: tuple[int, int],
    icc_bytes: bytes,
    thresholds: dict[str, float],
    label: str,
) -> dict[str, Any]:
    if source_size != target_size:
        raise ValidationError(f"{label} source/target dimensions differ before any resize: {source_size} != {target_size}.")
    expected_bytes = source_size[0] * source_size[1] * 3
    if len(source_rgb) != expected_bytes or len(target_rgb) != expected_bytes:
        raise ValidationError(f"{label} RGB24 byte counts do not match the exact untransformed dimensions.")
    source_image = Image.frombytes("RGB", source_size, source_rgb)
    target_image = Image.frombytes("RGB", target_size, target_rgb)
    source_srgb = _icc_to_srgb(source_image, icc_bytes, f"{label} source")
    target_srgb = _icc_to_srgb(target_image, icc_bytes, f"{label} target")
    scale = min(1.0, 1024.0 / max(source_size))
    resized_size = (max(1, round(source_size[0] * scale)), max(1, round(source_size[1] * scale)))
    if source_srgb.size != resized_size or target_srgb.size != resized_size:
        source_srgb = source_srgb.resize(resized_size, resample=Image.Resampling.LANCZOS)
        target_srgb = target_srgb.resize(resized_size, resample=Image.Resampling.LANCZOS)
    source_lab = rgb2lab(numpy.asarray(source_srgb, dtype=numpy.float32) / 255.0)
    target_lab = rgb2lab(numpy.asarray(target_srgb, dtype=numpy.float32) / 255.0)
    differences = deltaE_ciede2000(source_lab, target_lab)
    if not numpy.isfinite(differences).all():
        raise ValidationError(f"{label} CIEDE2000 comparison produced non-finite values.")
    mean = float(numpy.mean(differences, dtype=numpy.float64))
    p95, p99 = (float(value) for value in numpy.percentile(differences, [95, 99], method="linear"))
    metrics = {
        "pipelineId": EXPECTED_RENDER_PIPELINE_ID,
        "dimensionsBeforeResize": {"width": source_size[0], "height": source_size[1]},
        "dimensionsAfterResize": {"width": resized_size[0], "height": resized_size[1]},
        "resizeScale": scale,
        "resizeFilter": "Pillow Image.Resampling.LANCZOS",
        "colorManagement": "Pillow ImageCms / LittleCMS source ICC to explicit sRGB; relative-colorimetric; BPC off; exactly once per decoded image",
        "metric": "CIEDE2000 in Lab after LittleCMS conversion to sRGB",
        "iccSha256": hashlib.sha256(icc_bytes).hexdigest().upper(),
        "meanDeltaE00": mean,
        "p95DeltaE00": p95,
        "p99DeltaE00": p99,
        "thresholds": thresholds,
        "thresholdsPass": (
            mean <= thresholds["meanDeltaE00Maximum"] and
            p95 <= thresholds["p95DeltaE00Maximum"] and
            p99 <= thresholds["p99DeltaE00Maximum"]
        ),
    }
    return metrics


def _git_worktree_identity(root: Path) -> dict[str, Any]:
    def run_bytes(argv: list[str]) -> bytes:
        result = subprocess.run(argv, cwd=root, check=False, capture_output=True)
        if result.returncode != 0:
            raise ValidationError(f"Git identity command failed ({result.returncode}): {argv!r}.")
        return result.stdout

    head_bytes = run_bytes(["git", "rev-parse", "HEAD"]).strip()
    branch_bytes = run_bytes(["git", "branch", "--show-current"]).strip()
    status_bytes = run_bytes(["git", "status", "--porcelain=v1", "-z", "--untracked-files=all"])
    tracked_diff = run_bytes(["git", "diff", "--no-ext-diff", "--no-textconv", "--binary", "HEAD", "--", "."])
    untracked_bytes = run_bytes(["git", "ls-files", "--others", "--exclude-standard", "-z"])
    untracked_paths = sorted(path for path in untracked_bytes.split(b"\x00") if path)
    diff_hash = hashlib.sha256()
    diff_hash.update(b"HEAD\x00" + head_bytes + b"\x00")
    diff_hash.update(b"STATUS\x00" + status_bytes + b"\x00")
    diff_hash.update(b"TRACKED_DIFF\x00" + tracked_diff + b"\x00")
    untracked_records = []
    for raw_path in untracked_paths:
        path = root / os.fsdecode(raw_path)
        try:
            details = path.lstat()
        except OSError as ex:
            raise ValidationError(f"Untracked file disappeared while hashing the dirty diff: {path}: {ex}.") from ex
        diff_hash.update(b"UNTRACKED\x00" + raw_path + b"\x00")
        if path.is_symlink() or getattr(details, "st_file_attributes", 0) & 0x400:
            target = os.readlink(path)
            target_bytes = os.fsencode(target)
            diff_hash.update(b"SYMLINK\x00" + target_bytes + b"\x00")
            untracked_records.append({"path": os.fsdecode(raw_path), "kind": "symlink", "target": target})
        elif path.is_file():
            content = path.read_bytes()
            diff_hash.update(b"FILE\x00" + str(details.st_mode & 0xFFFF).encode("ascii") + b"\x00" + content + b"\x00")
            untracked_records.append({
                "path": os.fsdecode(raw_path),
                "kind": "file",
                "byteSize": len(content),
                "sha256": hashlib.sha256(content).hexdigest().upper(),
            })
        else:
            raise ValidationError(f"Untracked path is not a regular file or symlink: {path}.")

    status_text = run_bytes(["git", "status", "--short", "--untracked-files=all"]).decode("utf-8", errors="replace")
    return {
        "head": head_bytes.decode("ascii", errors="strict"),
        "branch": branch_bytes.decode("utf-8", errors="replace"),
        "dirtyDiffSha256": diff_hash.hexdigest().upper(),
        "gitStatusShort": status_text.splitlines(),
        "trackedDiffSha256": hashlib.sha256(tracked_diff).hexdigest().upper(),
        "untrackedFiles": untracked_records,
    }


def _new_formal_evidence_path(evidence_root: Path, test_sha256: str, dirty_sha256: str) -> Path:
    evidence_root.mkdir(parents=True, exist_ok=True)
    if evidence_root.is_symlink() or getattr(evidence_root.lstat(), "st_file_attributes", 0) & 0x400:
        raise ValidationError(f"R4 evidence directory is a link or reparse point: {evidence_root}.")
    stem = f"P5-R4-v1-formal-{test_sha256[:16].lower()}-{dirty_sha256[:16].lower()}"
    candidate = evidence_root / f"{stem}.json"
    suffix = 2
    while candidate.exists():
        candidate = evidence_root / f"{stem}-{suffix}.json"
        suffix += 1
    return candidate


def _write_formal_evidence(path: Path, report: dict[str, Any]) -> None:
    if path.exists() or path.is_symlink():
        raise ValidationError(f"Formal evidence already exists; preserving it: {path}.")
    path.parent.mkdir(parents=True, exist_ok=True)
    payload = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    with path.open("x", encoding="utf-8", newline="\n") as output:
        output.write(payload)
        output.flush()
        os.fsync(output.fileno())


def _stream_pixel_depth(stream: dict[str, Any]) -> int:
    pixel_format = str(stream.get("pix_fmt", "")).lower()
    match = re.search(r"p(10|12|14|16)(?:le|be)?$", pixel_format)
    try:
        raw_depth = int(stream.get("bits_per_raw_sample", "0"))
    except (TypeError, ValueError):
        raw_depth = 0
    return max(int(match.group(1)) if match else 0, raw_depth)


def _raw_rgb24_artifact(root: Path, render: dict[str, Any]) -> bytes:
    relative = render.get("workspaceArtifact")
    if not isinstance(relative, str):
        raise ValidationError("FFmpeg RGB24 render did not identify its fixed workspace artifact.")
    path = _safe_child(root, relative, "FFmpeg RGB24 workspace output")
    data = path.read_bytes()
    if len(data) != render.get("stdoutBytes") or hashlib.sha256(data).hexdigest().upper() != render.get("sha256"):
        raise ValidationError(f"Fixed FFmpeg RGB24 workspace output changed after decode: {path}.")
    return data


def preflight(args: argparse.Namespace) -> dict[str, Any]:
    global _LAST_REPORT, _PRIOR_RENDER_REPORTS
    root = Path.cwd().resolve()
    workspace_path = (root / args.workspace).resolve()
    _PRIOR_RENDER_REPORTS = []
    if workspace_path.exists():
        for previous_path in workspace_path.glob("*preflight*report.json"):
            try:
                previous_report = json.loads(previous_path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                continue
            _PRIOR_RENDER_REPORTS.append((str(previous_path.relative_to(root)), previous_report))
    manifest_path = (root / args.manifest).resolve()
    raw_manifest = manifest_path.read_bytes()
    manifest = json.loads(raw_manifest)
    if manifest.get("profile") != EXPECTED_PROFILE or manifest.get("schemaVersion") != 1:
        raise ValidationError("Unexpected P5-R4 manifest profile/schema version.")
    if manifest.get("roadmapRevision") != "317465937A08A908F318ED0F1005AFF7D5B41E637DFF75276F7C08732BB859A7":
        raise ValidationError("P5-R4 profile is bound to a different roadmap revision.")
    validation = manifest.get("validation", {})
    render_pipeline = validation.get("renderPipeline", {})
    if render_pipeline.get("pipelineId") != EXPECTED_RENDER_PIPELINE_ID:
        raise ValidationError("R4-v1 manifest has no recognized frozen render pipeline ID.")
    coverage_blockers = validation.get("coverageBlockers", [])
    if coverage_blockers:
        raise ValidationError(f"R4-v1 declares unresolved coverage blockers: {coverage_blockers}")

    configured_routes = {route for sample in manifest.get("samples", []) for route in sample.get("routes", [])}
    if configured_routes != EXPECTED_ROUTES:
        raise ValidationError(f"Manifest route set mismatch: {sorted(configured_routes)}")
    entries = manifest.get("samples", [])
    expected_ids = {
        "apple-p3-jpeg-p4-v1",
        "huawei-p3-heic-p4-v1",
        "nikon-z8-10bit-sdr-heic-cc0",
        "nikon-z8-unprofiled-sdr-jpeg-cc0",
    }
    if {sample.get("id") for sample in entries} != expected_ids:
        raise ValidationError("Manifest must enumerate exactly the four canonical R4-v1 samples.")

    p4_manifest = json.loads((root / "tests/fixtures/realsamples-manifest.json").read_text(encoding="utf-8"))
    by_filename = {item["filename"]: item for item in p4_manifest["samples"]}
    source_lock_path = root / validation["upstreamSourceLock"]
    try:
        source_identity = verify_offline_lock(root, manifest, source_lock_path)
    except SourceLockError as exc:
        raise ValidationError(str(exc)) from exc
    external_sources = source_identity["samples"]
    sample_reports: dict[str, Any] = {}
    tools: dict[str, str] = {}
    exiftool = _find_tool(args.exiftool, "EXIFTOOL", ("exiftool", "exiftool.exe"))
    ffmpeg = _find_tool(args.ffmpeg, "FFMPEG", ("ffmpeg", "ffmpeg.exe"))
    ffprobe = _find_tool(args.ffprobe, "FFPROBE", ("ffprobe", "ffprobe.exe"))
    heif_convert = _find_tool(args.heif_convert, "HEIF_CONVERT", ("heif-convert", "heif-convert.exe"))
    tools.update({"exiftool": exiftool, "ffmpeg": ffmpeg, "ffprobe": ffprobe, "heifConvert": heif_convert})
    versions = {
        "python": sys.version,
        "pillow": PIL.__version__,
        "littleCms": ImageCms.core.littlecms_version,
        "numpy": numpy.__version__,
        "scikitImage": skimage.__version__,
        "exiftool": _version([exiftool, "-ver"], EXPECTED_TOOLS["exiftool"], "ExifTool"),
        "ffmpeg": _version([ffmpeg, "-version"], EXPECTED_TOOLS["ffmpeg"], "FFmpeg"),
        "ffprobe": _version([ffprobe, "-version"], EXPECTED_TOOLS["ffprobe"], "FFprobe"),
        "heifConvert": _version([heif_convert, "--version"], EXPECTED_TOOLS["heifConvert"], "libheif heif-convert"),
    }
    for key, expected in EXPECTED_PYTHON.items():
        if versions[key] != expected:
            raise ValidationError(f"Expected {key} {expected}, received {versions[key]}.")

    report: dict[str, Any] = {
        "schemaVersion": 1,
        "profile": EXPECTED_PROFILE,
        "mode": "preflight",
        "status": "RUNNING",
        "argv": sys.argv,
        "manifest": str(manifest_path.relative_to(root)),
        "manifestSha256": hashlib.sha256(raw_manifest).hexdigest().upper(),
        "p4ManifestSha256": hashlib.sha256((root / "tests/fixtures/realsamples-manifest.json").read_bytes()).hexdigest().upper(),
        "upstreamSourceIdentityEvidence": {
            "path": source_identity["path"],
            "sha256": source_identity["sha256"],
            "repository": source_identity["repository"],
            "revision": source_identity["revision"],
            "verifiedSampleIds": source_identity["verifiedSampleIds"],
        },
        "tools": tools,
        "versions": versions,
        "sampleReports": sample_reports,
        "routeCoverage": sorted(configured_routes),
        "coverageBlockers": coverage_blockers,
        "independentRenderPipeline": manifest.get("validation", {}).get("renderPipeline"),
    }
    _LAST_REPORT = report
    _write_report(args, report)

    for entry in entries:
        path = _sample_path(root, entry)
        digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
        byte_size = path.stat().st_size
        if digest != entry["sha256"] or byte_size != entry["byteSize"]:
            raise ValidationError(f"Cached sample identity mismatch for {entry['id']}.")

        p4_reference = entry.get("provenance", {}).get("p4Reference")
        if p4_reference:
            if p4_reference.get("manifest") != "tests/fixtures/realsamples-manifest.json":
                raise ValidationError(f"P4 reference manifest is not the immutable P4-v1 manifest for {entry['id']}.")
            source = by_filename.get(p4_reference["filename"])
            if not source or source["sha256"].upper() != entry["sha256"].upper() or source["byteSize"] != byte_size:
                raise ValidationError(f"P4-v1 identity mismatch for {entry['id']}.")
        else:
            provenance = entry.get("provenance", {})
            if provenance.get("gitLfsObjectSha256", "").upper() != digest:
                raise ValidationError(f"Manifest does not pin the upstream LFS content hash for {entry['id']}.")
            if provenance.get("gitLfsObjectByteSize") != byte_size or not provenance.get("sourceUrl") or not provenance.get("readmeUrl") or not provenance.get("licenseUrl"):
                raise ValidationError(f"Manifest is missing the upstream source/LFS/license identity for {entry['id']}.")
            external = external_sources.get(entry["id"])
            pointer = external.get("gitLfsPointer", {}) if external else {}
            if not external or external.get("sourcePath") != provenance["sourcePath"]:
                raise ValidationError(f"Versioned upstream source lock is missing or mismatched for {entry['id']}.")
            if pointer.get("objectSha256", "").upper() != digest or pointer.get("objectByteSize") != byte_size:
                raise ValidationError(f"Versioned Git LFS object identity disagrees with the local cache for {entry['id']}.")

        data = path.read_bytes()
        if entry["container"] == "JPEG":
            structure = parse_jpeg(data)
            item_id = None
        elif entry["container"] == "HEIC":
            structure = parse_heif(data)
            item_id = structure["primaryItemId"]
        else:
            raise ValidationError(f"Unsupported manifest container {entry['container']}.")

        probe = _ffprobe_file(ffprobe, path)
        # FFmpeg 8.0.1 enumerates this Huawei grid's thumbnail tile images and
        # embedded video, but does not expose the pitm grid as an AVStream.
        # Do not map a thumbnail by ordinal; use libheif's primary-image API
        # path for that sample and retain the FFprobe inventory as cross-check.
        huawei_grid_primary = entry["id"] == "huawei-p3-heic-p4-v1" and structure.get("primaryItemType") == "grid"
        stream = None if huawei_grid_primary else _select_primary_stream(probe["streams"], item_id)
        metadata = _exiftool(exiftool, path)
        sample_report = {
            "path": str(path.relative_to(root)),
            "byteSize": byte_size,
            "sha256": digest,
            "container": entry["container"],
            "independentStructure": {k: v for k, v in structure.items() if k not in ("iccBytes", "primaryIccBytes")},
            "independentIccProfiles": [
                {"byteSize": len(profile), "sha256": hashlib.sha256(profile).hexdigest()}
                for profile in ([structure["iccBytes"]] if entry["container"] == "JPEG" and structure["iccBytes"] else
                                structure["primaryIccBytes"] if entry["container"] == "HEIC" else [])
            ],
            "ffprobe": {"streams": probe["streams"], "format": probe["format"], "probeCommand": probe["record"]["argv"]},
            "selectedPrimaryStream": stream,
            "exiftool": metadata,
            "provenance": entry.get("provenance"),
        }
        if entry["container"] == "JPEG":
            independent_dimensions = structure["dimensions"]
        else:
            dimensions = structure.get("primaryDimensions", [])
            if len(dimensions) != 1:
                raise ValidationError(f"Expected one primary-associated ispe dimension for {entry['id']}; found {len(dimensions)}.")
            independent_dimensions = dimensions[0]
        if stream is not None and (stream.get("width") != independent_dimensions.get("width") or stream.get("height") != independent_dimensions.get("height")):
            raise ValidationError(f"Independent parser and selected FFprobe stream dimensions disagree for {entry['id']}.")
        if huawei_grid_primary:
            sample_report["ffprobePrimaryMapping"] = {
                "status": "not-exposed",
                "reason": "FFmpeg 8.0.1 exposes lower-resolution HEIF tile streams and embedded video but no AVStream whose id equals the independently parsed grid pitm item; no thumbnail was selected",
                "pitmItemId": item_id,
            }
        sample_report["independentDimensions"] = independent_dimensions
        expected_facts = entry.get("expectedIndependentFacts", {})
        expected_dimensions = expected_facts.get("dimensions", {})
        if any(independent_dimensions.get(key) != value for key, value in expected_dimensions.items()) or set(expected_dimensions) != {"width", "height"}:
            raise ValidationError(f"Independent dimensions differ from the versioned R4-v1 fact lock for {entry['id']}.")
        expected_icc = expected_facts.get("iccSha256")
        actual_iccs = sample_report["independentIccProfiles"]
        if expected_icc is None:
            if actual_iccs:
                raise ValidationError(f"The versioned R4-v1 fact lock expects no ICC profile in {entry['id']}.")
        elif len(actual_iccs) != 1 or actual_iccs[0]["sha256"].upper() != expected_icc.upper() or actual_iccs[0]["byteSize"] != expected_facts.get("iccByteSize"):
            raise ValidationError(f"Primary ICC bytes differ from the versioned R4-v1 fact lock for {entry['id']}.")
        if "primaryItemId" in expected_facts and structure.get("primaryItemId") != expected_facts["primaryItemId"]:
            raise ValidationError(f"Primary item_ID differs from the versioned R4-v1 fact lock for {entry['id']}.")
        if "primaryItemType" in expected_facts and structure.get("primaryItemType") != expected_facts["primaryItemType"]:
            raise ValidationError(f"Primary item type differs from the versioned R4-v1 fact lock for {entry['id']}.")
        if "primaryColorAssociationCount" in expected_facts and len(structure.get("primaryAssociatedColr", [])) != expected_facts["primaryColorAssociationCount"]:
            raise ValidationError(f"Primary colr association count differs from the versioned R4-v1 fact lock for {entry['id']}.")
        if "primaryLumaBitDepth" in expected_facts or "primaryChromaBitDepth" in expected_facts:
            hvc = structure.get("primaryHvcC", [])
            if len(hvc) != 1 or hvc[0].get("lumaBitDepth") != expected_facts.get("primaryLumaBitDepth") or hvc[0].get("chromaBitDepth") != expected_facts.get("primaryChromaBitDepth"):
                raise ValidationError(f"Primary hvcC bit depth differs from the versioned R4-v1 fact lock for {entry['id']}.")
        if "ffprobePixelFormat" in expected_facts and (stream is None or stream.get("pix_fmt") != expected_facts["ffprobePixelFormat"]):
            raise ValidationError(f"Selected FFprobe pix_fmt differs from the versioned R4-v1 fact lock for {entry['id']}.")
        if "xmpColorInterpretationDeclarations" in expected_facts and structure.get("xmpColorInterpretationDeclarations") != expected_facts["xmpColorInterpretationDeclarations"]:
            raise ValidationError(f"XMP color declarations differ from the versioned R4-v1 fact lock for {entry['id']}.")
        if "exifColorSpace" in expected_facts and structure.get("exif", {}).get("colorSpace") != expected_facts["exifColorSpace"]:
            raise ValidationError(f"EXIF ColorSpace differs from the versioned R4-v1 fact lock for {entry['id']}.")
        if expected_facts.get("orientation") == "identity":
            _identity_orientation(metadata, entry["container"], structure, entry["id"])
        expected_video = expected_facts.get("embeddedHuaweiVideo")
        if expected_video:
            actual_video = structure.get("embeddedHuaweiLivePhotoVideo") or {}
            actual_video_facts = {
                "start": actual_video.get("videoStart"),
                "byteSize": actual_video.get("videoByteSize"),
                "tailBytes": actual_video.get("tailBytes"),
                "liveMarker": actual_video.get("liveMarker"),
            }
            if actual_video_facts != expected_video:
                raise ValidationError(f"Huawei live-photo boundary facts differ from the versioned R4-v1 lock: {actual_video_facts}.")
        sample_reports[entry["id"]] = sample_report
        _LAST_REPORT = report
        _write_report(args, report)

        if entry["id"] == "apple-p3-jpeg-p4-v1":
            _identity_orientation(metadata, entry["container"], structure, entry["id"])
            sample_report["ffmpegPrimaryRgb24Decode"] = _render_primary_rgb24(
                ffmpeg, path, stream, Path(args.workspace).resolve(), entry["id"]
            )
            sample_report["ffmpegPrimaryDecode"] = sample_report["ffmpegPrimaryRgb24Decode"]
        elif entry["id"] == "huawei-p3-heic-p4-v1":
            _identity_orientation(metadata, entry["container"], structure, entry["id"])
            sample_report["libheifPrimaryDecode"] = _decode_heif_primary_png(
                heif_convert, path, Path(args.workspace).resolve(), entry["id"], digest, structure
            )
        else:
            sample_report["ffmpegPrimaryDecode"] = _decode_to_null(ffmpeg, path, stream)
        _LAST_REPORT = report
        _write_report(args, report)

    # Acceptance-specific facts are derived from independent parsers/decoders,
    # not accepted from manifest prose alone.
    ten_bit = sample_reports["nikon-z8-10bit-sdr-heic-cc0"]
    ten_structure = ten_bit["independentStructure"]
    hevc_depths = ten_structure.get("primaryHvcC", [])
    if not hevc_depths or max(item["lumaBitDepth"] for item in hevc_depths) <= 8 or max(item["chromaBitDepth"] for item in hevc_depths) <= 8:
        raise ValidationError("The Nikon HEIC is not independently established as greater than 8-bit in its primary hvcC.")
    if ten_bit["ffmpegPrimaryDecode"].get("exitCode") != 0:
        raise ValidationError("The Nikon HEIC actual primary decode command was not recorded.")
    ten_stream = ten_bit["selectedPrimaryStream"]
    pixel_format = str(ten_stream.get("pix_fmt", "")).lower()
    try:
        decoded_sample_depth = int(ten_stream.get("bits_per_raw_sample", "0"))
    except (TypeError, ValueError):
        decoded_sample_depth = 0
    if not any(f"p{depth}" in pixel_format for depth in (10, 12, 14, 16)) and decoded_sample_depth <= 8:
        raise ValidationError(f"FFprobe selected primary does not expose a >8-bit decoded pixel format: {ten_stream}.")

    unprofiled = sample_reports["nikon-z8-unprofiled-sdr-jpeg-cc0"]
    jpeg_facts = unprofiled["independentStructure"]
    if jpeg_facts.get("iccSha256") or jpeg_facts.get("exif", {}).get("colorSpace") not in (None, 65535):
        raise ValidationError("The Nikon SDR JPEG contains an ICC profile or a calibrated EXIF ColorSpace value.")
    if jpeg_facts.get("xmpColorInterpretationDeclarations"):
        raise ValidationError(
            f"The Nikon SDR JPEG has explicit XMP image-color interpretation declarations: "
            f"{jpeg_facts['xmpColorInterpretationDeclarations']}"
        )
    exiftool_facts = unprofiled["exiftool"]["facts"]
    if jpeg_facts.get("xmpExtensions"):
        raise ValidationError("The Nikon SDR JPEG contains extended XMP that this preflight cannot independently reconstruct.")
    explicit_color_keys = []
    for key, value in exiftool_facts.items():
        if any(term in key.lower() for term in ("colorspace", "colorprimar", "transfercharacter", "matrixcoeff", "gamma", "profiledescription")):
            if "colorspace" in key.lower() and str(value).strip().lower() in ("uncalibrated", "65535"):
                continue
            explicit_color_keys.append(key)
    if explicit_color_keys:
        raise ValidationError(f"ExifTool found JPEG color declarations not represented by the manifest: {explicit_color_keys}")

    for sample_id in ("apple-p3-jpeg-p4-v1", "huawei-p3-heic-p4-v1"):
        sample = sample_reports[sample_id]
        profiles = sample["independentIccProfiles"]
        if len(profiles) != 1:
            raise ValidationError(f"Expected one independent primary ICC profile in {sample_id}; got {len(profiles)}.")
        if sample["independentStructure"].get("colorAssociationAmbiguous"):
            raise ValidationError(f"Color association is ambiguous for profiled sample {sample_id}.")
        if sample["independentStructure"].get("primaryOrientationProperties"):
            raise ValidationError(f"Non-identity HEIF transform must be independently implemented before render comparison: {sample_id}.")
        _identity_orientation(sample["exiftool"], "JPEG" if sample_id.startswith("apple-") else "HEIC", sample["independentStructure"], sample_id)
        if sample_id.startswith("apple-"):
            rgb = sample["ffmpegPrimaryRgb24Decode"]
            if rgb["stdoutBytes"] != sample["independentDimensions"]["width"] * sample["independentDimensions"]["height"] * 3:
                raise ValidationError(f"RGB24 source render byte count does not match dimensions for {sample_id}.")
        else:
            rgb = sample["libheifPrimaryDecode"]
            if rgb["decodedRgbBytes"] != sample["independentDimensions"]["width"] * sample["independentDimensions"]["height"] * 3:
                raise ValidationError(f"libheif source render byte count does not match dimensions for {sample_id}.")

    for entry in entries:
        path = _sample_path(root, entry)
        final_hash = hashlib.sha256(path.read_bytes()).hexdigest().upper()
        if final_hash != entry["sha256"].upper():
            raise ValidationError(f"Sample bytes changed while preflight was running: {entry['id']}.")
    report["inputHashesUnchangedAfterRead"] = True
    report["status"] = "PASS"
    _LAST_REPORT = report
    _write_report(args, report)
    return report


def formal_validate(args: argparse.Namespace) -> dict[str, Any]:
    global _LAST_REPORT
    root = Path.cwd().resolve()
    workspace_path = _safe_child(root, args.workspace, "R4 formal workspace")
    expected_workspace = (root / ".ai-tmp/workspace/P5-R4").resolve()
    if workspace_path != expected_workspace:
        raise ValidationError(f"Formal R4 validation requires the deterministic workspace {expected_workspace}.")
    evidence_path = _safe_child(root, args.evidence, "R4 formal evidence directory", must_exist=False)
    expected_evidence = (root / ".ai-tmp/evidence/P5-R4").resolve()
    if evidence_path != expected_evidence:
        raise ValidationError(f"Formal R4 evidence must be written only to {expected_evidence}.")

    test_run = _read_current_test_run(root, workspace_path, args.test_results)
    git_identity = _git_worktree_identity(root)
    formal_path = _new_formal_evidence_path(evidence_path, test_run["sha256"], git_identity["dirtyDiffSha256"])
    args.formal_evidence_path = formal_path
    report: dict[str, Any] = {
        "schemaVersion": 1,
        "profile": EXPECTED_PROFILE,
        "mode": "formal",
        "status": "RUNNING",
        "argv": sys.argv,
        "evidencePath": str(formal_path.relative_to(root)),
        "manifest": str(Path(args.manifest).as_posix()),
        "workspace": str(workspace_path.relative_to(root)),
        "testRun": test_run,
        "gitIdentity": git_identity,
        "routeCoverage": sorted(EXPECTED_ROUTES),
        "routeReports": {},
        "negativeRoutes": {},
        "coverageBlockers": [],
        "skipCount": 0,
        "blockerCount": 0,
    }
    _LAST_REPORT = report

    try:
        source_preflight = preflight(args)
    except Exception as ex:
        report["sourcePreflightError"] = f"{type(ex).__name__}: {ex}"
        _LAST_REPORT = report
        raise
    _LAST_REPORT = report
    preflight_report_path = workspace_path / "preflight-report.json"
    report["sourcePreflight"] = {
        "path": str(preflight_report_path.relative_to(root)),
        "sha256": hashlib.sha256(preflight_report_path.read_bytes()).hexdigest().upper(),
        "result": source_preflight,
    }

    manifest_path = (root / args.manifest).resolve()
    manifest_bytes = manifest_path.read_bytes()
    manifest = json.loads(manifest_bytes)
    validation = manifest["validation"]
    render = validation["renderComparison"]
    render_pipeline = validation["renderPipeline"]
    if render.get("thresholds") != EXPECTED_RENDER_THRESHOLDS:
        raise ValidationError(f"Frozen R4 render thresholds changed: {render.get('thresholds')!r}.")
    if render.get("thresholdStatus") != EXPECTED_THRESHOLD_STATUS or render_pipeline.get("frozenBeforeFormalConversion") is not True:
        raise ValidationError("R4 render thresholds/pipeline are not marked frozen before formal conversion.")
    if set(render.get("profiledRoutes", [])) != {"P5R4_AppleP3JpegToHeic", "P5R4_HuaweiHeicPrimaryToJpeg"}:
        raise ValidationError("R4 profiled render route set changed from the frozen Apple/Huawei set.")
    if render.get("sourceAndTargetProfileBytesMustMatch") is not True or render_pipeline.get("pipelineId") != EXPECTED_RENDER_PIPELINE_ID:
        raise ValidationError("R4 render comparison has an unknown profile carriage or pipeline policy.")
    if render.get("renderIntent") != "relative-colorimetric" or render.get("blackPointCompensation") is not False:
        raise ValidationError("R4 render comparison intent or black-point-compensation policy changed.")

    entries = manifest["samples"]
    entries_by_route: dict[str, dict[str, Any]] = {}
    for entry in entries:
        for route_name in entry.get("routes", []):
            if route_name in entries_by_route:
                raise ValidationError(f"R4 route {route_name} is assigned to multiple source samples.")
            entries_by_route[route_name] = entry
    if set(entries_by_route) != EXPECTED_ROUTES:
        raise ValidationError(f"R4 manifest route owners differ from the frozen route set: {sorted(entries_by_route)}.")

    output_files = _route_output_files(root, workspace_path / "tests", test_run)
    report["routeOutputInventory"] = output_files
    tools = source_preflight["tools"]
    ffmpeg = tools["ffmpeg"]
    ffprobe = tools["ffprobe"]
    exiftool = tools["exiftool"]
    heif_convert = tools["heifConvert"]
    run_sha = test_run["sha256"]
    source_reports = source_preflight["sampleReports"]
    manifest_sha = hashlib.sha256(manifest_bytes).hexdigest().upper()

    # Apple P3 JPEG -> HEIC: independent JPEG APP2, HEIF primary graph, CICP,
    # FFmpeg/libheif decode, and frozen ICC-to-sRGB render comparison.
    apple_entry = entries_by_route["P5R4_AppleP3JpegToHeic"]
    apple_source = _sample_path(root, apple_entry)
    apple_source_bytes = apple_source.read_bytes()
    apple_source_facts = parse_jpeg(apple_source_bytes)
    apple_source_icc = apple_source_facts.get("iccBytes")
    apple_expected = apple_entry["expectedIndependentFacts"]
    if not apple_source_icc or hashlib.sha256(apple_source_icc).hexdigest().upper() != apple_expected["iccSha256"].upper():
        raise ValidationError("Apple source ICC APP2 bytes do not match the frozen R4 identity.")
    apple_output_record = output_files["apple-p3-best-effort"][0]
    apple_output_path = root / apple_output_record["path"]
    apple_output_bytes = apple_output_path.read_bytes()
    apple_structure = parse_heif(apple_output_bytes)
    apple_types = [item["type"] for item in apple_structure["primaryAssociatedColr"]]
    if apple_structure["primaryItemType"] != "hvc1":
        raise ValidationError("Apple output pitm item is not an HEVC hvc1 primary.")
    if len(apple_structure["primaryIccBytes"]) != 1 or apple_structure["primaryIccBytes"][0] != apple_source_icc:
        raise ValidationError("Apple output primary ICC bytes do not exactly match the independently reassembled JPEG APP2 bytes.")
    if apple_structure["primaryNclx"] or apple_types not in (["prof"], ["rICC"]):
        raise ValidationError(f"Apple output must carry one exact primary ICC and no other colr property: {apple_types}.")
    apple_dims = _primary_dimensions(apple_structure, "Apple HEIC output")
    apple_metadata = _exiftool(exiftool, apple_output_path)
    _identity_orientation(apple_metadata, "HEIC", apple_structure, "apple-p3-output")
    apple_probe = _ffprobe_file(ffprobe, apple_output_path)
    apple_stream = _select_primary_stream(apple_probe["streams"], apple_structure["primaryItemId"])
    if (apple_stream.get("width"), apple_stream.get("height")) != apple_dims:
        raise ValidationError("Apple HEIC independent parser and pitm-matched FFprobe dimensions disagree.")
    apple_cicp = _unknown_hevc_color_fields(apple_stream, "Apple ICC-only output")
    apple_ffmpeg_decode = _decode_to_null(ffmpeg, apple_output_path, apple_stream)
    apple_source_preflight = source_reports[apple_entry["id"]]
    apple_source_dims = apple_source_facts["dimensions"]
    apple_source_stream = apple_source_preflight["selectedPrimaryStream"]
    if (apple_source_stream.get("width"), apple_source_stream.get("height")) != (apple_source_dims["width"], apple_source_dims["height"]):
        raise ValidationError("Apple JPEG parser and FFprobe dimensions disagree before color management.")
    apple_source_render = apple_source_preflight.get("ffmpegPrimaryRgb24Decode")
    if not apple_source_render:
        raise ValidationError("Apple source FFmpeg RGB24 render is absent from the independent preflight.")
    apple_source_rgb = _raw_rgb24_artifact(root, apple_source_render)
    apple_output_pixels, apple_output_render_size, apple_libheif_decode = _decode_formal_heif_primary_png(
        heif_convert, apple_output_path, workspace_path, "apple-p3-output", apple_output_record["sha256"],
        run_sha, apple_structure,
    )
    apple_render_comparison = _compare_profiled_renders(
        apple_source_rgb,
        (apple_source_dims["width"], apple_source_dims["height"]),
        apple_output_pixels,
        apple_output_render_size,
        apple_source_icc,
        render["thresholds"],
        "Apple P3 JPEG-to-HEIC",
    )
    if not apple_render_comparison["thresholdsPass"]:
        raise ValidationError(f"Apple P3 render comparison exceeded the frozen R4 thresholds: {apple_render_comparison}.")
    report["routeReports"]["P5R4_AppleP3JpegToHeic"] = {
        "status": "PASS",
        "source": {"path": str(apple_source.relative_to(root)), "byteSize": len(apple_source_bytes), "sha256": hashlib.sha256(apple_source_bytes).hexdigest().upper()},
        "output": apple_output_record,
        "independentStructure": {"primaryItemId": apple_structure["primaryItemId"], "primaryItemType": apple_structure["primaryItemType"], "primaryDimensions": apple_structure["primaryDimensions"], "primaryAssociatedColr": apple_structure["primaryAssociatedColr"]},
        "primaryIccSha256": hashlib.sha256(apple_structure["primaryIccBytes"][0]).hexdigest().upper(),
        "primaryNclxCount": len(apple_structure["primaryNclx"]),
        "exiftool": apple_metadata,
        "ffprobe": {"record": apple_probe["record"], "pitmMatchedStream": apple_stream, "hevcColorFields": apple_cicp},
        "ffmpegDecode": apple_ffmpeg_decode,
        "sourceRgb24Decode": apple_source_render,
        "libheifPrimaryDecode": apple_libheif_decode,
        "renderComparison": apple_render_comparison,
        "strictOutputDirectory": {"path": "tests/outputs/apple-p3-strict", "artifactCount": len(output_files["apple-p3-strict"])},
    }
    _LAST_REPORT = report

    # Unprofiled Nikon JPEG -> HEIC: unknown color facts stay unknown in both
    # the item graph and HEVC VUI, and the generated primary decodes.
    unprofiled_entry = entries_by_route["P5R4_UnprofiledJpegToHeic"]
    unprofiled_source = _sample_path(root, unprofiled_entry)
    unprofiled_source_facts = parse_jpeg(unprofiled_source.read_bytes())
    unprofiled_output_record = output_files["unprofiled-jpeg-best-effort"][0]
    unprofiled_output_path = root / unprofiled_output_record["path"]
    unprofiled_structure = parse_heif(unprofiled_output_path.read_bytes())
    if unprofiled_structure["primaryItemType"] != "hvc1":
        raise ValidationError("Unprofiled output pitm item is not an HEVC hvc1 primary.")
    if unprofiled_structure["primaryAssociatedColr"] or unprofiled_structure["primaryIccBytes"] or unprofiled_structure["primaryNclx"]:
        raise ValidationError("Unprofiled JPEG-to-HEIC output invented a primary-associated color property.")
    unprofiled_dims = _primary_dimensions(unprofiled_structure, "unprofiled HEIC output")
    unprofiled_source_dims = unprofiled_source_facts["dimensions"]
    if unprofiled_dims != (unprofiled_source_dims["width"], unprofiled_source_dims["height"]):
        raise ValidationError("Unprofiled JPEG and HEIC primary dimensions differ.")
    unprofiled_probe = _ffprobe_file(ffprobe, unprofiled_output_path)
    unprofiled_stream = _select_primary_stream(unprofiled_probe["streams"], unprofiled_structure["primaryItemId"])
    if (unprofiled_stream.get("width"), unprofiled_stream.get("height")) != unprofiled_dims:
        raise ValidationError("Unprofiled HEIC independent parser and pitm-matched FFprobe dimensions disagree.")
    unprofiled_cicp = _unknown_hevc_color_fields(unprofiled_stream, "Unprofiled output")
    unprofiled_ffmpeg_decode = _decode_to_null(ffmpeg, unprofiled_output_path, unprofiled_stream)
    report["routeReports"]["P5R4_UnprofiledJpegToHeic"] = {
        "status": "PASS",
        "source": {"path": str(unprofiled_source.relative_to(root)), "byteSize": unprofiled_entry["byteSize"], "sha256": unprofiled_entry["sha256"]},
        "output": unprofiled_output_record,
        "independentStructure": {"primaryItemId": unprofiled_structure["primaryItemId"], "primaryItemType": unprofiled_structure["primaryItemType"], "primaryDimensions": unprofiled_structure["primaryDimensions"], "primaryAssociatedColr": unprofiled_structure["primaryAssociatedColr"]},
        "primaryIccCount": len(unprofiled_structure["primaryIccBytes"]),
        "primaryNclxCount": len(unprofiled_structure["primaryNclx"]),
        "ffprobe": {"record": unprofiled_probe["record"], "pitmMatchedStream": unprofiled_stream, "hevcColorFields": unprofiled_cicp},
        "ffmpegDecode": unprofiled_ffmpeg_decode,
        "strictOutputDirectory": {"path": "tests/outputs/unprofiled-jpeg-strict", "artifactCount": len(output_files["unprofiled-jpeg-strict"])},
    }
    _LAST_REPORT = report

    # Huawei Moving Photo: independently prove that the product materialized
    # exactly the inspected still-image prefix, then compare its pinned
    # libheif primary render with the independently decoded JPEG result.
    huawei_entry = entries_by_route["P5R4_HuaweiHeicPrimaryToJpeg"]
    huawei_source = _sample_path(root, huawei_entry)
    huawei_source_bytes = huawei_source.read_bytes()
    huawei_source_structure = parse_heif(huawei_source_bytes)
    huawei_video = huawei_source_structure.get("embeddedHuaweiLivePhotoVideo") or {}
    huawei_start = huawei_video.get("videoStart")
    expected_huawei_video = huawei_entry["expectedIndependentFacts"].get("embeddedHuaweiVideo", {})
    if huawei_start != expected_huawei_video.get("start") or huawei_start != 2073984:
        raise ValidationError(f"Huawei inspected still-image prefix boundary differs from the frozen 0..2073984 range: {huawei_start}.")
    huawei_primary_path, huawei_primary_bytes, huawei_materialization = _materialized_huawei_primary(
        root, workspace_path / "tests", test_run, huawei_source, 0, huawei_start,
    )
    huawei_primary_structure = parse_heif(huawei_primary_bytes)
    if (huawei_primary_structure["primaryItemId"] != huawei_source_structure["primaryItemId"] or
        huawei_primary_structure["primaryItemType"] != huawei_source_structure["primaryItemType"]):
        raise ValidationError("Huawei materialized prefix does not retain the independently parsed pitm primary identity/type.")
    huawei_source_icc_list = huawei_primary_structure["primaryIccBytes"]
    huawei_expected_icc = huawei_entry["expectedIndependentFacts"]["iccSha256"].upper()
    if len(huawei_source_icc_list) != 1 or hashlib.sha256(huawei_source_icc_list[0]).hexdigest().upper() != huawei_expected_icc:
        raise ValidationError("Huawei materialized primary does not have its unique frozen primary ICC association.")
    if huawei_primary_structure["primaryNclx"]:
        raise ValidationError("Huawei materialized primary has an unexpected NCLX association under the frozen ICC-only route.")
    huawei_source_dims = _primary_dimensions(huawei_primary_structure, "Huawei materialized HEIC primary")
    huawei_primary_metadata = _exiftool(exiftool, huawei_primary_path)
    _identity_orientation(huawei_primary_metadata, "HEIC", huawei_primary_structure, "huawei-materialized-primary")
    huawei_source_pixels, huawei_source_png_size, huawei_libheif_decode = _decode_formal_heif_primary_png(
        heif_convert, huawei_primary_path, workspace_path, "huawei-p3-source-primary",
        huawei_materialization["sha256"], run_sha, huawei_primary_structure,
    )
    huawei_output_record = output_files["huawei-primary-best-effort"][0]
    huawei_output_path = root / huawei_output_record["path"]
    huawei_output_bytes = huawei_output_path.read_bytes()
    huawei_output_structure = parse_jpeg(huawei_output_bytes)
    huawei_target_icc = huawei_output_structure.get("iccBytes")
    if not huawei_target_icc or huawei_target_icc != huawei_source_icc_list[0]:
        raise ValidationError("Huawei output JPEG APP2 ICC sequence is incomplete or not byte-identical to the materialized primary ICC.")
    huawei_output_dims = huawei_output_structure["dimensions"]
    if (huawei_output_dims["width"], huawei_output_dims["height"]) != huawei_source_dims:
        raise ValidationError("Huawei source primary and output JPEG dimensions differ before color management.")
    huawei_output_metadata = _exiftool(exiftool, huawei_output_path)
    _identity_orientation(huawei_output_metadata, "JPEG", {"exif": huawei_output_structure["exif"]}, "huawei-jpeg-output")
    huawei_probe = _ffprobe_file(ffprobe, huawei_output_path)
    huawei_stream = _select_primary_stream(huawei_probe["streams"], None)
    if (huawei_stream.get("width"), huawei_stream.get("height")) != (huawei_output_dims["width"], huawei_output_dims["height"]):
        raise ValidationError("Huawei output JPEG parser and its sole FFprobe stream dimensions disagree.")
    huawei_rgb_render = _render_primary_rgb24(
        ffmpeg, huawei_output_path, huawei_stream, workspace_path,
        f"huawei-p3-output-{huawei_output_record['sha256'][:16].lower()}-{run_sha[:12].lower()}",
        "formal-renders",
    )
    huawei_output_rgb = _raw_rgb24_artifact(root, huawei_rgb_render)
    huawei_render_comparison = _compare_profiled_renders(
        huawei_source_pixels,
        huawei_source_png_size,
        huawei_output_rgb,
        (huawei_output_dims["width"], huawei_output_dims["height"]),
        huawei_source_icc_list[0],
        render["thresholds"],
        "Huawei HEIC primary-to-JPEG",
    )
    if not huawei_render_comparison["thresholdsPass"]:
        raise ValidationError(f"Huawei render comparison exceeded the frozen R4 thresholds: {huawei_render_comparison}.")
    report["routeReports"]["P5R4_HuaweiHeicPrimaryToJpeg"] = {
        "status": "PASS",
        "source": {"path": str(huawei_source.relative_to(root)), "byteSize": len(huawei_source_bytes), "sha256": hashlib.sha256(huawei_source_bytes).hexdigest().upper(), "primaryItemId": huawei_source_structure["primaryItemId"], "primaryItemType": huawei_source_structure["primaryItemType"]},
        "materializedPrimary": huawei_materialization,
        "materializedPrimaryStructure": {"primaryItemId": huawei_primary_structure["primaryItemId"], "primaryItemType": huawei_primary_structure["primaryItemType"], "primaryDimensions": huawei_primary_structure["primaryDimensions"], "primaryAssociatedColr": huawei_primary_structure["primaryAssociatedColr"]},
        "materializedPrimaryExifTool": huawei_primary_metadata,
        "materializedPrimaryLibheifDecode": huawei_libheif_decode,
        "output": huawei_output_record,
        "outputStructure": {"dimensions": huawei_output_dims, "iccSegmentCount": huawei_output_structure["iccSegmentCount"], "iccSha256": huawei_output_structure["iccSha256"], "exif": huawei_output_structure["exif"]},
        "outputExifTool": huawei_output_metadata,
        "ffprobe": {"record": huawei_probe["record"], "soleImageStream": huawei_stream},
        "ffmpegRgb24Decode": huawei_rgb_render,
        "renderComparison": huawei_render_comparison,
        "strictOutputDirectory": {"path": "tests/outputs/huawei-primary-strict", "artifactCount": len(output_files["huawei-primary-strict"])},
    }
    _LAST_REPORT = report

    # Nikon 10-bit HEIC -> JPEG rejects both policies. Same-format passthrough
    # must remain exact and independently decode at >8-bit from the pitm stream.
    ten_entry = entries_by_route["P5R4_TenBitHeicToJpeg"]
    ten_source = _sample_path(root, ten_entry)
    ten_source_bytes = ten_source.read_bytes()
    ten_source_structure = parse_heif(ten_source_bytes)
    ten_hvc = ten_source_structure["primaryHvcC"]
    if not ten_hvc or min(item["lumaBitDepth"] for item in ten_hvc) <= 8 or min(item["chromaBitDepth"] for item in ten_hvc) <= 8:
        raise ValidationError("Nikon source hvcC does not independently establish >8-bit luma and chroma.")
    ten_source_probe = _ffprobe_file(ffprobe, ten_source)
    ten_source_stream = _select_primary_stream(ten_source_probe["streams"], ten_source_structure["primaryItemId"])
    ten_source_depth = _stream_pixel_depth(ten_source_stream)
    if ten_source_depth <= 8:
        raise ValidationError(f"Nikon source FFprobe primary does not expose >8-bit decode: {ten_source_stream}.")
    ten_source_decode = _decode_to_null(ffmpeg, ten_source, ten_source_stream)
    if ten_source_decode["exitCode"] != 0:
        raise ValidationError("Nikon 10-bit source primary did not independently decode with FFmpeg.")

    ten_passthrough_report: dict[str, Any] = {}
    for directory_name in ("ten-bit-passthrough-strict", "ten-bit-passthrough-best-effort"):
        output_record = output_files[directory_name][0]
        output_path = root / output_record["path"]
        output_bytes = output_path.read_bytes()
        if output_bytes != ten_source_bytes:
            raise ValidationError(f"Nikon 10-bit passthrough is not byte-identical in {directory_name}.")
        output_structure = parse_heif(output_bytes)
        output_hvc = output_structure["primaryHvcC"]
        if not output_hvc or min(item["lumaBitDepth"] for item in output_hvc) <= 8 or min(item["chromaBitDepth"] for item in output_hvc) <= 8:
            raise ValidationError(f"Nikon passthrough hvcC does not remain >8-bit in {directory_name}.")
        output_probe = _ffprobe_file(ffprobe, output_path)
        output_stream = _select_primary_stream(output_probe["streams"], output_structure["primaryItemId"])
        output_depth = _stream_pixel_depth(output_stream)
        if output_depth <= 8:
            raise ValidationError(f"Nikon passthrough FFprobe primary does not remain >8-bit in {directory_name}.")
        output_decode = _decode_to_null(ffmpeg, output_path, output_stream)
        if output_decode["exitCode"] != 0:
            raise ValidationError(f"Nikon passthrough did not independently decode with FFmpeg in {directory_name}.")
        ten_passthrough_report[directory_name] = {
            "status": "PASS",
            "output": output_record,
            "byteIdenticalToSource": True,
            "sha256": hashlib.sha256(output_bytes).hexdigest().upper(),
            "primaryHvcC": output_hvc,
            "ffprobe": {"record": output_probe["record"], "pitmMatchedStream": output_stream, "decodedBitDepth": output_depth},
            "ffmpegDecode": output_decode,
        }
    report["routeReports"]["P5R4_TenBitHeicToJpeg"] = {
        "status": "PASS",
        "source": {"path": str(ten_source.relative_to(root)), "byteSize": len(ten_source_bytes), "sha256": hashlib.sha256(ten_source_bytes).hexdigest().upper(), "primaryHvcC": ten_hvc, "ffprobePrimaryStream": ten_source_stream, "decodedBitDepth": ten_source_depth},
        "ffprobe": {"record": ten_source_probe["record"], "pitmMatchedStream": ten_source_stream},
        "ffmpegDecode": ten_source_decode,
        "strictOutputDirectory": {"path": "tests/outputs/ten-bit-jpeg-strict", "artifactCount": len(output_files["ten-bit-jpeg-strict"])},
        "bestEffortOutputDirectory": {"path": "tests/outputs/ten-bit-jpeg-best-effort", "artifactCount": len(output_files["ten-bit-jpeg-best-effort"])},
    }
    report["routeReports"]["P5R4_TenBitHeicPassthrough"] = {
        "status": "PASS",
        "sourceSha256": hashlib.sha256(ten_source_bytes).hexdigest().upper(),
        "policyOutputs": ten_passthrough_report,
    }
    report["negativeRoutes"] = {
        "P5R4_TargetProfileTransformUnsupported": {"status": "PASS", "outputDirectory": "tests/outputs/nclx-only-no-transform", "artifactCount": len(output_files["nclx-only-no-transform"])},
        "P5R4_AmbiguousColorMetadata": {"status": "PASS", "outputDirectory": "tests/outputs/ambiguous-color-output", "artifactCount": len(output_files["ambiguous-color-output"])},
    }
    _LAST_REPORT = report

    input_hashes: dict[str, str] = {}
    for entry in entries:
        source_path = _sample_path(root, entry)
        current_hash = hashlib.sha256(source_path.read_bytes()).hexdigest().upper()
        if current_hash != entry["sha256"].upper():
            raise ValidationError(f"R4 immutable sample hash changed during formal validation: {entry['id']}.")
        input_hashes[entry["id"]] = current_hash
    report["inputHashesUnchangedAfterValidation"] = True
    report["inputHashes"] = input_hashes
    report["status"] = "PASS"
    report["zeroSkip"] = True
    report["zeroBlockers"] = True
    report["completedAtUtc"] = datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")
    _LAST_REPORT = report
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("formal", "preflight"), default="formal")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--workspace", required=True)
    parser.add_argument("--evidence")
    parser.add_argument("--test-results")
    parser.add_argument("--exiftool")
    parser.add_argument("--ffmpeg")
    parser.add_argument("--ffprobe")
    parser.add_argument("--heif-convert")
    args = parser.parse_args()
    try:
        if args.mode == "preflight":
            report = preflight(args)
            print(json.dumps({"status": report["status"], "profile": report["profile"], "sampleCount": len(report["sampleReports"]), "routeCoverage": report["routeCoverage"]}, ensure_ascii=False, indent=2))
            return 0
        if not args.evidence or not args.test_results:
            raise ValidationError("Formal mode requires --evidence and the immediately preceding --test-results TRX file.")
        report = formal_validate(args)
        _write_formal_evidence(args.formal_evidence_path, report)
        print(json.dumps({
            "status": report["status"],
            "profile": report["profile"],
            "routeCoverage": report["routeCoverage"],
            "zeroSkip": report["zeroSkip"],
            "zeroBlockers": report["zeroBlockers"],
            "evidence": report["evidencePath"],
        }, ensure_ascii=False, indent=2))
        return 0
    except Exception as ex:
        if args.mode == "formal":
            failed_report = _LAST_REPORT if _LAST_REPORT.get("mode") == "formal" else {
                "schemaVersion": 1,
                "profile": EXPECTED_PROFILE,
                "mode": "formal",
                "argv": sys.argv,
                "status": "BLOCKED",
            }
            failed_report["status"] = "BLOCKED"
            failed_report["blockerCount"] = max(1, int(failed_report.get("blockerCount", 0)))
            failed_report["zeroBlockers"] = False
            failed_report["error"] = f"{type(ex).__name__}: {ex}"
            evidence_path = getattr(args, "formal_evidence_path", None)
            if evidence_path is not None:
                try:
                    _write_formal_evidence(evidence_path, failed_report)
                except OSError:
                    pass
            print(f"P5-R4 formal BLOCKED: {ex}", file=sys.stderr)
        else:
            failed_report = _LAST_REPORT or {
                "schemaVersion": 1,
                "profile": EXPECTED_PROFILE,
                "mode": "preflight",
                "argv": sys.argv,
                "sampleReports": {},
            }
            failed_report["status"] = "BLOCKED"
            failed_report["error"] = f"{type(ex).__name__}: {ex}"
            try:
                _write_report(args, failed_report)
            except OSError:
                pass
            print(f"P5-R4 preflight BLOCKED: {ex}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
