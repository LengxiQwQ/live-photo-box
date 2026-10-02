#!/usr/bin/env python3
"""Independent P5-R3 JPEG/HEIF structure, decode, and reconstruction validator."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import shutil
import struct
import subprocess
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, field
from io import BytesIO
from pathlib import Path
from typing import Any

import numpy as np
from PIL import Image, ImageCms, ImageOps, __version__ as PILLOW_VERSION


ROOT = Path(__file__).resolve().parents[2]
HDRGM = "http://ns.adobe.com/hdr-gain-map/1.0/"
APPLE_HDRGM = "http://ns.apple.com/HDRGainMap/1.0/"
CONTAINER = "http://ns.google.com/photos/1.0/container/"
ITEM = "http://ns.google.com/photos/1.0/container/item/"
RDF = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
ISO_TAGS = (
    "Version",
    "GainMapMin",
    "GainMapMax",
    "Gamma",
    "OffsetSDR",
    "OffsetHDR",
    "HDRCapacityMin",
    "HDRCapacityMax",
    "BaseRenditionIsHDR",
)


class ValidationError(RuntimeError):
    pass


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValidationError(message)


def run_command(args: list[str], *, cwd: Path | None = None) -> dict[str, Any]:
    completed = subprocess.run(
        args,
        cwd=str(cwd) if cwd else None,
        capture_output=True,
        text=True,
        errors="replace",
        check=False,
    )
    result = {
        "argv": args,
        "cwd": str(cwd) if cwd else None,
        "exitCode": completed.returncode,
        "stdout": completed.stdout,
        "stderr": completed.stderr,
    }
    if completed.returncode != 0:
        raise ValidationError(
            f"Command failed ({completed.returncode}): {args!r}\n"
            f"stdout: {completed.stdout[-2000:]}\n"
            f"stderr: {completed.stderr[-2000:]}"
        )
    return result


def tool_version(name: str, args: list[str]) -> dict[str, Any]:
    executable = shutil.which(name)
    require(executable is not None, f"Required independent validation tool is missing: {name}")
    result = run_command([executable, *args])
    return {"path": executable, "versionOutput": (result["stdout"] + result["stderr"]).strip()}


def box_iter(data: bytes, start: int, end: int):
    position = start
    while position < end:
        require(end - position >= 8, f"Truncated box header at 0x{position:X}.")
        size32 = int.from_bytes(data[position : position + 4], "big")
        box_type = data[position + 4 : position + 8].decode("latin-1")
        header_size = 8
        if size32 == 1:
            require(end - position >= 16, f"Truncated extended box header for {box_type}.")
            size = int.from_bytes(data[position + 8 : position + 16], "big")
            header_size = 16
        elif size32 == 0:
            size = end - position
        else:
            size = size32
        require(size >= header_size and position + size <= end, f"Invalid {box_type} box size {size}.")
        yield {
            "type": box_type,
            "start": position,
            "payloadStart": position + header_size,
            "end": position + size,
            "size": size,
        }
        position += size
    require(position == end, "Box traversal did not end at the enclosing boundary.")


def one_box(boxes: list[dict[str, Any]], box_type: str, *, required: bool = True) -> dict[str, Any] | None:
    found = [box for box in boxes if box["type"] == box_type]
    if required:
        require(len(found) == 1, f"Expected one {box_type} box, found {len(found)}.")
    else:
        require(len(found) <= 1, f"Ambiguous duplicate {box_type} boxes.")
    return found[0] if found else None


def read_uint(data: bytes, offset: int, size: int, endian: str = "big") -> int:
    require(size in (0, 1, 2, 3, 4, 8), f"Unsupported integer width {size}.")
    require(offset >= 0 and offset + size <= len(data), "Integer read is outside the file.")
    return int.from_bytes(data[offset : offset + size], endian) if size else 0


@dataclass
class HeifItem:
    item_id: int
    item_type: str
    name: str
    content_type: str | None = None
    content_encoding: str | None = None
    construction_method: int = 0
    base_offset: int = 0
    extents: list[tuple[int, int]] = field(default_factory=list)
    property_indices: list[int] = field(default_factory=list)
    properties: list[dict[str, Any]] = field(default_factory=list)


@dataclass
class HeifFacts:
    path: Path
    file_hash: str
    primary_item_id: int
    items: dict[int, HeifItem]
    references: list[tuple[str, int, int]]
    idat_payload_start: int | None

    def item_bytes(self, item_id: int) -> bytes:
        item = self.items[item_id]
        pieces = []
        for offset, length in item.extents:
            require(length > 0 and offset + length <= self.path.stat().st_size,
                    f"HEIF item {item_id} has an out-of-file extent.")
            with self.path.open("rb") as stream:
                stream.seek(offset)
                payload = stream.read(length)
            require(len(payload) == length, f"HEIF item {item_id} extent was truncated during read.")
            pieces.append(payload)
        return b"".join(pieces)

    def item_summary(self, item_id: int) -> dict[str, Any]:
        item = self.items[item_id]
        payload = self.item_bytes(item_id)
        width = None
        height = None
        aux_type = None
        for prop in item.properties:
            if prop["type"] == "ispe":
                width, height = prop["width"], prop["height"]
            if prop["type"] == "auxC":
                aux_type = prop["auxType"]
        return {
            "itemId": item.item_id,
            "itemType": item.item_type,
            "name": item.name,
            "contentType": item.content_type,
            "extents": [{"offset": offset, "length": length} for offset, length in item.extents],
            "payloadLength": len(payload),
            "payloadSha256": sha256_bytes(payload),
            "width": width,
            "height": height,
            "auxType": aux_type,
            "propertyTypes": [prop["type"] for prop in item.properties],
        }


def _parse_infe(data: bytes, box: dict[str, Any]) -> dict[str, Any]:
    pos = box["payloadStart"]
    version = data[pos]
    pos += 4
    require(version in (2, 3), f"Unsupported HEIF infe version {version}.")
    item_id_size = 2 if version == 2 else 4
    item_id = read_uint(data, pos, item_id_size)
    pos += item_id_size
    protection = read_uint(data, pos, 2)
    pos += 2
    item_type = data[pos : pos + 4].decode("latin-1")
    pos += 4
    item_name_end = data.find(b"\0", pos, box["end"])
    require(item_name_end >= 0, "HEIF infe item name is not null terminated.")
    name = data[pos:item_name_end].decode("utf-8", "strict")
    pos = item_name_end + 1
    content_type = None
    content_encoding = None
    if item_type == "mime":
        end = data.find(b"\0", pos, box["end"])
        require(end >= 0, f"HEIF MIME item {item_id} has no content type.")
        content_type = data[pos:end].decode("ascii", "strict")
        pos = end + 1
        if pos < box["end"]:
            end = data.find(b"\0", pos, box["end"])
            require(end >= 0, f"HEIF MIME item {item_id} has malformed content encoding.")
            content_encoding = data[pos:end].decode("ascii", "strict")
    return {
        "itemId": item_id,
        "itemType": item_type,
        "name": name,
        "contentType": content_type,
        "contentEncoding": content_encoding,
        "protection": protection,
    }


def parse_heif(path: Path) -> HeifFacts:
    data = path.read_bytes()
    top = list(box_iter(data, 0, len(data)))
    meta = one_box(top, "meta")
    assert meta is not None
    meta_children = list(box_iter(data, meta["payloadStart"] + 4, meta["end"]))

    pitm = one_box(meta_children, "pitm")
    assert pitm is not None
    pitm_version = data[pitm["payloadStart"]]
    primary_pos = pitm["payloadStart"] + 4
    primary_id = read_uint(data, primary_pos, 2 if pitm_version == 0 else 4)

    iinf = one_box(meta_children, "iinf")
    assert iinf is not None
    iinf_version = data[iinf["payloadStart"]]
    position = iinf["payloadStart"] + 4
    entry_count = read_uint(data, position, 2 if iinf_version == 0 else 4)
    position += 2 if iinf_version == 0 else 4
    item_infos = {}
    for child in box_iter(data, position, iinf["end"]):
        if child["type"] == "infe":
            info = _parse_infe(data, child)
            require(info["itemId"] not in item_infos, f"Duplicate HEIF item id {info['itemId']}.")
            item_infos[info["itemId"]] = info
    require(len(item_infos) == entry_count, "HEIF iinf entry count disagrees with its infe children.")
    require(primary_id in item_infos, "HEIF pitm primary item does not exist in iinf.")

    iloc = one_box(meta_children, "iloc")
    assert iloc is not None
    version = data[iloc["payloadStart"]]
    require(version in (0, 1, 2), f"Unsupported HEIF iloc version {version}.")
    position = iloc["payloadStart"] + 4
    offset_size = data[position] >> 4
    length_size = data[position] & 0x0F
    base_size = data[position + 1] >> 4
    index_size = (data[position + 1] & 0x0F) if version in (1, 2) else 0
    position += 2
    count_size = 4 if version == 2 else 2
    location_count = read_uint(data, position, count_size)
    position += count_size
    locations: dict[int, tuple[int, int, list[tuple[int, int]]]] = {}
    for _ in range(location_count):
        item_id = read_uint(data, position, 4 if version == 2 else 2)
        position += 4 if version == 2 else 2
        construction = 0
        if version in (1, 2):
            construction = read_uint(data, position, 2) & 0x0FFF
            position += 2
        data_reference_index = read_uint(data, position, 2)
        position += 2
        base_offset = read_uint(data, position, base_size)
        position += base_size
        extent_count = read_uint(data, position, 2)
        position += 2
        extents = []
        for _extent in range(extent_count):
            if version in (1, 2) and index_size:
                position += index_size
            extent_offset = read_uint(data, position, offset_size)
            position += offset_size
            extent_length = read_uint(data, position, length_size)
            position += length_size
            extents.append((extent_offset, extent_length))
        require(data_reference_index == 0, f"HEIF item {item_id} uses an external data reference.")
        locations[item_id] = (construction, base_offset, extents)
    require(position == iloc["end"], "HEIF iloc has trailing or malformed bytes.")

    idat = one_box(meta_children, "idat", required=False)
    idat_payload_start = idat["payloadStart"] if idat else None
    iprp = one_box(meta_children, "iprp", required=False)
    property_boxes: list[dict[str, Any]] = []
    property_associations: dict[int, list[int]] = {}
    if iprp:
        iprp_children = list(box_iter(data, iprp["payloadStart"], iprp["end"]))
        ipco = one_box(iprp_children, "ipco")
        ipma = one_box(iprp_children, "ipma")
        assert ipco is not None and ipma is not None
        property_boxes = list(box_iter(data, ipco["payloadStart"], ipco["end"]))
        ipma_version = data[ipma["payloadStart"]]
        ipma_flags = int.from_bytes(data[ipma["payloadStart"] + 1 : ipma["payloadStart"] + 4], "big")
        position = ipma["payloadStart"] + 4
        association_entries = read_uint(data, position, 4)
        position += 4
        for _ in range(association_entries):
            item_id = read_uint(data, position, 4 if ipma_version >= 1 else 2)
            position += 4 if ipma_version >= 1 else 2
            count = read_uint(data, position, 1)
            position += 1
            indices = []
            for _index in range(count):
                if ipma_flags & 1:
                    value = read_uint(data, position, 2)
                    position += 2
                    property_index = value & 0x7FFF
                else:
                    value = read_uint(data, position, 1)
                    position += 1
                    property_index = value & 0x7F
                if property_index:
                    indices.append(property_index)
            require(item_id not in property_associations, f"Duplicate HEIF ipma entry for item {item_id}.")
            property_associations[item_id] = indices
        require(position == ipma["end"], "HEIF ipma has trailing or malformed bytes.")

    def property_info(prop: dict[str, Any]) -> dict[str, Any]:
        result: dict[str, Any] = {"type": prop["type"], "size": prop["size"]}
        payload = data[prop["payloadStart"] : prop["end"]]
        if prop["type"] == "auxC":
            require(len(payload) >= 5, "HEIF auxC property is truncated.")
            end = payload.find(b"\0", 4)
            require(end > 4, "HEIF auxC property has no auxiliary type.")
            result["auxType"] = payload[4:end].decode("utf-8", "strict")
        elif prop["type"] == "ispe":
            require(len(payload) >= 12, "HEIF ispe property is truncated.")
            result["width"] = int.from_bytes(payload[4:8], "big")
            result["height"] = int.from_bytes(payload[8:12], "big")
        return result

    items: dict[int, HeifItem] = {}
    for item_id, info in item_infos.items():
        require(item_id in locations, f"HEIF item {item_id} has no iloc entry.")
        construction, base_offset, raw_extents = locations[item_id]
        require(construction in (0, 1), f"HEIF item {item_id} uses unsupported construction method {construction}.")
        extents = []
        for extent_offset, extent_length in raw_extents:
            absolute = base_offset + extent_offset
            if construction == 1:
                require(idat_payload_start is not None, f"HEIF item {item_id} uses idat without an idat box.")
                absolute += idat_payload_start
            extents.append((absolute, extent_length))
        indices = property_associations.get(item_id, [])
        properties = []
        for index in indices:
            require(1 <= index <= len(property_boxes), f"HEIF item {item_id} refers to invalid property index {index}.")
            properties.append(property_info(property_boxes[index - 1]))
        items[item_id] = HeifItem(
            item_id=item_id,
            item_type=info["itemType"],
            name=info["name"],
            content_type=info["contentType"],
            content_encoding=info["contentEncoding"],
            construction_method=construction,
            base_offset=base_offset,
            extents=extents,
            property_indices=indices,
            properties=properties,
        )

    references = []
    iref = one_box(meta_children, "iref", required=False)
    if iref:
        iref_version = data[iref["payloadStart"]]
        id_size = 4 if iref_version >= 1 else 2
        for ref_box in box_iter(data, iref["payloadStart"] + 4, iref["end"]):
            position = ref_box["payloadStart"]
            from_id = read_uint(data, position, id_size)
            position += id_size
            ref_count = read_uint(data, position, 2)
            position += 2
            for _ in range(ref_count):
                to_id = read_uint(data, position, id_size)
                position += id_size
                references.append((ref_box["type"], from_id, to_id))
            require(position == ref_box["end"], f"HEIF {ref_box['type']} reference has trailing bytes.")

    return HeifFacts(path, sha256_bytes(data), primary_id, items, references, idat_payload_start)


def _jpeg_find_next_marker(data: bytes, start: int) -> tuple[int, int]:
    search = start
    while True:
        marker_start = data.find(b"\xFF", search)
        require(marker_start >= 0, "JPEG codestream has no EOI marker.")
        code_pos = marker_start + 1
        while code_pos < len(data) and data[code_pos] == 0xFF:
            code_pos += 1
        require(code_pos < len(data), "JPEG codestream ends inside a marker.")
        code = data[code_pos]
        if code == 0x00 or 0xD0 <= code <= 0xD7:
            search = code_pos + 1
            continue
        return marker_start, code


def parse_jpeg_codestream(data: bytes, start: int, limit: int) -> dict[str, Any]:
    require(start + 2 <= limit and data[start : start + 2] == b"\xFF\xD8", f"JPEG SOI missing at 0x{start:X}.")
    pos = start + 2
    segments = []
    saw_scan = False
    while pos < limit:
        require(data[pos] == 0xFF, f"JPEG marker prefix missing at 0x{pos:X}.")
        marker_start, marker = _jpeg_find_next_marker(data, pos)
        if marker == 0xD9:
            return {"start": start, "end": marker_start + 2, "segments": segments}
        code_pos = marker_start + 1
        while code_pos < limit and data[code_pos] == 0xFF:
            code_pos += 1
        if marker in (0xD8, 0x01) or 0xD0 <= marker <= 0xD7:
            segments.append({"marker": marker, "start": marker_start, "payloadStart": code_pos + 1, "payloadEnd": code_pos + 1})
            pos = code_pos + 1
            continue
        require(code_pos + 3 <= limit, "JPEG segment length is truncated.")
        length = int.from_bytes(data[code_pos + 1 : code_pos + 3], "big")
        require(length >= 2 and code_pos + 1 + length <= limit, "JPEG segment length is outside its codestream.")
        payload_start = code_pos + 3
        payload_end = code_pos + 1 + length
        segments.append({"marker": marker, "start": marker_start, "payloadStart": payload_start, "payloadEnd": payload_end})
        pos = payload_end
        if marker == 0xDA:
            saw_scan = True
            next_marker, _ = _jpeg_find_next_marker(data, pos)
            pos = next_marker
    raise ValidationError("JPEG codestream has no terminating EOI marker.")


def parse_mpf(data: bytes, segments: list[dict[str, Any]]) -> list[dict[str, Any]]:
    mpf = [segment for segment in segments if segment["marker"] == 0xE2 and
           data[segment["payloadStart"] : segment["payloadStart"] + 4] == b"MPF\0"]
    require(len(mpf) == 1, f"Expected exactly one JPEG MPF APP2 marker, found {len(mpf)}.")
    tiff = mpf[0]["payloadStart"] + 4
    byte_order = data[tiff : tiff + 2]
    require(byte_order in (b"II", b"MM"), "MPF TIFF byte order is invalid.")
    endian = "little" if byte_order == b"II" else "big"
    require(read_uint(data, tiff + 2, 2, endian) == 42, "MPF TIFF magic is invalid.")
    ifd_offset = read_uint(data, tiff + 4, 4, endian)
    ifd = tiff + ifd_offset
    count = read_uint(data, ifd, 2, endian)
    entries = {}
    pos = ifd + 2
    for _ in range(count):
        tag = read_uint(data, pos, 2, endian)
        kind = read_uint(data, pos + 2, 2, endian)
        item_count = read_uint(data, pos + 4, 4, endian)
        value = data[pos + 8 : pos + 12]
        entries[tag] = (kind, item_count, value)
        pos += 12
    require(0xB001 in entries and 0xB002 in entries, "MPF NumberOfImages or MPEntry is missing.")
    number_kind, number_count, number_raw = entries[0xB001]
    require(number_kind == 4 and number_count == 1, "MPF NumberOfImages field has an invalid type/count.")
    number = int.from_bytes(number_raw, endian)
    mpentry_kind, mpentry_bytes_count, mpentry_raw = entries[0xB002]
    require(mpentry_kind == 7 and mpentry_bytes_count == number * 16, "MPF MPEntry field has an invalid type/size.")
    mpentry_offset = int.from_bytes(mpentry_raw, endian)
    if mpentry_bytes_count <= 4:
        entry_data = mpentry_raw[:mpentry_bytes_count]
    else:
        entry_data = data[tiff + mpentry_offset : tiff + mpentry_offset + mpentry_bytes_count]
    require(len(entry_data) == number * 16, "MPF MPEntry data is truncated.")
    result = []
    for index in range(number):
        entry = entry_data[index * 16 : (index + 1) * 16]
        relative_offset = int.from_bytes(entry[8:12], endian)
        result.append({
            "index": index,
            "attributes": int.from_bytes(entry[0:4], endian),
            "size": int.from_bytes(entry[4:8], endian),
            "relativeOffset": relative_offset,
            # CIPA DC-007 defines MPEntry offsets relative to the MP Endian
            # field, which is the first byte of this TIFF header.  The first
            # image is the one exception and is represented by offset zero.
            "offset": 0 if index == 0 else tiff + relative_offset,
            "dependentImage1": int.from_bytes(entry[12:14], endian),
            "dependentImage2": int.from_bytes(entry[14:16], endian),
        })
    return result


def jpeg_xmp_packets(data: bytes, segments: list[dict[str, Any]]) -> list[bytes]:
    prefix = b"http://ns.adobe.com/xap/1.0/\0"
    packets = []
    for segment in segments:
        if segment["marker"] == 0xE1:
            payload = data[segment["payloadStart"] : segment["payloadEnd"]]
            if payload.startswith(prefix):
                packets.append(payload[len(prefix) :])
    return packets


def parse_container_directory(packet: bytes) -> list[dict[str, Any]] | None:
    try:
        root = ET.fromstring(packet)
    except ET.ParseError:
        return None
    items = []
    for element in root.iter(f"{{{CONTAINER}}}Item"):
        semantic = element.attrib.get(f"{{{ITEM}}}Semantic")
        mime = element.attrib.get(f"{{{ITEM}}}Mime")
        length = element.attrib.get(f"{{{ITEM}}}Length")
        padding = element.attrib.get(f"{{{ITEM}}}Padding")
        items.append({
            "semantic": semantic,
            "mime": mime,
            "length": int(length) if length is not None and length.isdigit() else None,
            "padding": int(padding) if padding is not None and padding.isdigit() else None,
        })
    return items or None


def parse_iso_metadata(packets: list[bytes]) -> tuple[dict[str, Any] | None, list[dict[str, Any]]]:
    candidates = []
    marker_only = []
    for packet_index, packet in enumerate(packets):
        try:
            root = ET.fromstring(packet)
        except ET.ParseError as error:
            raise ValidationError(f"XMP packet {packet_index} is malformed XML: {error}") from error
        values: dict[str, list[str]] = {name: [] for name in ISO_TAGS}
        for element in root.iter():
            for name in ISO_TAGS:
                key = f"{{{HDRGM}}}{name}"
                if key in element.attrib:
                    values[name].append(element.attrib[key].strip())
                if element.tag == key and element.text:
                    values[name].append(element.text.strip())
        present = {name: found for name, found in values.items() if found}
        if not present:
            continue
        require(all(len(found) == 1 for found in present.values()),
                f"XMP packet {packet_index} contains duplicate ISO GainMap properties.")
        if set(present) == {"Version"} and present["Version"][0] == "1.0":
            marker_only.append({"packetIndex": packet_index, "version": "1.0"})
            continue
        require(set(present) == set(ISO_TAGS),
                f"XMP packet {packet_index} has incomplete ISO GainMap metadata ({sorted(present)}).")
        raw = {name: found[0] for name, found in values.items()}
        require(raw["Version"] == "1.0", f"XMP packet {packet_index} has unsupported GainMap version.")
        try:
            metadata = {name: float(raw[name]) for name in ISO_TAGS if name != "BaseRenditionIsHDR" and name != "Version"}
        except ValueError as error:
            raise ValidationError(f"XMP packet {packet_index} has non-numeric ISO metadata.") from error
        require(all(math.isfinite(value) for value in metadata.values()),
                f"XMP packet {packet_index} has non-finite ISO metadata.")
        require(raw["BaseRenditionIsHDR"] in ("True", "False"),
                f"XMP packet {packet_index} has an invalid BaseRenditionIsHDR value.")
        metadata["Version"] = raw["Version"]
        metadata["BaseRenditionIsHDR"] = raw["BaseRenditionIsHDR"] == "True"
        require(metadata["Gamma"] > 0 and metadata["GainMapMin"] <= metadata["GainMapMax"] and
                metadata["HDRCapacityMin"] >= 0 and metadata["HDRCapacityMax"] > metadata["HDRCapacityMin"] and
                metadata["OffsetSDR"] >= 0 and metadata["OffsetHDR"] >= 0,
                f"XMP packet {packet_index} has invalid ISO GainMap ranges or offsets.")
        candidates.append((packet_index, metadata))
    require(len(candidates) <= 1,
            f"Found {len(candidates)} complete ISO metadata packets; the mapping is ambiguous.")
    return (candidates[0][1] if candidates else None), marker_only


def parse_jpeg(path: Path, sidecar_dir: Path | None = None) -> dict[str, Any]:
    data = path.read_bytes()
    primary = parse_jpeg_codestream(data, 0, len(data))
    entries = parse_mpf(data, primary["segments"])
    require(len(entries) == 2, f"Expected two MPF images, found {len(entries)}.")
    require(entries[0]["offset"] == 0, "MPF primary image offset is not rooted at the file SOI.")
    primary_mpf_size_matches_marker_range = entries[0]["size"] == primary["end"]
    gain = entries[1]
    require(gain["offset"] == primary["end"] and gain["size"] > 0 and
            gain["offset"] + gain["size"] <= len(data), "MPF GainMap range is invalid or overlaps the primary.")
    gain_codestream = parse_jpeg_codestream(data, gain["offset"], gain["offset"] + gain["size"])
    require(gain_codestream["end"] == gain["offset"] + gain["size"],
            "MPF GainMap range does not contain exactly one complete JPEG codestream.")
    primary_packets = jpeg_xmp_packets(data, primary["segments"])
    # The MPF codestream parser returns absolute marker positions into `data`,
    # so inspect XMP against the same byte array rather than a sliced copy.
    gain_packets = jpeg_xmp_packets(data, gain_codestream["segments"])
    directories = [directory for packet in primary_packets if (directory := parse_container_directory(packet))]
    require(len(directories) == 1, f"Expected one JPEG XMP Container Directory, found {len(directories)}.")
    directory = directories[0]
    primary_entries = [item for item in directory if item["semantic"] == "Primary"]
    gain_entries = [item for item in directory if item["semantic"] == "GainMap"]
    require(len(primary_entries) == 1 and len(gain_entries) == 1,
            "JPEG XMP Container Directory must identify one Primary and one GainMap item.")
    require(gain_entries[0]["mime"] == "image/jpeg" and gain_entries[0]["length"] == gain["size"],
            "JPEG XMP GainMap item MIME/length differs from its exact MPF range.")
    metadata, markers = parse_iso_metadata(primary_packets + gain_packets)
    require(metadata is not None, "JPEG source/target has no complete ISO GainMap metadata packet.")
    gain_bytes = data[gain["offset"] : gain["offset"] + gain["size"]]
    if sidecar_dir:
        sidecar_dir.mkdir(parents=True, exist_ok=False)
        (sidecar_dir / "primary-codestream.jpg").write_bytes(data[: primary["end"]])
        (sidecar_dir / "gainmap-codestream.jpg").write_bytes(gain_bytes)
    return {
        "container": "jpeg",
        "fileSha256": sha256_bytes(data),
        "fileSize": len(data),
        "primary": {"offset": 0, "length": primary["end"], "sha256": sha256_bytes(data[: primary["end"]])},
        "mpf": {"imageCount": len(entries), "entries": entries,
                "primarySizeMatchesMarkerRange": primary_mpf_size_matches_marker_range},
        "gainMap": {"offset": gain["offset"], "length": gain["size"], "sha256": sha256_bytes(gain_bytes),
                    "xmpMarkerOnlyPackets": markers},
        "containerDirectory": directory,
        "metadata": metadata,
        "_primaryBytes": data[: primary["end"]],
        "_gainMapBytes": gain_bytes,
    }


def heif_item_with_aux(facts: HeifFacts, relationship: str | None = None) -> int:
    primary = facts.primary_item_id
    candidates = []
    for item_id, item in facts.items.items():
        aux_types = [prop["auxType"] for prop in item.properties if prop["type"] == "auxC"]
        if len(aux_types) == 1 and "gainmap" in aux_types[0].lower():
            if relationship is None or aux_types[0] == relationship:
                candidates.append(item_id)
    require(len(candidates) == 1, f"Expected one GainMap auxC item, found {len(candidates)}.")
    gain_id = candidates[0]
    aux_refs = [ref for ref in facts.references if ref[0] == "auxl" and ref[1] == gain_id and ref[2] == primary]
    require(len(aux_refs) == 1, "HEIF GainMap must have exactly one auxl relationship to the pitm primary.")
    conflicting = [ref for ref in facts.references if ref[0] == "auxl" and
                   (ref[1] == gain_id or ref[2] == gain_id) and ref not in aux_refs]
    require(not conflicting, "HEIF GainMap has duplicate or conflicting auxl ownership relationships.")
    return gain_id


def heif_xmp_packets(facts: HeifFacts, gain_id: int, *, require_bound: bool) -> tuple[list[bytes], list[int]]:
    xmp_items = [item for item in facts.items.values() if item.item_type == "mime" and
                 item.content_type in ("application/rdf+xml", "application/xml")]
    candidates = []
    for item in xmp_items:
        packet = facts.item_bytes(item.item_id)
        try:
            ET.fromstring(packet)
        except ET.ParseError:
            continue
        candidates.append((item, packet))
    bound = [(item, packet) for item, packet in candidates if
             ("cdsc", item.item_id, gain_id) in facts.references]
    if require_bound:
        require(len(bound) == 1, f"Expected exactly one item-bound cdsc XMP packet for GainMap {gain_id}, found {len(bound)}.")
        other_map_references = [ref for ref in facts.references if ref[0] == "cdsc" and ref[2] == gain_id and ref[1] != bound[0][0].item_id]
        require(not other_map_references, "HEIF GainMap has duplicate cdsc metadata owners.")
        return [bound[0][1]], [bound[0][0].item_id]
    return [packet for _, packet in candidates], [item.item_id for item, _ in candidates]


def heif_image_grid(facts: HeifFacts, image_item: HeifItem) -> dict[str, Any] | None:
    if image_item.item_type != "grid":
        require(image_item.item_type in ("hvc1", "hev1", "av01"),
                f"HEIF image item is not a supported coded image: {image_item.item_type}.")
        return None

    descriptor = facts.item_bytes(image_item.item_id)
    require(len(descriptor) in (8, 12), "HEIF grid item descriptor has an unsupported size.")
    version, flags, rows_minus_one, columns_minus_one = descriptor[:4]
    require(version == 0 and flags & 0xFE == 0,
            f"HEIF grid item descriptor has unsupported version/flags ({version}, {flags}).")
    dimension_size = 4 if flags & 1 else 2
    require(len(descriptor) == 4 + 2 * dimension_size,
            "HEIF grid descriptor dimensions do not match its flags.")
    output_width = int.from_bytes(descriptor[4 : 4 + dimension_size], "big")
    output_height = int.from_bytes(descriptor[4 + dimension_size : 4 + 2 * dimension_size], "big")
    rows = rows_minus_one + 1
    columns = columns_minus_one + 1
    require(output_width > 0 and output_height > 0, "HEIF grid output dimensions must be nonzero.")

    tile_ids = [to_id for kind, from_id, to_id in facts.references
                if kind == "dimg" and from_id == image_item.item_id]
    require(len(tile_ids) == rows * columns and len(set(tile_ids)) == len(tile_ids),
            "HEIF primary grid dimg references do not uniquely fill the declared rows and columns.")
    tiles = []
    for tile_id in tile_ids:
        require(tile_id in facts.items, f"HEIF grid references missing tile item {tile_id}.")
        tile = facts.items[tile_id]
        require(tile.item_type in ("hvc1", "hev1", "av01"),
                f"HEIF grid tile {tile_id} is not a supported coded image: {tile.item_type}.")
        summary = facts.item_summary(tile_id)
        require(summary["payloadLength"] > 0 and summary["width"] and summary["height"],
                f"HEIF grid tile {tile_id} lacks coded bytes or ispe dimensions.")
        tiles.append(summary)

    tile_widths = {tile["width"] for tile in tiles}
    tile_heights = {tile["height"] for tile in tiles}
    require(len(tile_widths) == 1 and len(tile_heights) == 1,
            "HEIF primary grid tiles do not have a consistent coded size.")
    tile_width = next(iter(tile_widths))
    tile_height = next(iter(tile_heights))
    require((columns - 1) * tile_width < output_width <= columns * tile_width and
            (rows - 1) * tile_height < output_height <= rows * tile_height,
            "HEIF grid output dimensions cannot be reconstructed from its ordered tiles.")
    ispe = [prop for prop in image_item.properties if prop["type"] == "ispe"]
    require(len(ispe) == 1 and ispe[0]["width"] == output_width and ispe[0]["height"] == output_height,
            "HEIF grid descriptor dimensions disagree with its ispe property.")
    return {
        "version": version,
        "flags": flags,
        "rows": rows,
        "columns": columns,
        "outputWidth": output_width,
        "outputHeight": output_height,
        "tileWidth": tile_width,
        "tileHeight": tile_height,
        "tileItemIdsInReferenceOrder": tile_ids,
        "tiles": tiles,
    }


def parse_heif_representation(path: Path, sidecar_dir: Path | None = None,
                              expected_relationship: str | None = None,
                              require_bound_xmp: bool = False) -> dict[str, Any]:
    facts = parse_heif(path)
    primary = facts.items[facts.primary_item_id]
    primary_grid = heif_image_grid(facts, primary)
    gain_id = heif_item_with_aux(facts, expected_relationship)
    gain_item = facts.items[gain_id]
    gain_map_grid = heif_image_grid(facts, gain_item)
    for item_id in (facts.primary_item_id, gain_id):
        summary = facts.item_summary(item_id)
        require(summary["payloadLength"] > 0 and summary["width"] and summary["height"],
                f"HEIF image item {item_id} lacks extents or ispe dimensions.")
    xmp_packets, xmp_item_ids = heif_xmp_packets(facts, gain_id, require_bound=require_bound_xmp)
    metadata, markers = parse_iso_metadata(xmp_packets)
    if expected_relationship == "urn:com:photo:aux:hdrgainmap" or require_bound_xmp:
        require(metadata is not None, "Generic HEIF GainMap lacks complete ISO XMP metadata.")
    map_summary = facts.item_summary(gain_id)
    if sidecar_dir:
        sidecar_dir.mkdir(parents=True, exist_ok=False)
        (sidecar_dir / "gainmap-item-payload.bin").write_bytes(facts.item_bytes(gain_id))
        if gain_map_grid is not None:
            for index, tile_id in enumerate(gain_map_grid["tileItemIdsInReferenceOrder"]):
                (sidecar_dir / f"gainmap-tile-{index:03d}-item-{tile_id}.bin").write_bytes(
                    facts.item_bytes(tile_id)
                )
    return {
        "container": "heif",
        "fileSha256": facts.file_hash,
        "fileSize": path.stat().st_size,
        "primaryItemId": facts.primary_item_id,
        "primary": facts.item_summary(facts.primary_item_id),
        "primaryGrid": primary_grid,
        "gainMapItemId": gain_id,
        "gainMap": map_summary,
        "gainMapGrid": gain_map_grid,
        "references": [
            {"type": kind, "fromItemId": from_id, "toItemId": to_id}
            for kind, from_id, to_id in facts.references
        ],
        "xmpItemIds": xmp_item_ids,
        "metadata": metadata,
        "xmpMarkerOnlyPackets": markers,
        "_facts": facts,
        "_gainMapItemId": gain_id,
    }


def exiftool_apple_facts(path: Path, work_dir: Path,
                         evidence_payload_dir: Path | None = None) -> dict[str, Any]:
    exiftool = shutil.which("exiftool")
    require(exiftool is not None, "ExifTool is required for independent Apple MakerNote reading.")
    source_hash = sha256_file(path)
    tool_input = work_dir / "apple-exiftool-source.heic"
    require(not tool_input.exists(), f"Refusing to overwrite ExifTool input copy {tool_input}.")
    shutil.copyfile(path, tool_input)
    copy_hash = sha256_file(tool_input)
    require(copy_hash == source_hash,
            "ASCII-path ExifTool copy does not match the hash-locked Apple HEIF source.")
    if evidence_payload_dir:
        evidence_copy = evidence_payload_dir / "apple-exiftool-source.heic"
        require(not evidence_copy.exists(), f"Refusing to overwrite ExifTool evidence copy {evidence_copy}.")
        shutil.copyfile(tool_input, evidence_copy)
        require(sha256_file(evidence_copy) == source_hash,
                "Archived ExifTool input copy does not match the hash-locked Apple HEIF source.")
    result = run_command([
        exiftool,
        "-json", "-n", "-G1", "-a", "-s", "-charset", "filename=UTF8",
        "-Apple:HDRHeadroom", "-Apple:HDRGain", "-QuickTime:AuxiliaryImageType", str(tool_input.resolve()),
    ])
    try:
        rows = json.loads(result["stdout"])
    except json.JSONDecodeError as error:
        raise ValidationError(f"ExifTool did not return valid JSON for {path.name}: {error}") from error
    require(len(rows) == 1, f"ExifTool returned {len(rows)} records for {path.name}.")
    row = rows[0]
    headroom = [value for key, value in row.items() if key.endswith(":HDRHeadroom")]
    gain = [value for key, value in row.items() if key.endswith(":HDRGain")]
    auxiliary = [value for key, value in row.items() if key.endswith(":AuxiliaryImageType")]
    require(len(headroom) == 1 and len(gain) == 1 and len(auxiliary) == 1,
            "Apple MakerNote mapping values or HEIF auxiliary type are missing/ambiguous.")
    return {"headroomTag": float(headroom[0]), "gainTag": float(gain[0]), "auxiliaryType": str(auxiliary[0]),
            "rawTags": row, "command": result, "sourceSha256": source_hash,
            "exiftoolInputCopySha256": copy_hash, "exiftoolInputCopyPath": str(tool_input.resolve())}


def decode_heif(path: Path, destination: Path, expected_aux_type: str) -> dict[str, Any]:
    require(not destination.exists(), f"Refusing to overwrite decoder directory {destination}.")
    destination.mkdir(parents=True)
    output = destination / "primary.png"
    result = run_command([shutil.which("heif-convert") or "heif-convert", "--with-aux", "--quiet", str(path), str(output)])
    require(output.is_file() and output.stat().st_size > 0, "Independent HEIF decoder did not write its primary image.")
    auxiliary_files = [candidate for candidate in destination.glob("*.png") if candidate != output]
    require(len(auxiliary_files) == 1,
            f"Independent HEIF decoder wrote {len(auxiliary_files)} auxiliary PNGs; expected exactly one.")
    require(expected_aux_type.replace(":", "_").replace("/", "_") in auxiliary_files[0].name,
            f"Decoded auxiliary filename does not identify expected auxC type {expected_aux_type}.")
    primary = Image.open(output).copy()
    auxiliary = Image.open(auxiliary_files[0]).copy()
    return {
        "decoder": "heif-convert --with-aux",
        "command": result,
        "primaryPath": str(output),
        "primarySha256": sha256_file(output),
        "primarySize": list(primary.size),
        "primaryIccPresent": bool(primary.info.get("icc_profile")),
        "auxiliaryPath": str(auxiliary_files[0]),
        "auxiliarySha256": sha256_file(auxiliary_files[0]),
        "auxiliarySize": list(auxiliary.size),
        "auxiliaryIccPresent": bool(auxiliary.info.get("icc_profile")),
        "_primaryImage": primary,
        "_auxiliaryImage": auxiliary,
    }


def decode_jpeg(rep: dict[str, Any], destination: Path) -> dict[str, Any]:
    require(not destination.exists(), f"Refusing to overwrite decoder directory {destination}.")
    destination.mkdir(parents=True)
    primary_path = destination / "primary-codestream.jpg"
    gain_path = destination / "gainmap-codestream.jpg"
    primary_path.write_bytes(rep["_primaryBytes"])
    gain_path.write_bytes(rep["_gainMapBytes"])
    primary = Image.open(primary_path)
    primary.load()
    primary = ImageOps.exif_transpose(primary)
    gain = Image.open(gain_path)
    gain.load()
    return {
        "decoder": f"Pillow {PILLOW_VERSION} JPEG plugin",
        "primaryPath": str(primary_path),
        "primarySha256": sha256_file(primary_path),
        "primarySize": list(primary.size),
        "primaryIccPresent": bool(primary.info.get("icc_profile")),
        "auxiliaryPath": str(gain_path),
        "auxiliarySha256": sha256_file(gain_path),
        "auxiliarySize": list(gain.size),
        "auxiliaryIccPresent": bool(gain.info.get("icc_profile")),
        "_primaryImage": primary.copy(),
        "_auxiliaryImage": gain.copy(),
    }


def primary_srgb(image: Image.Image) -> np.ndarray:
    profile_bytes = image.info.get("icc_profile")
    image = ImageOps.exif_transpose(image).convert("RGB")
    if profile_bytes:
        try:
            source_profile = ImageCms.ImageCmsProfile(BytesIO(profile_bytes))
            target_profile = ImageCms.createProfile("sRGB")
            image = ImageCms.profileToProfile(
                image, source_profile, target_profile, outputMode="RGB", renderingIntent=0
            )
        except Exception as error:
            raise ValidationError(f"ICC profile could not be transformed to sRGB: {error}") from error
    return np.asarray(image, dtype=np.float64) / 255.0


def map_codes(image: Image.Image) -> np.ndarray:
    return np.asarray(image.convert("RGB"), dtype=np.float64) / 255.0


def resize_codes(codes: np.ndarray, size: tuple[int, int]) -> np.ndarray:
    image = Image.fromarray(np.uint8(np.clip(np.rint(codes * 255.0), 0, 255)), mode="RGB")
    return np.asarray(image.resize(size, Image.Resampling.BILINEAR), dtype=np.float64) / 255.0


def srgb_eotf(encoded: np.ndarray) -> np.ndarray:
    return np.where(encoded <= 0.04045, encoded / 12.92, np.power((encoded + 0.055) / 1.055, 2.4))


def rec709_eotf(encoded: np.ndarray) -> np.ndarray:
    return np.where(encoded < 0.081, encoded / 4.5, np.power((encoded + 0.099) / 1.099, 1.0 / 0.45))


def apple_iso_metadata(facts: dict[str, Any]) -> tuple[dict[str, Any], float]:
    headroom_tag = facts["headroomTag"]
    gain_tag = facts["gainTag"]
    require(math.isfinite(headroom_tag) and math.isfinite(gain_tag) and headroom_tag >= 0 and gain_tag >= 0,
            "Apple MakerNote values are non-finite or negative.")
    if headroom_tag < 1.0:
        stops = -20.0 * gain_tag + 1.8 if gain_tag <= 0.01 else -0.101 * gain_tag + 1.601
    else:
        stops = -70.0 * gain_tag + 3.0 if gain_tag <= 0.01 else -0.303 * gain_tag + 2.303
    headroom = math.pow(2.0, stops)
    require(math.isfinite(stops) and stops > 0 and math.isfinite(headroom) and headroom > 1,
            "Apple MakerNote values have no representable positive HDR headroom.")
    capacity = math.log2(headroom)
    metadata = {
        "Version": "1.0",
        "GainMapMin": 0.0,
        "GainMapMax": capacity,
        "Gamma": 1.0,
        "OffsetSDR": 0.0,
        "OffsetHDR": 0.0,
        "HDRCapacityMin": 0.0,
        "HDRCapacityMax": capacity,
        "BaseRenditionIsHDR": False,
    }
    return metadata, headroom


def apple_map_to_iso(codes: np.ndarray, headroom: float) -> np.ndarray:
    channel_delta = np.max(np.abs(codes - codes[..., :1]))
    require(channel_delta <= 1.0 / 255.0,
            f"Apple GainMap is not grayscale (maximum channel delta {channel_delta:.8f}); refusing to clamp.")
    linear_map = rec709_eotf(codes[..., 0])
    pixel_gain = 1.0 + (headroom - 1.0) * linear_map
    recovery = np.log2(pixel_gain) / math.log2(headroom)
    require(np.all(np.isfinite(recovery)) and np.min(recovery) >= 0 and np.max(recovery) <= 1,
            "Apple GainMap could not be mapped into normalized ISO code values without clamping.")
    return np.repeat(recovery[..., None], 3, axis=2)


def reconstruct_hdr(primary_encoded: np.ndarray, gain_codes: np.ndarray,
                    metadata: dict[str, Any]) -> np.ndarray:
    require(not metadata["BaseRenditionIsHDR"], "BaseRenditionIsHDR=true is outside this frozen reference.")
    base = srgb_eotf(primary_encoded)
    if gain_codes.shape[:2] != base.shape[:2]:
        gain_codes = resize_codes(gain_codes, (base.shape[1], base.shape[0]))
    recovery = np.power(gain_codes, 1.0 / metadata["Gamma"])
    log_gain = metadata["GainMapMin"] * (1.0 - recovery) + metadata["GainMapMax"] * recovery
    hdr = (base + metadata["OffsetSDR"]) * np.power(2.0, log_gain) - metadata["OffsetHDR"]
    require(np.all(np.isfinite(hdr)), "HDR reference reconstruction produced non-finite pixel values.")
    return hdr


def metrics(left: np.ndarray, right: np.ndarray) -> dict[str, float]:
    require(left.shape == right.shape, f"Reference arrays have different shapes: {left.shape} vs {right.shape}.")
    diff = np.abs(left - right)
    mae = float(np.mean(diff))
    p95 = float(np.percentile(diff, 95))
    mse = float(np.mean(np.square(left - right)))
    psnr = float("inf") if mse == 0 else 10.0 * math.log10(1.0 / mse)
    return {"mae": mae, "p95AbsoluteError": p95, "psnrDb": psnr}


def compare_metadata(source_meta: dict[str, Any], target_meta: dict[str, Any], tolerance: float) -> dict[str, Any]:
    comparisons = {}
    for key in ISO_TAGS:
        left, right = source_meta[key], target_meta[key]
        if key == "Version" or key == "BaseRenditionIsHDR":
            delta = 0.0 if left == right else 1.0
            passed = left == right
        else:
            delta = abs(float(left) - float(right))
            passed = delta <= tolerance
        comparisons[key] = {"source": left, "target": right, "absoluteDelta": delta, "pass": passed}
    return {"fields": comparisons, "pass": all(item["pass"] for item in comparisons.values())}


def metric_pass(values: dict[str, float], thresholds: dict[str, float]) -> dict[str, Any]:
    checks = {
        "mae": values["mae"] <= thresholds["maeMax"],
        "p95AbsoluteError": values["p95AbsoluteError"] <= thresholds["p95AbsoluteErrorMax"],
        "psnrDb": values["psnrDb"] >= thresholds["psnrDbMin"],
    }
    return {**values, "thresholds": thresholds, "checks": checks, "pass": all(checks.values())}


def _representation(path: Path, container: str, directory: Path,
                    expected_relationship: str | None, bound_xmp: bool,
                    sidecar_dir: Path | None) -> dict[str, Any]:
    if container == "jpeg":
        return parse_jpeg(path, sidecar_dir)
    return parse_heif_representation(path, sidecar_dir, expected_relationship, bound_xmp)


def _decode(path: Path, container: str, rep: dict[str, Any], directory: Path,
            expected_aux_type: str | None) -> dict[str, Any]:
    if container == "jpeg":
        return decode_jpeg(rep, directory)
    require(expected_aux_type is not None, "HEIF decode requires an independently verified auxC type.")
    return decode_heif(path, directory, expected_aux_type)


def _public_representation(rep: dict[str, Any]) -> dict[str, Any]:
    return {key: value for key, value in rep.items() if not key.startswith("_")}


def _serialize_report(value: Any) -> Any:
    if isinstance(value, Path):
        return str(value)
    if isinstance(value, np.generic):
        return value.item()
    if isinstance(value, float) and not math.isfinite(value):
        return "Infinity" if value > 0 else "-Infinity"
    if isinstance(value, dict):
        return {key: _serialize_report(item) for key, item in value.items() if not key.startswith("_")}
    if isinstance(value, (list, tuple)):
        return [_serialize_report(item) for item in value]
    return value


def validate_route(route: dict[str, Any], sample_dir: Path, artifact_dir: Path,
                   policy: dict[str, Any], route_work_dir: Path,
                   evidence_route_dir: Path | None) -> dict[str, Any]:
    source = sample_dir / route["sourceFile"]
    target = artifact_dir / route["targetFile"]
    require(source.is_file(), f"Canonical source sample is missing: {source}")
    require(target.is_file(), f"Converted output is missing: {target}")
    source_hash_before = sha256_file(source)
    require(source_hash_before == route["sourceSha256"],
            f"Canonical source SHA-256 mismatch for {route['sourceFile']}: {source_hash_before}")

    input_kind = "heif" if route["sourceContainer"].startswith("heif-") else "jpeg"
    output_kind = "heif" if route["targetContainer"].startswith("heif-") else "jpeg"
    source_relation = None
    target_relation = route["expectedGainMapRelationship"] if output_kind == "heif" else None
    if route["sourceContainer"] == "heif-apple-gainmap-auxiliary":
        source_relation = "urn:com:apple:photo:2020:aux:hdrgainmap"
    elif route["sourceContainer"] == "heif-samsung-iso-gainmap-auxiliary":
        source_relation = "urn:com:samsung:photo:2024:aux:hdrgainmap"

    input_structure_dir = evidence_route_dir / "input-payload" if evidence_route_dir else None
    output_structure_dir = evidence_route_dir / "output-payload" if evidence_route_dir else None
    input_rep = _representation(source, input_kind, route_work_dir, source_relation,
                                bound_xmp=(input_kind == "heif" and source_relation == "urn:com:photo:aux:hdrgainmap"),
                                sidecar_dir=input_structure_dir)
    output_rep = _representation(target, output_kind, route_work_dir, target_relation,
                                 bound_xmp=(output_kind == "heif"),
                                 sidecar_dir=output_structure_dir)

    apple_facts = None
    if route["sourceContainer"] == "heif-apple-gainmap-auxiliary":
        apple_facts = exiftool_apple_facts(
            source, route_work_dir, input_structure_dir
        )
        require(apple_facts["auxiliaryType"] == source_relation,
                "ExifTool Apple MakerNote metadata and independent auxC source relationship disagree.")
        input_metadata, apple_headroom = apple_iso_metadata(apple_facts)
        input_codes = None
    else:
        input_metadata = input_rep["metadata"]
        apple_headroom = None
        input_codes = None
    output_metadata = output_rep["metadata"]
    require(input_metadata is not None and output_metadata is not None,
            "Source and target both require complete ISO metadata or a complete supported Apple mapping.")

    decode_root = evidence_route_dir / "decode" if evidence_route_dir else route_work_dir / "decode"
    input_decode_dir = decode_root / "input"
    output_decode_dir = decode_root / "output"
    input_aux_type = source_relation if input_kind == "heif" else None
    output_aux_type = target_relation if output_kind == "heif" else None
    input_decode = _decode(source, input_kind, input_rep, input_decode_dir, input_aux_type)
    output_decode = _decode(target, output_kind, output_rep, output_decode_dir, output_aux_type)

    input_primary = primary_srgb(input_decode["_primaryImage"])
    output_primary = primary_srgb(output_decode["_primaryImage"])
    require(input_primary.shape == output_primary.shape,
            f"Primary rendered dimensions differ: {input_primary.shape[1]}x{input_primary.shape[0]} vs "
            f"{output_primary.shape[1]}x{output_primary.shape[0]}.")

    input_map = map_codes(input_decode["_auxiliaryImage"])
    output_map = map_codes(output_decode["_auxiliaryImage"])
    if apple_facts is not None:
        input_map = apple_map_to_iso(input_map, apple_headroom)
        input_metadata = input_metadata
    if input_map.shape[:2] != input_primary.shape[:2]:
        input_map = resize_codes(input_map, (input_primary.shape[1], input_primary.shape[0]))
    if output_map.shape[:2] != output_primary.shape[:2]:
        output_map = resize_codes(output_map, (output_primary.shape[1], output_primary.shape[0]))

    primary_values = metric_pass(
        metrics(srgb_eotf(input_primary), srgb_eotf(output_primary)),
        policy["comparisonMetrics"]["primaryLinearSrgb"],
    )
    gain_values = metric_pass(
        metrics(input_map, output_map),
        policy["comparisonMetrics"]["gainMapCode"],
    )
    metadata_delta = compare_metadata(
        input_metadata,
        output_metadata,
        policy["metadataTolerance"]["appleDerivedAbsoluteDeltaMax"] if apple_facts else
        policy["metadataTolerance"]["isoToIsoAbsoluteDeltaMax"],
    )
    hdr_input = reconstruct_hdr(input_primary, input_map, input_metadata)
    hdr_output = reconstruct_hdr(output_primary, output_map, output_metadata)
    normalizer = max(
        1.0,
        math.pow(2.0, input_metadata["HDRCapacityMax"]) - 1.0,
        math.pow(2.0, output_metadata["HDRCapacityMax"]) - 1.0,
    )
    hdr_values = metric_pass(
        metrics(hdr_input / normalizer, hdr_output / normalizer),
        policy["comparisonMetrics"]["reconstructedHdrHeadroomRelative"],
    )

    source_hash_after = sha256_file(source)
    require(source_hash_after == source_hash_before, "Canonical source hash changed during independent validation.")
    primary_dims = [input_primary.shape[1], input_primary.shape[0]]
    require(primary_dims == [output_primary.shape[1], output_primary.shape[0]],
            "Primary dimension preservation rule failed.")
    checks = {
        "canonicalSourceHashLocked": source_hash_before == route["sourceSha256"],
        "sourceUnchanged": source_hash_after == source_hash_before,
        "sourceMpfPrimaryEntrySizeMatchesMarkerRange":
            input_kind != "jpeg" or input_rep["mpf"]["primarySizeMatchesMarkerRange"],
        "targetMpfPrimaryEntrySizeMatchesMarkerRange":
            output_kind != "jpeg" or output_rep["mpf"]["primarySizeMatchesMarkerRange"],
        "primaryDimensionsMatch": primary_dims == [output_primary.shape[1], output_primary.shape[0]],
        "metadataMapping": metadata_delta["pass"],
        "primaryReference": primary_values["pass"],
        "gainMapReference": gain_values["pass"],
        "hdrReconstructionReference": hdr_values["pass"],
        "noSkip": True,
    }
    report = {
        "routeId": route["id"],
        "status": "PASS" if all(checks.values()) else "FAIL",
        "source": {"path": str(source), "sha256Before": source_hash_before, "sha256After": source_hash_after,
                   "declaredSha256": route["sourceSha256"]},
        "target": {"path": str(target), "sha256": sha256_file(target), "size": target.stat().st_size},
        "structure": {"input": _public_representation(input_rep), "output": _public_representation(output_rep)},
        "appleMakerNoteMapping": apple_facts,
        "metadata": {"inputIsoEquivalent": input_metadata, "outputIso": output_metadata, "comparison": metadata_delta},
        "decoding": {"input": {key: value for key, value in input_decode.items() if not key.startswith("_")},
                     "output": {key: value for key, value in output_decode.items() if not key.startswith("_")}},
        "renderedPrimaryDimensions": {"input": primary_dims,
                                      "output": [output_primary.shape[1], output_primary.shape[0]]},
        "referenceComparison": {"primaryLinearSrgb": primary_values,
                                "gainMapCode": gain_values,
                                "reconstructedHdrHeadroomRelative": hdr_values,
                                "hdrNormalizer": normalizer},
        "checks": checks,
    }
    if not all(checks.values()):
        failures = [name for name, passed in checks.items() if not passed]
        report["failure"] = f"Frozen comparisons failed: {', '.join(failures)}"
    return report


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--matrix", type=Path, default=ROOT / "tools/p5-r3-gainmap-validation/matrix-v1.json")
    parser.add_argument("--policy", type=Path, default=ROOT / "tools/p5-r3-gainmap-validation/reference-policy-v1.json")
    parser.add_argument("--sample-dir", type=Path, required=True)
    parser.add_argument("--artifact-dir", type=Path, required=True)
    parser.add_argument("--work-dir", type=Path, required=True)
    parser.add_argument("--evidence-dir", type=Path)
    args = parser.parse_args()

    matrix = json.loads(args.matrix.read_text(encoding="utf-8"))
    policy = json.loads(args.policy.read_text(encoding="utf-8"))
    require(matrix["schemaVersion"] == 1 and policy["schemaVersion"] == 1,
            "Unsupported matrix or reference policy schema.")
    routes = matrix["canonicalRoutes"]
    require(len(routes) == 4 and len({route["id"] for route in routes}) == 4,
            "R3 matrix must contain exactly four unique canonical routes.")
    require(policy["status"] == "frozen-before-formal-output-validation",
            "Reference assumptions were not frozen before output validation.")
    require(not args.work_dir.exists(), f"Refusing to overwrite working validation directory {args.work_dir}.")
    args.work_dir.mkdir(parents=True)
    evidence_dir = args.evidence_dir.resolve() if args.evidence_dir else None
    if evidence_dir:
        require(not evidence_dir.exists(), f"Refusing to overwrite formal evidence root {evidence_dir}.")
        evidence_dir.mkdir(parents=True)

    tools = {
        "python": {"versionOutput": sys.version},
        "pillow": {"version": PILLOW_VERSION},
        "numpy": {"version": np.__version__},
        "exiftool": tool_version("exiftool", ["-ver"]),
        "heifConvert": tool_version("heif-convert", ["--version"]),
        "ffmpeg": tool_version("ffmpeg", ["-version"]),
    }
    results = []
    for route in routes:
        route_evidence = evidence_dir / route["id"] if evidence_dir else None
        if route_evidence:
            require(not route_evidence.exists(), f"Refusing to overwrite route evidence {route_evidence}.")
            route_evidence.mkdir(parents=True)
        route_work = args.work_dir / route["id"]
        route_work.mkdir()
        try:
            result = validate_route(route, args.sample_dir, args.artifact_dir, policy, route_work, route_evidence)
        except Exception as error:
            result = {"routeId": route["id"], "status": "FAIL", "noSkip": True,
                      "failure": f"{type(error).__name__}: {error}"}
        results.append(result)
        if route_evidence:
            (route_evidence / "route-report.json").write_text(
                json.dumps(_serialize_report(result), indent=2, ensure_ascii=False) + "\n", encoding="utf-8"
            )

    report = {
        "schemaVersion": 1,
        "suiteId": matrix["suiteId"],
        "status": "PASS" if len(results) == 4 and all(item["status"] == "PASS" for item in results) else "FAIL",
        "routesExpected": 4,
        "routesExecuted": len(results),
        "skipped": 0,
        "matrixSha256": sha256_file(args.matrix),
        "referencePolicySha256": sha256_file(args.policy),
        "tools": tools,
        "routes": results,
    }
    serialized = json.dumps(_serialize_report(report), indent=2, ensure_ascii=False) + "\n"
    if evidence_dir:
        (evidence_dir / "run-report.json").write_text(serialized, encoding="utf-8")
    print(serialized, end="")
    return 0 if report["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
