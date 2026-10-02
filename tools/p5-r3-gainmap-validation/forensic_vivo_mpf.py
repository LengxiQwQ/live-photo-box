#!/usr/bin/env python3
"""Read-only forensic report for the locked P5-R3 Vivo JPEG MPF discrepancy."""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from typing import Any


EXPECTED_SHA256 = "631CF8DAC983A9F58FC1D8CB63AAF8CC0569C45B80F046F1B995936D608320B9"
CONTAINER_NS = "http://ns.google.com/photos/1.0/container/"
ITEM_NS = "http://ns.google.com/photos/1.0/container/item/"
XMP_PREFIX = b"http://ns.adobe.com/xap/1.0/\0"


class ForensicError(RuntimeError):
    pass


def need(condition: bool, message: str) -> None:
    if not condition:
        raise ForensicError(message)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def next_marker(data: bytes, start: int, limit: int) -> tuple[int, int]:
    cursor = start
    while cursor < limit:
        marker_start = data.find(b"\xFF", cursor, limit)
        need(marker_start >= 0, f"No JPEG marker found after offset {cursor}.")
        code_pos = marker_start + 1
        while code_pos < limit and data[code_pos] == 0xFF:
            code_pos += 1
        need(code_pos < limit, "JPEG codestream ends inside a marker.")
        code = data[code_pos]
        if code == 0x00 or 0xD0 <= code <= 0xD7:
            cursor = code_pos + 1
            continue
        return marker_start, code
    raise ForensicError("JPEG codestream has no terminating marker.")


def parse_jpeg(data: bytes, start: int, limit: int) -> dict[str, Any]:
    need(start + 2 <= limit and data[start:start + 2] == b"\xFF\xD8", f"SOI missing at {start}.")
    pos = start + 2
    markers: list[dict[str, Any]] = [{"code": 0xD8, "name": "SOI", "start": start, "end": start + 2}]
    scans: list[dict[str, int]] = []
    while pos < limit:
        marker_start, code = next_marker(data, pos, limit)
        code_pos = marker_start + 1
        while code_pos < limit and data[code_pos] == 0xFF:
            code_pos += 1
        need(code_pos < limit, "JPEG marker code is truncated.")
        if code == 0xD9:
            markers.append({"code": code, "name": "EOI", "start": marker_start, "end": code_pos + 1})
            return {"start": start, "end": code_pos + 1, "markers": markers, "scans": scans}
        if code in (0xD8, 0x01) or 0xD0 <= code <= 0xD7:
            end = code_pos + 1
            markers.append({"code": code, "name": f"RST{code - 0xD0}" if code >= 0xD0 else f"0x{code:02X}",
                            "start": marker_start, "end": end})
            pos = end
            continue
        need(code_pos + 3 <= limit, f"Marker 0x{code:02X} length is truncated.")
        segment_length = int.from_bytes(data[code_pos + 1:code_pos + 3], "big")
        need(segment_length >= 2, f"Marker 0x{code:02X} has an invalid length.")
        payload_start = code_pos + 3
        payload_end = code_pos + 1 + segment_length
        need(payload_end <= limit, f"Marker 0x{code:02X} exceeds the selected JPEG range.")
        markers.append({"code": code, "name": f"0x{code:02X}", "start": marker_start,
                        "end": payload_end, "payloadStart": payload_start, "payloadEnd": payload_end})
        pos = payload_end
        if code == 0xDA:
            scan_start = pos
            scan_end, _ = next_marker(data, scan_start, limit)
            scans.append({"start": scan_start, "end": scan_end, "length": scan_end - scan_start})
            pos = scan_end
    raise ForensicError("JPEG codestream has no terminating EOI marker.")


def read_uint(data: bytes, offset: int, width: int, endian: str) -> int:
    need(offset >= 0 and offset + width <= len(data), f"Integer range outside file at {offset}.")
    return int.from_bytes(data[offset:offset + width], endian)


def parse_mpf(data: bytes, primary: dict[str, Any]) -> dict[str, Any]:
    app2 = [m for m in primary["markers"] if m["code"] == 0xE2 and
            data[m["payloadStart"]:m["payloadStart"] + 4] == b"MPF\0"]
    need(len(app2) == 1, f"Expected one MPF APP2 segment; found {len(app2)}.")
    segment = app2[0]
    tiff = segment["payloadStart"] + 4
    byte_order = data[tiff:tiff + 2]
    need(byte_order in (b"II", b"MM"), "MPF TIFF byte order is invalid.")
    endian = "little" if byte_order == b"II" else "big"
    need(read_uint(data, tiff + 2, 2, endian) == 42, "MPF TIFF magic is invalid.")
    ifd = tiff + read_uint(data, tiff + 4, 4, endian)
    count = read_uint(data, ifd, 2, endian)
    tags: dict[int, dict[str, Any]] = {}
    for index in range(count):
        entry_offset = ifd + 2 + index * 12
        tag = read_uint(data, entry_offset, 2, endian)
        tags[tag] = {
            "location": entry_offset,
            "rawHex": data[entry_offset:entry_offset + 12].hex().upper(),
            "type": read_uint(data, entry_offset + 2, 2, endian),
            "count": read_uint(data, entry_offset + 4, 4, endian),
            "valueOrOffset": read_uint(data, entry_offset + 8, 4, endian),
        }
    need(0xB001 in tags and 0xB002 in tags, "MPF NumberOfImages or MPEntry is missing.")
    number = tags[0xB001]["valueOrOffset"]
    mpentry = tags[0xB002]
    need(mpentry["type"] == 7 and mpentry["count"] == number * 16, "MPEntry type/count is invalid.")
    mpentry_start = tiff + mpentry["valueOrOffset"]
    entries = []
    for index in range(number):
        location = mpentry_start + index * 16
        raw = data[location:location + 16]
        need(len(raw) == 16, f"MPEntry {index} is truncated.")
        relative = int.from_bytes(raw[8:12], endian)
        entries.append({
            "index": index,
            "location": location,
            "rawHex": raw.hex().upper(),
            "attributes": int.from_bytes(raw[0:4], endian),
            "size": int.from_bytes(raw[4:8], endian),
            "relativeOffset": relative,
            "absoluteOffset": 0 if index == 0 else tiff + relative,
            "dependentImage1": int.from_bytes(raw[12:14], endian),
            "dependentImage2": int.from_bytes(raw[14:16], endian),
        })
    return {
        "app2": {"start": segment["start"], "end": segment["end"], "payloadStart": segment["payloadStart"],
                 "payloadEnd": segment["payloadEnd"], "markerLength": segment["end"] - segment["start"]},
        "tiff": {"start": tiff, "byteOrderHex": byte_order.hex().upper(), "ifdOffset": ifd - tiff,
                 "ifdAbsolute": ifd, "entryCount": count},
        "tags": {f"0x{tag:04X}": value for tag, value in tags.items()},
        "mpEntryArray": {"start": mpentry_start, "end": mpentry_start + number * 16,
                         "rawHex": data[mpentry_start:mpentry_start + number * 16].hex().upper()},
        "entries": entries,
    }


def read_xmp_items(data: bytes, primary: dict[str, Any]) -> list[dict[str, Any]]:
    result = []
    for marker in primary["markers"]:
        if marker["code"] != 0xE1:
            continue
        payload = data[marker["payloadStart"]:marker["payloadEnd"]]
        if not payload.startswith(XMP_PREFIX):
            continue
        packet = payload[len(XMP_PREFIX):]
        try:
            root = ET.fromstring(packet)
        except ET.ParseError as exc:
            raise ForensicError(f"Primary XMP is malformed: {exc}") from exc
        items = []
        for item in root.iter(f"{{{CONTAINER_NS}}}Item"):
            attrs = item.attrib
            length = attrs.get(f"{{{ITEM_NS}}}Length")
            padding = attrs.get(f"{{{ITEM_NS}}}Padding")
            items.append({
                "semantic": attrs.get(f"{{{ITEM_NS}}}Semantic"),
                "mime": attrs.get(f"{{{ITEM_NS}}}Mime"),
                "length": int(length) if length is not None and length.isdigit() else None,
                "padding": int(padding) if padding is not None and padding.isdigit() else None,
            })
        if items:
            result.append({"markerStart": marker["start"], "items": items})
    return result


def exiftool_mpf(path: Path, explicit: str | None) -> dict[str, Any]:
    exe = explicit or shutil.which("exiftool")
    if not exe:
        return {"available": False, "reason": "ExifTool was not found; raw independent MPF parse remains available."}
    version = subprocess.run([exe, "-ver"], capture_output=True, text=True, check=False)
    result = subprocess.run([exe, "-j", "-G1", "-s", "-MPF:All", str(path)],
                            capture_output=True, text=True, check=False)
    return {
        "available": True,
        "path": str(Path(exe).resolve()),
        "version": version.stdout.strip(),
        "exitCode": result.returncode,
        "stderr": result.stderr.strip(),
        "json": json.loads(result.stdout) if result.stdout.strip().startswith("[") else None,
        "stdout": result.stdout if not result.stdout.strip().startswith("[") else None,
    }


def build_report(source: Path, expected: str, exiftool: str | None, v1_report_path: Path) -> dict[str, Any]:
    data = source.read_bytes()
    source_hash = sha256(data)
    need(source_hash == expected.upper(), f"Vivo source hash mismatch: {source_hash}.")
    primary = parse_jpeg(data, 0, len(data))
    mpf = parse_mpf(data, primary)
    need(len(mpf["entries"]) == 2, f"Expected two MPF entries; found {len(mpf['entries'])}.")
    second = mpf["entries"][1]
    actual_second = parse_jpeg(data, second["absoluteOffset"], len(data))
    xmp_packets = read_xmp_items(data, primary)
    all_items = [item for packet in xmp_packets for item in packet["items"]]
    gain = [item for item in all_items if item["semantic"] == "GainMap"]
    motion = [item for item in all_items if item["semantic"] == "MotionPhoto"]
    primary_items = [item for item in all_items if item["semantic"] == "Primary"]
    need(len(gain) == len(motion) == len(primary_items) == 1, "Vivo XMP does not have one Primary, GainMap and MotionPhoto item.")
    mpf_primary_size = mpf["entries"][0]["size"]
    actual_primary_end = primary["end"]
    mismatch = {"start": mpf_primary_size, "end": actual_primary_end,
                "length": actual_primary_end - mpf_primary_size}
    need(mismatch["length"] == 600, f"Expected the observed 600-byte interval; found {mismatch['length']}.")
    interval = data[mismatch["start"]:mismatch["end"]]
    scan_parts = []
    for scan in primary["scans"]:
        part_start = max(mismatch["start"], scan["start"])
        part_end = min(mismatch["end"], scan["end"])
        if part_start < part_end:
            part = data[part_start:part_end]
            scan_parts.append({"start": part_start, "end": part_end, "length": len(part),
                               "classification": "entropy-coded JPEG scan data", "sha256": sha256(part)})
    eoi = next((m for m in primary["markers"] if m["code"] == 0xD9), None)
    need(eoi is not None, "Primary JPEG has no parsed EOI marker.")
    eoi_start = max(mismatch["start"], eoi["start"])
    eoi_end = min(mismatch["end"], eoi["end"])
    eoi_part = data[eoi_start:eoi_end] if eoi_start < eoi_end else b""
    eoi_component = ({"start": eoi_start, "end": eoi_end, "length": len(eoi_part),
                      "classification": "JPEG EOI marker", "rawHex": eoi_part.hex().upper(),
                      "sha256": sha256(eoi_part)} if eoi_part else None)
    classified_length = sum(part["length"] for part in scan_parts) + (eoi_component["length"] if eoi_component else 0)
    classification = " + ".join(
        [f"{part['length']} entropy-coded scan bytes" for part in scan_parts] +
        ([f"{eoi_component['length']}-byte EOI marker"] if eoi_component else []))
    file_size = len(data)
    gain_start = second["absoluteOffset"]
    gain_end = gain_start + second["size"]
    motion_start = gain_end
    motion_end = motion_start + motion[0]["length"]
    need(gain_start == actual_primary_end, "MPF second-image offset differs from the actual first EOI.")
    need(second["size"] == gain[0]["length"], "MPF second-image size differs from XMP GainMap length.")
    need(motion_end == file_size, "XMP MotionPhoto length does not reach the file end after Primary + GainMap.")
    v1_report = json.loads(v1_report_path.read_text(encoding="utf-8-sig"))
    v1_route = next(route for route in v1_report["routes"] if route["routeId"] == "vivo-jpeg-to-heic")
    need(v1_report["status"] == "FAIL" and v1_route["status"] == "FAIL",
         "Archived v1 run report does not preserve the expected Vivo failure.")
    marker_map = []
    for marker in primary["markers"]:
        row = dict(marker)
        row["name"] = {0xD8: "SOI", 0xD9: "EOI", 0xDA: "SOS", 0xE1: "APP1", 0xE2: "APP2"}.get(
            marker["code"], f"0x{marker['code']:02X}")
        marker_map.append(row)
    return {
        "schemaVersion": 1,
        "reportId": "P5-R3-Vivo-MPF-Forensic-v1",
        "purpose": "Classify the locked source-side MPF discrepancy without changing the v1 gate.",
        "v1Gate": {
            "status": v1_report["status"],
            "matrixSha256": v1_report["matrixSha256"],
            "referencePolicySha256": v1_report["referencePolicySha256"],
            "vivoRouteStatus": v1_route["status"],
            "failedPredicate": "sourceMpfPrimaryEntrySizeMatchesMarkerRange",
        },
        "source": {"path": str(source), "size": file_size, "sha256": source_hash,
                   "lockedSha256": expected.upper()},
        "jpegPrimary": {"start": primary["start"], "endExclusiveAfterEoi": actual_primary_end,
                        "lengthSoiThroughEoi": actual_primary_end, "scans": primary["scans"],
                        "markerMap": marker_map},
        "mpf": mpf,
        "discrepancy": {
            "entry0DeclaredEndpoint": mpf_primary_size,
            "actualPrimaryEoiEndpoint": actual_primary_end,
            "actualMpfSecondSoiOffset": second["absoluteOffset"],
            "interval": mismatch,
            "intervalSha256": sha256(interval),
            "classification": classification or "unclassified",
            "scanComponents": scan_parts,
            "eoiComponent": eoi_component,
            "classifiedByteCount": classified_length,
            "allBytesIndependentlyClassified": classified_length == mismatch["length"],
            "fullyContainedInPrimaryCodestream": mismatch["start"] >= primary["start"] and mismatch["end"] <= primary["end"],
        },
        "xmpContainerDirectory": {
            "packets": xmp_packets,
            "primaryItemCount": len(primary_items),
            "gainMapItemCount": len(gain),
            "motionPhotoItemCount": len(motion),
            "primaryActualLength": actual_primary_end,
            "gainMapLength": gain[0]["length"],
            "motionPhotoLength": motion[0]["length"],
            "sumMatchesFileLength": actual_primary_end + gain[0]["length"] + motion[0]["length"] == file_size,
            "arithmeticEndpoint": actual_primary_end + gain[0]["length"] + motion[0]["length"],
        },
        "gainMapCodestream": {"start": gain_start, "endExclusive": gain_end,
                              "mpfLength": second["size"], "xmpLength": gain[0]["length"],
                              "actualEoiEndExclusive": actual_second["end"],
                              "sha256": sha256(data[gain_start:gain_end])},
        "exifToolMpfCorroboration": exiftool_mpf(source, exiftool),
        "classificationComplete": classified_length == mismatch["length"] and mismatch["length"] == 600,
        "strictV1PredicateStillFails": mpf_primary_size != actual_primary_end,
        "status": "FORENSIC_COMPLETE_V1_REMAINS_FAIL",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--expected-source-sha256", default=EXPECTED_SHA256)
    parser.add_argument("--v1-report", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--exiftool")
    args = parser.parse_args()
    try:
        need(not args.output.exists(), f"Refusing to overwrite forensic report: {args.output}.")
        report = build_report(args.source, args.expected_source_sha256, args.exiftool, args.v1_report)
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(json.dumps({"status": report["status"], "output": str(args.output),
                          "sourceSha256": report["source"]["sha256"],
                          "intervalSha256": report["discrepancy"]["intervalSha256"],
                          "classification": report["discrepancy"]["classification"],
                          "strictV1PredicateStillFails": report["strictV1PredicateStillFails"]},
                         indent=2, ensure_ascii=False))
        return 0
    except (ForensicError, OSError, KeyError, ValueError, StopIteration) as exc:
        print(f"Forensic report failed closed: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
