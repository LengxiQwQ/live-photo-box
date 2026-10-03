#!/usr/bin/env python3
"""Independent, create-only P5-R5-v1 RealSample matrix and identity preflight runner.

This process never imports LivePhotoBox parsers. It asks the narrow managed
route driver to execute product routes, then verifies media with hash-locked
FFmpeg 8.0.1 tools.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path
from typing import Any, Iterable


RUNNER_VERSION = "P5-R5-v1-independent-runner-3"
EXPECTED_PROFILE_MANIFEST_SHA256 = "791EA58EB93CA40D8458146003121C1A65B79998DDE9B7785DCBC54E86E329DB"
EXPECTED_TOOL_ERRATUM_SHA256 = "62AC93A5454A71BF6703C6F3D3AC6D0EC66C4982B2F10EE0980C3D685A2EFED1"
EXPECTED_TOOL_ERRATUM_RELATIVE = Path(
    "tools/p5-r5-video-validation/profiles/P5-R5-v1/errata/tool-identity-E1.json"
)
EXPECTED_PROFILE_MANIFEST_RELATIVE = "tools/p5-r5-video-validation/profiles/P5-R5-v1/manifest.json"
EXPECTED_FFMPEG_VERSION = "ffmpeg version 8.0.1-full_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers"
EXPECTED_FFMPEG_SHA256 = "74DB6C184A03DBA2BDFE23E1A1F41CF5A8385BC1DE6A7A1B26DB1DC541ABEF93"
EXPECTED_FFPROBE_VERSION = "ffprobe version 8.0.1-full_build-www.gyan.dev Copyright (c) 2007-2025 the FFmpeg developers"
EXPECTED_FFPROBE_SHA256 = "55BB6C6289367AE2383EFA86B26BF2596F8ADB72AC747360EB13DF162354161C"
EXPECTED_P4_MANIFEST_SHA256 = "7149B13D84A305450704722475A4ED670DACD8D258F305051E54479C603C50DE"
EXPECTED_ACCEPTANCE_FACT_HASHES = {
    "p4SourceManifestSha256": EXPECTED_P4_MANIFEST_SHA256,
    "sourcesSha256": "0977D21C14458A44D684CC6CB6E87A835B71F782E6C82E4667EFC56073A61FCF",
    "requiredRoutesSha256": "DC3857CDFF9685CEE7126A1367FCF8B8645113760FE663EE4931F650C68234DA",
    "requiredFailureCasesSha256": "CBBE8C571E2CF285F794889D80EC8BDF0FC853CF25D8DF9F9BD1334616631214",
    "timingRulesSha256": "BCEC329B85123A1827649F7996F98866E426B7495D91AAA9CC2128A892A86719",
    "referenceMetricsSha256": "375333FA34433BF8427A04AD9A103A66AFA005C62204DDBAF398AAFF949F883F",
    "skipPolicySha256": "4ECFAC295177701067A9B5D65D71ADB3453406FE27FA99AF24695E7ED6822E69",
}
EXPECTED_PARENT_SHA256 = "317526B2B7B6A5774CC41FBB232FB2526901EE358AB4B470ADEEF7AC3617794A"
EXPECTED_PARENT_BYTES = 9_316_483
EXPECTED_HUAWEI_RANGE_OFFSET = 2_797_019
EXPECTED_HUAWEI_RANGE_BYTES = 6_519_404
EXPECTED_HUAWEI_VIDEO_SHA256 = "664EF6DBA25D7B04228C79B874A0EDA742B1211E17EE770D0E82E765BD454CE6"
ORPHAN_RELATIVE = Path(
    ".ai-tmp/workspace/P5-R5/iteration-5-bridge-1/"
    "lpb-video-sidecar-44492-72569734-3941563989-322549303.tmp"
)
EXPECTED_ROUTES = {
    "apple-copy-mov-to-mp4",
    "apple-copy-mov-same-container",
    "vivo-copy-mp4-to-mov",
    "vivo-copy-mp4-same-container",
    "apple-hevc-to-h264-sdr",
    "apple-hevc-to-h264-sdr-mp4",
    "vivo-h264-to-hevc-sdr",
    "huawei-main10-hlg-to-hevc-preservation",
    "huawei-main10-hlg-to-h264-preservation-required",
}
EXPECTED_FAILURES = {
    "target-fps-positive",
    "real-sidecar-missing",
    "ambiguous-or-malformed-preservation-facts",
    "cancellation",
    "output-validation-or-publish-failure",
}


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


def write_create_only(path: Path, data: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("xb") as stream:
        stream.write(data)


def json_bytes(value: Any) -> bytes:
    return (json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n").encode("utf-8")


def canonical_sha256(value: Any) -> str:
    encoded = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode("utf-8")
    return sha256_bytes(encoded)


def parse_json(data: bytes, description: str) -> Any:
    try:
        return json.loads(data.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as ex:
        raise ValidationError(f"{description} was not valid UTF-8 JSON: {ex}") from ex


def as_fraction(value: Any, description: str) -> Fraction:
    if value is None or value == "N/A":
        raise ValidationError(f"{description} is absent.")
    try:
        if isinstance(value, (int, float)):
            return Fraction(str(value))
        return Fraction(str(value))
    except (ValueError, ZeroDivisionError) as ex:
        raise ValidationError(f"{description} is not a valid rational number: {value!r}") from ex


def relative(path: Path, root: Path) -> str:
    return path.resolve().relative_to(root.resolve()).as_posix()


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValidationError(message)


@dataclass
class Runner:
    root: Path
    run_root: Path
    manifest: dict[str, Any]
    ffmpeg: Path
    ffprobe: Path
    dotnet: str
    driver_dll: Path
    command_records: list[dict[str, Any]]
    source_paths: dict[str, Path]
    source_hashes_before: dict[str, str]
    tool_version_records: dict[str, dict[str, Any]]
    command_index: int = 0

    def command(self, label: str, argv: list[str], timeout: int = 600) -> tuple[dict[str, Any], bytes, bytes]:
        self.command_index += 1
        safe = re.sub(r"[^A-Za-z0-9._-]+", "_", label)
        prefix = f"{self.command_index:03d}-{safe}"
        stdout_path = self.run_root / "raw" / f"{prefix}.stdout.bin"
        stderr_path = self.run_root / "raw" / f"{prefix}.stderr.bin"
        if stdout_path.exists() or stderr_path.exists():
            raise ValidationError(f"Refusing to overwrite raw command evidence for {label}.")
        started = time.perf_counter()
        try:
            completed = subprocess.run(
                argv,
                cwd=self.root,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                check=False,
                timeout=timeout,
            )
            stdout = completed.stdout
            stderr = completed.stderr
            exit_code: int | None = completed.returncode
            timed_out = False
        except subprocess.TimeoutExpired as ex:
            stdout = ex.stdout or b""
            stderr = ex.stderr or b""
            exit_code = None
            timed_out = True
        write_create_only(stdout_path, stdout)
        write_create_only(stderr_path, stderr)
        record = {
            "label": label,
            "argv": [str(part) for part in argv],
            "cwd": str(self.root),
            "elapsedSeconds": round(time.perf_counter() - started, 6),
            "exitCode": exit_code,
            "timedOut": timed_out,
            "stdout": {
                "path": relative(stdout_path, self.root),
                "bytes": len(stdout),
                "sha256": sha256_bytes(stdout),
            },
            "stderr": {
                "path": relative(stderr_path, self.root),
                "bytes": len(stderr),
                "sha256": sha256_bytes(stderr),
            },
        }
        self.command_records.append(record)
        if timed_out:
            raise ValidationError(f"Command timed out after {timeout}s: {label}.")
        return record, stdout, stderr

    def require_command_success(self, label: str, argv: list[str], timeout: int = 600) -> tuple[dict[str, Any], bytes, bytes]:
        record, stdout, stderr = self.command(label, argv, timeout)
        if record["exitCode"] != 0:
            raise ValidationError(
                f"{label} exited {record['exitCode']}; stderr: {stderr.decode('utf-8', 'replace')[-2000:]}"
            )
        return record, stdout, stderr

    def probe(self, path: Path, label: str) -> dict[str, Any]:
        argv = [
            str(self.ffprobe), "-v", "error", "-show_format", "-show_streams",
            "-show_packets", "-show_frames", "-show_data_hash", "sha256",
            "-of", "json", str(path),
        ]
        _, stdout, _ = self.require_command_success(label, argv, timeout=300)
        result = parse_json(stdout, label)
        require(isinstance(result, dict), f"{label} did not return an object.")
        require(isinstance(result.get("streams"), list), f"{label} omitted streams.")
        require(isinstance(result.get("format"), dict), f"{label} omitted format facts.")
        return result

    def route_driver(self, label: str, args: list[str], timeout: int = 900) -> dict[str, Any]:
        argv = [self.dotnet, str(self.driver_dll), *args]
        _, stdout, stderr = self.require_command_success(label, argv, timeout)
        value = parse_json(stdout, label)
        require(isinstance(value, dict), f"{label} output did not contain a JSON object: {stderr.decode('utf-8', 'replace')}")
        return value


def resolve_tool(explicit: str | None, env_key: str, exe_name: str) -> Path:
    candidates: list[str] = []
    if explicit:
        candidates.append(explicit)
    if os.environ.get(env_key):
        candidates.append(os.environ[env_key])
    found = shutil.which(exe_name)
    if found:
        candidates.append(found)
    found_exe = shutil.which(exe_name + ".exe")
    if found_exe:
        candidates.append(found_exe)
    local = os.environ.get("LOCALAPPDATA")
    if local:
        candidates.append(str(
            Path(local) / "Microsoft" / "WinGet" / "Packages" /
            "Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe" /
            "ffmpeg-8.0.1-full_build" / "bin" / (exe_name + ".exe")
        ))
    for candidate in candidates:
        path = Path(candidate).expanduser().resolve()
        if path.is_file():
            return path
    raise ValidationError(f"Could not resolve required pinned executable {exe_name}; checked explicit argument, {env_key}, PATH and the frozen WinGet location.")


def verify_tool_erratum(
    root: Path,
    manifest: dict[str, Any],
    profile_path: Path,
) -> tuple[dict[str, Any], Path, str]:
    erratum_path = root / EXPECTED_TOOL_ERRATUM_RELATIVE
    erratum_bytes = erratum_path.read_bytes()
    erratum_sha256 = sha256_bytes(erratum_bytes)
    require(
        erratum_sha256 == EXPECTED_TOOL_ERRATUM_SHA256,
        f"P5-R5 tool identity erratum hash mismatch: {erratum_sha256}.",
    )
    erratum = parse_json(erratum_bytes, "P5-R5 tool identity erratum E1")
    expected_erratum = {
        "schemaVersion": 1,
        "erratumId": "P5-R5-v1-tool-identity-E1",
        "profileId": "P5-R5-v1",
        "manifestPath": EXPECTED_PROFILE_MANIFEST_RELATIVE,
        "manifestSha256": EXPECTED_PROFILE_MANIFEST_SHA256,
        "targetField": "independentValidation.ffmpeg.version",
        "frozenValue": "ffmpeg version 8.0.1-full_build-www.gyan.dev Copyright (c) 2007-2025 the FFmpeg developers",
        "correctedValue": EXPECTED_FFMPEG_VERSION,
        "binary": {
            "semanticVersion": "8.0.1-full_build-www.gyan.dev",
            "sha256": EXPECTED_FFMPEG_SHA256,
        },
        "reason": "The frozen ffmpeg version metadata contains a copyright-year transcription error. The exact hash-locked executable reports the corrected version line; executable identity and semantic version remain unchanged.",
        "unchangedAcceptance": {
            **EXPECTED_ACCEPTANCE_FACT_HASHES,
            "numericImageQualityThreshold": "none-specified-by-parent-roadmap",
        },
        "changeBoundary": [
            "Only the copyright-year metadata in independentValidation.ffmpeg.version is corrected.",
            "P5-R5-v1 routes, required failure cases, source identities, timing rules, quality model, skip policy, thresholds, executable hashes, and backend policy are unchanged.",
            "This erratum does not authorize conversion-matrix execution or alter R5 acceptance status.",
        ],
    }
    require(erratum == expected_erratum, "P5-R5 tool identity erratum content or scope changed.")
    require(sha256_file(profile_path) == EXPECTED_PROFILE_MANIFEST_SHA256, "Erratum is not bound to the frozen P5-R5-v1 manifest.")
    require(erratum["manifestSha256"] == sha256_file(profile_path), "Erratum manifest identity does not match the frozen file.")
    require(manifest["independentValidation"]["ffmpeg"] == {
        "version": erratum["frozenValue"],
        "sha256": EXPECTED_FFMPEG_SHA256,
    }, "Erratum does not target only the frozen ffmpeg version metadata.")
    return erratum, erratum_path, erratum_sha256


def verify_manifest(root: Path) -> tuple[dict[str, Any], Path, dict[str, Any], Path, str]:
    profile_path = root / "tools/p5-r5-video-validation/profiles/P5-R5-v1/manifest.json"
    manifest_bytes = profile_path.read_bytes()
    manifest_sha256 = sha256_bytes(manifest_bytes)
    require(
        manifest_sha256 == EXPECTED_PROFILE_MANIFEST_SHA256,
        f"P5-R5-v1 frozen manifest SHA-256 mismatch: {manifest_sha256}.",
    )
    manifest = parse_json(manifest_bytes, "P5-R5-v1 manifest")
    require(manifest.get("profileId") == "P5-R5-v1", "Profile identifier changed.")
    require(manifest.get("state") == "frozen-before-conversion-matrix", "Profile is not marked frozen before the conversion matrix.")
    require(manifest.get("conversionOutputsObservedBeforeFreeze") is False, "Profile says conversion outputs were observed before freeze.")
    source_manifest = root / manifest["sourceManifest"]["path"]
    source_manifest_hash = sha256_file(source_manifest)
    require(
        source_manifest_hash == EXPECTED_P4_MANIFEST_SHA256 == manifest["sourceManifest"]["sha256"],
        "P4-v1 source manifest hash is not the frozen identity.",
    )
    routes = manifest.get("requiredRoutes")
    failures = manifest.get("requiredFailureCases")
    require(isinstance(routes, list) and len(routes) == 9, "Frozen manifest must contain exactly 9 route rows.")
    require(isinstance(failures, list) and len(failures) == 5, "Frozen manifest must contain exactly 5 failure rows.")
    route_ids = {item.get("id") for item in routes if item.get("required") is True}
    failure_ids = {item.get("id") for item in failures if item.get("required") is True}
    require(route_ids == EXPECTED_ROUTES, f"Frozen required route set mismatch: {sorted(route_ids)}.")
    require(failure_ids == EXPECTED_FAILURES, f"Frozen required failure set mismatch: {sorted(failure_ids)}.")
    require(all(item.get("skipAllowed") is False for item in routes), "At least one required route permits skipping.")
    require(all(item.get("required") is True for item in routes + failures), "A matrix row is not mandatory.")
    independent = manifest.get("independentValidation")
    require(isinstance(independent, dict), "Frozen independent validation tool identities are absent.")
    require(independent.get("ffmpeg") == {
        "version": "ffmpeg version 8.0.1-full_build-www.gyan.dev Copyright (c) 2007-2025 the FFmpeg developers",
        "sha256": EXPECTED_FFMPEG_SHA256,
    }, "Frozen ffmpeg identity changed outside the versioned erratum scope.")
    require(independent.get("ffprobe") == {
        "version": EXPECTED_FFPROBE_VERSION,
        "sha256": EXPECTED_FFPROBE_SHA256,
    }, "Frozen ffprobe identity changed.")
    actual_acceptance_hashes = {
        "p4SourceManifestSha256": source_manifest_hash,
        "sourcesSha256": canonical_sha256(manifest["sources"]),
        "requiredRoutesSha256": canonical_sha256(routes),
        "requiredFailureCasesSha256": canonical_sha256(failures),
        "timingRulesSha256": canonical_sha256(independent["timingRules"]),
        "referenceMetricsSha256": canonical_sha256(independent["referenceMetrics"]),
        "skipPolicySha256": canonical_sha256(independent["skipPolicy"]),
    }
    require(
        actual_acceptance_hashes == EXPECTED_ACCEPTANCE_FACT_HASHES,
        "Frozen R5 acceptance facts differ from the versioned tool erratum's unchanged scope.",
    )
    erratum, erratum_path, erratum_sha256 = verify_tool_erratum(root, manifest, profile_path)
    return manifest, profile_path, erratum, erratum_path, erratum_sha256


def validate_tool(
    runner: Runner,
    name: str,
    path: Path,
    frozen: dict[str, str],
    expected_version: str | None = None,
) -> dict[str, Any]:
    actual_hash = sha256_file(path)
    require(actual_hash == frozen["sha256"], f"{name} SHA-256 mismatch: {actual_hash}.")
    record, stdout, stderr = runner.require_command_success(f"{name}-version", [str(path), "-version"], timeout=30)
    version = stdout.decode("utf-8", "replace").splitlines()[0].strip()
    expected = expected_version if expected_version is not None else frozen["version"]
    require(version == expected, f"{name} version mismatch: {version!r}.")
    return {
        "path": str(path),
        "sha256": actual_hash,
        "versionLine": version,
        "versionCommand": record,
        "stderrBytes": len(stderr),
    }


def validate_sources(root: Path, manifest: dict[str, Any]) -> tuple[dict[str, Path], dict[str, str]]:
    sources: dict[str, Path] = {}
    identities: dict[str, str] = {}
    for item in manifest["sources"]:
        path = root / item["cacheRelativePath"]
        require(path.is_file(), f"Required cached source is missing: {path}.")
        require(path.stat().st_size == item["byteSize"], f"Source byte size changed: {item['id']}.")
        actual = sha256_file(path)
        require(actual == item["sha256"], f"Source hash changed: {item['id']} ({actual}).")
        require(item.get("originalIsReadOnly") is True, f"Read-only original constraint missing for {item['id']}.")
        sources[item["id"]] = path.resolve()
        identities[item["id"]] = actual
    return sources, identities


def tool_fields(probe: dict[str, Any]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for stream in probe["streams"]:
        side_data = stream.get("side_data_list") or []
        rotation = next((item.get("rotation") for item in side_data if item.get("rotation") is not None), None)
        if rotation is None:
            rotation = (stream.get("tags") or {}).get("rotate")
        result.append({
            "index": stream.get("index"),
            "codecType": stream.get("codec_type"),
            "codecName": stream.get("codec_name"),
            "codecTag": stream.get("codec_tag_string"),
            "profile": stream.get("profile"),
            "pixelFormat": stream.get("pix_fmt"),
            "width": stream.get("width"),
            "height": stream.get("height"),
            "bitsPerRawSample": stream.get("bits_per_raw_sample"),
            "bitsPerCodedSample": stream.get("bits_per_coded_sample"),
            "sampleRate": stream.get("sample_rate"),
            "channels": stream.get("channels"),
            "channelLayout": stream.get("channel_layout"),
            "timeBase": stream.get("time_base"),
            "startPts": stream.get("start_pts"),
            "durationTs": stream.get("duration_ts"),
            "duration": stream.get("duration"),
            "avgFrameRate": stream.get("avg_frame_rate"),
            "rFrameRate": stream.get("r_frame_rate"),
            "colorRange": stream.get("color_range"),
            "colorSpace": stream.get("color_space"),
            "colorTransfer": stream.get("color_transfer"),
            "colorPrimaries": stream.get("color_primaries"),
            "rotationDegrees": rotation,
        })
    return result


def one_stream(probe: dict[str, Any], codec_type: str) -> dict[str, Any]:
    selected = [stream for stream in probe["streams"] if stream.get("codec_type") == codec_type]
    require(len(selected) == 1, f"Expected exactly one {codec_type} stream; found {len(selected)}.")
    return selected[0]


def stream_ordinal(probe: dict[str, Any], stream: dict[str, Any]) -> int:
    ordinal = 0
    for candidate in probe["streams"]:
        if candidate.get("codec_type") != stream.get("codec_type"):
            continue
        if candidate is stream:
            return ordinal
        ordinal += 1
    raise ValidationError("Could not map FFprobe stream to its media-type ordinal.")


def stream_by_ordinal(probe: dict[str, Any], codec_type: str, ordinal: int) -> dict[str, Any]:
    selected = [stream for stream in probe["streams"] if stream.get("codec_type") == codec_type]
    require(ordinal < len(selected), f"Output is missing {codec_type} stream ordinal {ordinal}.")
    return selected[ordinal]


def stream_time_base(stream: dict[str, Any]) -> Fraction:
    return as_fraction(stream.get("time_base"), "stream time_base")


def probe_items(probe: dict[str, Any], kind: str) -> list[dict[str, Any]]:
    require(kind in {"packet", "frame"}, f"Unsupported FFprobe combined item type {kind!r}.")
    require("packets" not in probe and "frames" not in probe, "FFprobe returned unexpected legacy packet/frame arrays.")
    items = probe.get("packets_and_frames")
    require(isinstance(items, list), "FFprobe omitted its packets_and_frames array.")
    selected: list[dict[str, Any]] = []
    for ordinal, item in enumerate(items):
        require(isinstance(item, dict), f"FFprobe combined item {ordinal} is not an object.")
        item_type = item.get("type")
        require(item_type in {"packet", "frame"}, f"FFprobe combined item {ordinal} has unknown type {item_type!r}.")
        if item_type == kind:
            selected.append(item)
    return selected


def stream_packets(probe: dict[str, Any], stream: dict[str, Any]) -> list[dict[str, Any]]:
    stream_index = stream.get("index")
    packets = [packet for packet in probe_items(probe, "packet") if packet.get("stream_index") == stream_index]
    return packets


def packet_hash(packet: dict[str, Any]) -> str:
    value = packet.get("data_hash")
    require(isinstance(value, str) and value.startswith("SHA256:"), "FFprobe packet omitted its SHA-256 data hash.")
    return value[7:].upper()


def compare_copy_packets(source_probe: dict[str, Any], output_probe: dict[str, Any]) -> dict[str, Any]:
    comparisons: list[dict[str, Any]] = []
    types = sorted({stream.get("codec_type") for stream in source_probe["streams"] if stream.get("codec_type") in {"video", "audio"}})
    for codec_type in types:
        source_streams = [s for s in source_probe["streams"] if s.get("codec_type") == codec_type]
        output_streams = [s for s in output_probe["streams"] if s.get("codec_type") == codec_type]
        require(len(source_streams) == len(output_streams), f"{codec_type} stream count changed during Copy.")
        for ordinal, (source_stream, output_stream) in enumerate(zip(source_streams, output_streams, strict=True)):
            source_packets = stream_packets(source_probe, source_stream)
            output_packets = stream_packets(output_probe, output_stream)
            require(len(source_packets) == len(output_packets) and bool(source_packets), f"{codec_type} packet count changed or was empty.")
            source_tb = stream_time_base(source_stream)
            output_tb = stream_time_base(output_stream)
            tolerance = max(source_tb, output_tb)
            source_first_pts = as_fraction(source_packets[0].get("pts"), "source first packet PTS")
            output_first_pts = as_fraction(output_packets[0].get("pts"), "output first packet PTS")
            source_first_dts = as_fraction(source_packets[0].get("dts"), "source first packet DTS")
            output_first_dts = as_fraction(output_packets[0].get("dts"), "output first packet DTS")
            maximum_pts_delta = Fraction(0)
            maximum_dts_delta = Fraction(0)
            maximum_duration_delta = Fraction(0)
            for ordinal_packet, (source_packet, output_packet) in enumerate(zip(source_packets, output_packets, strict=True)):
                require(packet_hash(source_packet) == packet_hash(output_packet), f"{codec_type} packet payload hash/order changed at packet {ordinal_packet}.")
                pts_delta = abs(
                    (as_fraction(source_packet.get("pts"), "source packet PTS") - source_first_pts) * source_tb
                    - (as_fraction(output_packet.get("pts"), "output packet PTS") - output_first_pts) * output_tb
                )
                dts_delta = abs(
                    (as_fraction(source_packet.get("dts"), "source packet DTS") - source_first_dts) * source_tb
                    - (as_fraction(output_packet.get("dts"), "output packet DTS") - output_first_dts) * output_tb
                )
                source_duration = as_fraction(source_packet.get("duration"), "source packet duration") * source_tb
                output_duration = as_fraction(output_packet.get("duration"), "output packet duration") * output_tb
                duration_delta = abs(source_duration - output_duration)
                require(pts_delta <= tolerance, f"{codec_type} packet {ordinal_packet} normalized PTS exceeds the frozen coarse-tick tolerance.")
                require(dts_delta <= tolerance, f"{codec_type} packet {ordinal_packet} normalized DTS exceeds the frozen coarse-tick tolerance.")
                require(duration_delta <= tolerance, f"{codec_type} packet {ordinal_packet} duration exceeds the frozen coarse-tick tolerance.")
                maximum_pts_delta = max(maximum_pts_delta, pts_delta)
                maximum_dts_delta = max(maximum_dts_delta, dts_delta)
                maximum_duration_delta = max(maximum_duration_delta, duration_delta)
            comparisons.append({
                "codecType": codec_type,
                "ordinal": ordinal,
                "sourcePacketCount": len(source_packets),
                "outputPacketCount": len(output_packets),
                "payloadSha256OrderExact": True,
                "sourceTimeBase": str(source_tb),
                "outputTimeBase": str(output_tb),
                "toleranceSeconds": str(tolerance),
                "maxNormalizedPtsDeltaSeconds": str(maximum_pts_delta),
                "maxNormalizedDtsDeltaSeconds": str(maximum_dts_delta),
                "maxPacketDurationDeltaSeconds": str(maximum_duration_delta),
            })
    require(types == ["audio", "video"] or set(types) == {"audio", "video"}, "Copy route must include exactly video and audio packet classes.")
    return {"comparisons": comparisons, "exact": True}


def mebx_streams(probe: dict[str, Any]) -> list[dict[str, Any]]:
    return [
        stream for stream in probe["streams"]
        if stream.get("codec_type") == "data" and stream.get("codec_tag_string") == "mebx"
    ]


def compare_mebx(source_probe: dict[str, Any], output_probe: dict[str, Any]) -> dict[str, Any]:
    source_streams = mebx_streams(source_probe)
    output_streams = mebx_streams(output_probe)
    require(source_streams, "Canonical Huawei extracted source has no mebx stream.")
    require(len(source_streams) == len(output_streams), "Huawei conversion changed the mebx stream count.")
    comparisons: list[dict[str, Any]] = []
    for ordinal, (src, out) in enumerate(zip(source_streams, output_streams, strict=True)):
        source_packets = stream_packets(source_probe, src)
        output_packets = stream_packets(output_probe, out)
        require(len(source_packets) == len(output_packets) and bool(source_packets), "Huawei output mebx packets are missing or re-counted.")
        require(
            [packet_hash(packet) for packet in source_packets] == [packet_hash(packet) for packet in output_packets],
            "Huawei output mebx packet payloads/order differ from the canonical extracted source.",
        )
        src_tb = stream_time_base(src)
        out_tb = stream_time_base(out)
        tolerance = max(src_tb, out_tb)
        src_first = as_fraction(source_packets[0].get("pts"), "source mebx first PTS")
        out_first = as_fraction(output_packets[0].get("pts"), "output mebx first PTS")
        max_delta = Fraction(0)
        for src_packet, out_packet in zip(source_packets, output_packets, strict=True):
            delta = abs(
                (as_fraction(src_packet.get("pts"), "source mebx PTS") - src_first) * src_tb
                - (as_fraction(out_packet.get("pts"), "output mebx PTS") - out_first) * out_tb
            )
            require(delta <= tolerance, "Huawei mebx packet timing exceeds the frozen coarse-tick tolerance.")
            max_delta = max(max_delta, delta)
        comparisons.append({
            "ordinal": ordinal,
            "sourcePacketCount": len(source_packets),
            "outputPacketCount": len(output_packets),
            "payloadSha256OrderExact": True,
            "maxNormalizedPtsDeltaSeconds": str(max_delta),
            "toleranceSeconds": str(tolerance),
        })
    return {"streamCount": len(source_streams), "comparisons": comparisons, "exact": True}


def rotation_degrees(stream: dict[str, Any]) -> int | None:
    for item in stream.get("side_data_list") or []:
        value = item.get("rotation")
        if value is not None:
            return int(round(float(value)))
    value = (stream.get("tags") or {}).get("rotate")
    return int(round(float(value))) if value is not None else None


def format_major_brand(probe: dict[str, Any]) -> str:
    tags = probe["format"].get("tags") or {}
    brand = tags.get("major_brand")
    require(isinstance(brand, str), "FFprobe did not report the ISO-BMFF major brand.")
    return brand


def check_container(probe: dict[str, Any], target: str) -> dict[str, Any]:
    brand = format_major_brand(probe)
    if target == "mov":
        require(brand.strip().lower() == "qt", f"Expected QuickTime MOV major brand; found {brand!r}.")
    else:
        require(brand.strip().lower() != "qt", f"Expected MP4 major brand; found QuickTime {brand!r}.")
    format_name = probe["format"].get("format_name", "")
    require("mov" in format_name or "mp4" in format_name, f"FFprobe found non-ISO-BMFF format {format_name!r}.")
    return {"target": target, "majorBrand": brand, "formatName": format_name, "passed": True}


def frame_facts(probe: dict[str, Any]) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    video = one_stream(probe, "video")
    frames = [item for item in probe_items(probe, "frame") if item.get("media_type") == "video"]
    require(bool(frames), "FFprobe did not enumerate decoded video frames.")
    require(video.get("time_base"), "Video stream has no rational time base.")
    return video, frames


def compare_reencoded_frames(source_probe: dict[str, Any], output_probe: dict[str, Any]) -> dict[str, Any]:
    source_video, source_frames = frame_facts(source_probe)
    output_video, output_frames = frame_facts(output_probe)
    require(len(source_frames) == len(output_frames), "Decoded video frame counts differ.")
    source_tb = stream_time_base(source_video)
    output_tb = stream_time_base(output_video)
    source_pts = [as_fraction(frame.get("best_effort_timestamp"), "source frame best-effort timestamp") * source_tb for frame in source_frames]
    output_pts = [as_fraction(frame.get("best_effort_timestamp"), "output frame best-effort timestamp") * output_tb for frame in output_frames]
    source_start = source_pts[0]
    output_start = output_pts[0]
    normalized_source = [value - source_start for value in source_pts]
    normalized_output = [value - output_start for value in output_pts]
    max_delta = Fraction(0)
    max_tolerance = Fraction(0)
    for index, (source_value, output_value) in enumerate(zip(normalized_source, normalized_output, strict=True)):
        if index + 1 < len(normalized_source):
            interval = normalized_source[index + 1] - source_value
        elif index:
            interval = source_value - normalized_source[index - 1]
        else:
            interval = max(source_tb, output_tb)
        require(interval > 0, f"Source frame timing is not increasing at frame {index}.")
        delta = abs(source_value - output_value)
        require(delta <= interval, f"Frame {index} presentation timestamp exceeds one source frame interval.")
        max_delta = max(max_delta, delta)
        max_tolerance = max(max_tolerance, interval)
    require(
        all(right >= left for left, right in zip(normalized_source, normalized_source[1:])),
        "Source presentation order is not monotonic.",
    )
    require(
        all(right >= left for left, right in zip(normalized_output, normalized_output[1:])),
        "Output presentation order is not monotonic.",
    )
    return {
        "sourceDecodedFrameCount": len(source_frames),
        "outputDecodedFrameCount": len(output_frames),
        "frameOrderCountEqual": True,
        "sourceTimeBase": str(source_tb),
        "outputTimeBase": str(output_tb),
        "maxNormalizedTimestampDeltaSeconds": str(max_delta),
        "largestApplicableSourceFrameIntervalSeconds": str(max_tolerance),
        "timestampRulePassed": True,
    }


def decode_all(runner: Runner, path: Path, label: str) -> dict[str, Any]:
    argv = [
        str(runner.ffmpeg), "-hide_banner", "-nostats", "-v", "error", "-xerror",
        "-noautorotate", "-i", str(path), "-map", "0:v:0", "-f", "null", "NUL",
    ]
    record, _, stderr = runner.require_command_success(label, argv, timeout=600)
    return {"command": record, "fullDecode": True, "stderrTail": stderr.decode("utf-8", "replace")[-1000:]}


def probe_duration(stream: dict[str, Any], packets: list[dict[str, Any]]) -> Fraction:
    tb = stream_time_base(stream)
    if packets:
        starts = [as_fraction(packet.get("pts"), "audio packet PTS") * tb for packet in packets]
        ends = [
            as_fraction(packet.get("pts"), "audio packet PTS") * tb
            + as_fraction(packet.get("duration"), "audio packet duration") * tb
            for packet in packets
        ]
        return max(ends) - min(starts)
    if stream.get("duration_ts") is not None:
        return as_fraction(stream["duration_ts"], "audio duration_ts") * tb
    return as_fraction(stream.get("duration"), "audio stream duration")


def compare_audio(source_probe: dict[str, Any], output_probe: dict[str, Any]) -> dict[str, Any]:
    source_audio = [stream for stream in source_probe["streams"] if stream.get("codec_type") == "audio"]
    output_audio = [stream for stream in output_probe["streams"] if stream.get("codec_type") == "audio"]
    require(len(source_audio) == len(output_audio), "Audio stream count changed.")
    result: list[dict[str, Any]] = []
    for ordinal, (src, out) in enumerate(zip(source_audio, output_audio, strict=True)):
        src_packets = stream_packets(source_probe, src)
        out_packets = stream_packets(output_probe, out)
        src_duration = probe_duration(src, src_packets)
        out_duration = probe_duration(out, out_packets)
        packet_durations = [
            as_fraction(packet.get("duration"), "source audio packet duration") * stream_time_base(src)
            for packet in src_packets
        ]
        tolerance = max(packet_durations) if packet_durations else stream_time_base(src)
        delta = abs(src_duration - out_duration)
        require(delta <= tolerance, f"Audio duration differs by more than one source audio packet for stream {ordinal}.")
        result.append({
            "ordinal": ordinal,
            "sourceCodec": src.get("codec_name"),
            "outputCodec": out.get("codec_name"),
            "sourceProfile": src.get("profile"),
            "outputProfile": out.get("profile"),
            "sourceSampleRate": src.get("sample_rate"),
            "outputSampleRate": out.get("sample_rate"),
            "sourceChannels": src.get("channels"),
            "outputChannels": out.get("channels"),
            "sourceChannelLayout": src.get("channel_layout"),
            "outputChannelLayout": out.get("channel_layout"),
            "sourceDurationSeconds": str(src_duration),
            "outputDurationSeconds": str(out_duration),
            "absoluteDurationDeltaSeconds": str(delta),
            "oneSourcePacketToleranceSeconds": str(tolerance),
        })
    return {"streams": result, "passed": True}


def check_remux_route(
    route_id: str,
    target: str,
    source_probe: dict[str, Any],
    output_probe: dict[str, Any],
    source_path: Path,
    output_path: Path,
    driver_report: dict[str, Any],
) -> dict[str, Any]:
    record = driver_report["executionRecord"]
    truth = record["truth"]
    require(driver_report["success"] is True, f"{route_id} production conversion failed: {driver_report.get('error')}.")
    require(record.get("backend") == "ProjectIsoBmffRemux", f"{route_id} selected a non-remux owner.")
    require(truth.get("selectedCapability") == "VideoRemux", f"{route_id} selected capability is not VideoRemux.")
    require(truth.get("fallbackOccurred") is False, f"{route_id} reports a fallback.")
    expected_codec = one_stream(source_probe, "video").get("codec_name")
    output_video = one_stream(output_probe, "video")
    require(output_video.get("codec_name") == expected_codec, f"{route_id} changed the video codec.")
    container_facts = check_container(output_probe, target)
    source_inventory = [s for s in source_probe["streams"]]
    output_inventory = [s for s in output_probe["streams"]]
    source_inventory_key = [(s.get("codec_type"), s.get("codec_name"), s.get("codec_tag_string")) for s in source_inventory]
    output_inventory_key = [(s.get("codec_type"), s.get("codec_name"), s.get("codec_tag_string")) for s in output_inventory]
    require(source_inventory_key == output_inventory_key, f"{route_id} changed the required stream inventory, including data tracks.")
    for codec_type in ("video", "audio"):
        source_typed = [s for s in source_inventory if s.get("codec_type") == codec_type]
        output_typed = [s for s in output_inventory if s.get("codec_type") == codec_type]
        common_fields = (
            ("codec_name", "profile", "codec_tag_string", "time_base")
            if codec_type == "video"
            else ("codec_name", "profile", "codec_tag_string", "sample_rate", "channels", "channel_layout", "time_base")
        )
        for ordinal, (source_stream, output_stream) in enumerate(zip(source_typed, output_typed, strict=True)):
            require(
                all(source_stream.get(field) == output_stream.get(field) for field in common_fields),
                f"{route_id} changed Copy {codec_type} stream facts at ordinal {ordinal}.",
            )
    packet_comparison = compare_copy_packets(source_probe, output_probe)
    source_sha = sha256_file(source_path)
    output_sha = sha256_file(output_path)
    same_container = route_id.endswith("same-container")
    byte_identical = source_sha == output_sha
    actual_operation = truth.get("actualOperationKind")
    required_operation = "Passthrough" if same_container and byte_identical else "ContainerRemux"
    require(actual_operation == required_operation, f"{route_id} operation truth is {actual_operation}; expected {required_operation}.")
    if same_container:
        require(byte_identical or actual_operation == "ContainerRemux", "Same-container Copy is not truthful about passthrough/remux.")
    source_rotation = rotation_degrees(one_stream(source_probe, "video"))
    output_rotation = rotation_degrees(output_video)
    require(source_rotation == output_rotation, f"{route_id} changed rotation/display matrix.")
    return {
        "routeId": route_id,
        "checksPassed": True,
        "actualOperation": actual_operation,
        "byteIdentity": {"sourceSha256": source_sha, "outputSha256": output_sha, "identical": byte_identical},
        "streamInventory": {
            "source": tool_fields(source_probe),
            "output": tool_fields(output_probe),
            "exactTypeCodecTagOrder": True,
        },
        "container": container_facts,
        "packetCopy": packet_comparison,
        "audio": compare_audio(source_probe, output_probe),
        "rotation": {
            "sourceDegrees": source_rotation,
            "outputDegrees": output_rotation,
            "equal": True,
        },
    }


def sdr_profile(source_video: dict[str, Any], output_video: dict[str, Any]) -> dict[str, Any]:
    pix_fmt = output_video.get("pix_fmt") or ""
    profile = (output_video.get("profile") or "").lower()
    transfer = output_video.get("color_transfer")
    primaries = output_video.get("color_primaries")
    matrix = output_video.get("color_space")
    require(output_video.get("codec_name") in {"h264", "hevc"}, "MF output codec does not match an SDR video codec.")
    require("10" not in pix_fmt and "12" not in pix_fmt and "16" not in pix_fmt, "SDR MF output is not ordinary 8-bit.")
    require(pix_fmt in {"yuv420p", "yuvj420p"}, f"Unexpected SDR MF pixel format {pix_fmt!r}.")
    require(profile not in {"", "unknown"}, "SDR MF output profile is unknown.")
    require(transfer in {"bt709", "smpte170m", "bt470bg", "gamma22", "gamma28", "smpte240m"}, f"Output transfer is not a known SDR CICP value: {transfer!r}.")
    require(primaries is not None and matrix is not None, "SDR MF output is missing CICP primaries or matrix.")
    require(output_video.get("color_range") == source_video.get("color_range"), "SDR MF output changed the source CICP range.")
    require(primaries == source_video.get("color_primaries"), "SDR MF output changed the source CICP primaries.")
    require(transfer == source_video.get("color_transfer"), "SDR MF output changed the source CICP transfer.")
    require(matrix == source_video.get("color_space"), "SDR MF output changed the source CICP matrix.")
    return {
        "codec": output_video.get("codec_name"),
        "profile": output_video.get("profile"),
        "pixelFormat": pix_fmt,
        "colorRange": output_video.get("color_range"),
        "colorPrimaries": primaries,
        "colorTransfer": transfer,
        "colorMatrix": matrix,
        "sourceColorFacts": {
            "range": source_video.get("color_range"),
            "primaries": source_video.get("color_primaries"),
            "transfer": source_video.get("color_transfer"),
            "matrix": source_video.get("color_space"),
        },
        "ordinarySdr": True,
    }


def compare_quality(runner: Runner, source: Path, output: Path, output_video: dict[str, Any], label: str) -> dict[str, Any]:
    width = output_video.get("width")
    height = output_video.get("height")
    require(isinstance(width, int) and isinstance(height, int) and width > 0 and height > 0, "Output stored dimensions are absent.")
    graph = (
        f"[0:v:0]scale={width}:{height},setsar=1,format=yuv420p,setpts=PTS-STARTPTS,split=2[pr][sr];"
        f"[1:v:0]setsar=1,format=yuv420p,setpts=PTS-STARTPTS,split=2[po][so];"
        "[pr][po]psnr=shortest=1[psnr];[sr][so]ssim=shortest=1[ssim]"
    )
    argv = [
        str(runner.ffmpeg), "-hide_banner", "-nostats", "-v", "info", "-xerror",
        "-noautorotate", "-i", str(source), "-noautorotate", "-i", str(output),
        "-filter_complex", graph, "-map", "[psnr]", "-map", "[ssim]", "-f", "null", "NUL",
    ]
    record, _, stderr = runner.require_command_success(label, argv, timeout=600)
    diagnostic = stderr.decode("utf-8", "replace")
    psnr_matches = re.findall(r"PSNR[^\r\n]*?average:([^\s]+)", diagnostic, flags=re.IGNORECASE)
    ssim_matches = re.findall(r"SSIM[^\r\n]*?All:([^\s]+)", diagnostic, flags=re.IGNORECASE)
    require(psnr_matches, "FFmpeg did not report the frozen PSNR calculation.")
    require(ssim_matches, "FFmpeg did not report the frozen SSIM calculation.")
    return {
        "command": record,
        "formula": runner.manifest["independentValidation"]["referenceMetrics"]["formula"],
        "psnrAverage": psnr_matches[-1],
        "ssimAll": ssim_matches[-1],
        "numericAcceptanceThresholdApplied": False,
    }


def check_mf_route(
    runner: Runner,
    route_id: str,
    target: str,
    target_codec: str,
    source_path: Path,
    output_path: Path,
    source_probe: dict[str, Any],
    output_probe: dict[str, Any],
    driver_report: dict[str, Any],
) -> dict[str, Any]:
    record = driver_report["executionRecord"]
    truth = record["truth"]
    require(driver_report["success"] is True, f"{route_id} conversion failed: {driver_report.get('error')}.")
    require(record.get("backend") == "WindowsMediaFoundation", f"{route_id} did not use Media Foundation.")
    require(record.get("hardwareMode") == "SoftwareForced", f"{route_id} was not software-forced.")
    require(record.get("hardwareFallbackOccurred") is False and truth.get("fallbackOccurred") is False, f"{route_id} reports a fallback.")
    require(truth.get("selectedCapability") == "VideoTranscodeSdr", f"{route_id} did not select VideoTranscodeSdr.")
    require(truth.get("backendName") == "WindowsMediaFoundation", f"{route_id} canonical backend name is incorrect.")
    require(truth.get("actualOperationKind") == "LossyReencode", f"{route_id} actual operation is not LossyReencode.")
    require(not any(stream.get("codec_type") not in {"video", "audio", "data"} for stream in output_probe["streams"]), f"{route_id} output contains an unexpected stream type.")
    output_video = one_stream(output_probe, "video")
    source_video = one_stream(source_probe, "video")
    expected_codec = target_codec
    require(output_video.get("codec_name") == expected_codec, f"{route_id} output codec differs from its frozen route.")
    container_facts = check_container(output_probe, target)
    profile = sdr_profile(source_video, output_video)
    frame_timing = compare_reencoded_frames(source_probe, output_probe)
    audio = compare_audio(source_probe, output_probe)
    source_rotation = rotation_degrees(source_video)
    output_rotation = rotation_degrees(output_video)
    require(source_rotation == output_rotation, f"{route_id} changed rotation/display matrix.")
    decoded_source = decode_all(runner, source_path, f"{route_id}-decode-source")
    decoded_output = decode_all(runner, output_path, f"{route_id}-decode-output")
    quality = compare_quality(runner, source_path, output_path, output_video, f"{route_id}-psnr-ssim")
    return {
        "routeId": route_id,
        "checksPassed": True,
        "container": container_facts,
        "profile": profile,
        "rotation": {"sourceDegrees": source_rotation, "outputDegrees": output_rotation, "equal": True},
        "frameTiming": frame_timing,
        "audio": audio,
        "independentDecode": {"source": decoded_source, "output": decoded_output},
        "referenceMetrics": quality,
        "sourceSha256": sha256_file(source_path),
        "outputSha256": sha256_file(output_path),
        "streamInventory": {"source": tool_fields(source_probe), "output": tool_fields(output_probe)},
    }


def huawei_hdr_profile(source_video: dict[str, Any], output_video: dict[str, Any]) -> dict[str, Any]:
    profile = (output_video.get("profile") or "").lower()
    pix_fmt = output_video.get("pix_fmt") or ""
    require(output_video.get("codec_name") == "hevc", "Huawei preservation output is not HEVC.")
    require("main 10" in profile or "main10" in profile, f"Huawei output is not Main10-compatible: {profile!r}.")
    require("10" in pix_fmt or "12" in pix_fmt or "16" in pix_fmt, f"Huawei output pixel format is not >8-bit: {pix_fmt!r}.")
    require(output_video.get("color_primaries") == "bt2020", "Huawei output primaries are not BT.2020.")
    require(output_video.get("color_transfer") == "arib-std-b67", "Huawei output transfer is not HLG.")
    require(output_video.get("color_space") == "bt2020nc", "Huawei output matrix is not BT.2020 non-constant luminance.")
    require(output_video.get("color_range") == source_video.get("color_range") == "pc", "Huawei output range is not the frozen full-range value.")
    return {
        "codec": output_video.get("codec_name"),
        "profile": output_video.get("profile"),
        "pixelFormat": pix_fmt,
        "colorPrimaries": output_video.get("color_primaries"),
        "colorTransfer": output_video.get("color_transfer"),
        "colorMatrix": output_video.get("color_space"),
        "colorRange": output_video.get("color_range"),
        "bitDepthGreaterThanEight": True,
        "hdrCicpPreserved": True,
    }


def check_huawei_route(
    runner: Runner,
    route_id: str,
    source_path: Path,
    output_path: Path,
    source_probe: dict[str, Any],
    output_probe: dict[str, Any],
    driver_report: dict[str, Any],
) -> dict[str, Any]:
    record = driver_report["executionRecord"]
    truth = record["truth"]
    require(driver_report["success"] is True, f"Huawei HEVC preservation failed: {driver_report.get('error')}.")
    require(truth.get("backendName") == "minimal-libav", "Huawei preservation route did not record minimal-libav.")
    require(truth.get("backendVersion") == "9.0.1", "Huawei preservation route reported the wrong backend version.")
    require(truth.get("selectedCapability") == "VideoTranscodeHdr10Bit", "Huawei preservation route selected the wrong capability.")
    require(truth.get("actualOperationKind") == "LossyReencode", "Huawei HEVC preservation route is not reported as LossyReencode.")
    require(record.get("hardwareMode") == "SoftwareForced", "Huawei preservation route hardware mode is not software-forced.")
    require(truth.get("fallbackOccurred") is False, "Huawei preservation route reports a fallback.")
    source_video = one_stream(source_probe, "video")
    output_video = one_stream(output_probe, "video")
    profile = huawei_hdr_profile(source_video, output_video)
    frame_timing = compare_reencoded_frames(source_probe, output_probe)
    require(frame_timing["sourceDecodedFrameCount"] == 86, "Huawei source decode count differs from the frozen 86-frame profile.")
    audio = compare_audio(source_probe, output_probe)
    source_audio = one_stream(source_probe, "audio")
    output_audio = one_stream(output_probe, "audio")
    for field in ("codec_name", "profile", "sample_rate", "channels", "channel_layout"):
        require(source_audio.get(field) == output_audio.get(field), f"Huawei preservation route changed audio field {field}.")
    require(not any(stream.get("codec_type") not in {"video", "audio", "data"} for stream in output_probe["streams"]), "Huawei output contains an unexpected stream type.")
    source_rotation = rotation_degrees(source_video)
    output_rotation = rotation_degrees(output_video)
    require(source_rotation == output_rotation == -90, "Huawei route changed the frozen -90 degree rotation.")
    decoded_source = decode_all(runner, source_path, f"{route_id}-decode-source")
    decoded_output = decode_all(runner, output_path, f"{route_id}-decode-output")
    mebx = compare_mebx(source_probe, output_probe)
    return {
        "routeId": route_id,
        "checksPassed": True,
        "profile": profile,
        "rotation": {"sourceDegrees": source_rotation, "outputDegrees": output_rotation, "equal": True},
        "frameTiming": frame_timing,
        "audio": audio,
        "mebx": mebx,
        "independentDecode": {"source": decoded_source, "output": decoded_output},
        "sourceSha256": sha256_file(source_path),
        "outputSha256": sha256_file(output_path),
        "streamInventory": {"source": tool_fields(source_probe), "output": tool_fields(output_probe)},
        "hdrReferenceMetrics": "not-used-by-frozen-policy",
    }


def box_children(data: bytes | bytearray, start: int, end: int, description: str) -> list[dict[str, Any]]:
    boxes: list[dict[str, Any]] = []
    position = start
    while position < end:
        require(end - position >= 8, f"{description} has a truncated box header at {position}.")
        size32 = int.from_bytes(data[position:position + 4], "big")
        kind = bytes(data[position + 4:position + 8]).decode("latin-1")
        header = 8
        if size32 == 1:
            require(end - position >= 16, f"{description} has a truncated extended box header.")
            size = int.from_bytes(data[position + 8:position + 16], "big")
            header = 16
        elif size32 == 0:
            size = end - position
        else:
            size = size32
        require(size >= header and size <= end - position, f"{description} has an invalid {kind} box size.")
        boxes.append({"type": kind, "start": position, "payload": position + header, "end": position + size, "size": size})
        position += size
    require(position == end, f"{description} box children do not end at the declared boundary.")
    return boxes


def exactly_one(boxes: Iterable[dict[str, Any]], kind: str, description: str) -> dict[str, Any]:
    matches = [box for box in boxes if box["type"] == kind]
    require(len(matches) == 1, f"{description} must contain exactly one {kind}; found {len(matches)}.")
    return matches[0]


def mutate_huawei_cicp_conflict(source: Path, target: Path) -> dict[str, Any]:
    data = bytearray(source.read_bytes())
    top = box_children(data, 0, len(data), "top-level file")
    moov = exactly_one(top, "moov", "top-level file")
    moov_children = box_children(data, moov["payload"], moov["end"], "moov")
    video_track: dict[str, Any] | None = None
    for trak in [box for box in moov_children if box["type"] == "trak"]:
        trak_children = box_children(data, trak["payload"], trak["end"], "trak")
        mdia = exactly_one(trak_children, "mdia", "trak")
        mdia_children = box_children(data, mdia["payload"], mdia["end"], "mdia")
        hdlr = exactly_one(mdia_children, "hdlr", "mdia")
        handler = bytes(data[hdlr["payload"] + 8:hdlr["payload"] + 12])
        if handler == b"vide":
            require(video_track is None, "Mutation source has multiple video tracks.")
            video_track = {"trak": trak, "mdiaChildren": mdia_children}
    require(video_track is not None, "Mutation source has no video track.")
    minf = exactly_one(video_track["mdiaChildren"], "minf", "video mdia")
    minf_children = box_children(data, minf["payload"], minf["end"], "video minf")
    stbl = exactly_one(minf_children, "stbl", "video minf")
    stbl_children = box_children(data, stbl["payload"], stbl["end"], "video stbl")
    stsd = exactly_one(stbl_children, "stsd", "video stbl")
    require(stsd["end"] - stsd["payload"] >= 8, "Video stsd is truncated.")
    entry_count = int.from_bytes(data[stsd["payload"] + 4:stsd["payload"] + 8], "big")
    entries = box_children(data, stsd["payload"] + 8, stsd["end"], "video sample descriptions")
    require(entry_count == 1 and len(entries) == 1, "Mutation source must have exactly one video sample entry.")
    entry = entries[0]
    require(entry["type"] in {"hvc1", "hev1"}, "Mutation source is not a HEVC sample entry.")
    child_start = entry["payload"] + 78
    require(child_start <= entry["end"], "HEVC sample entry is shorter than its fixed header.")
    sample_children = box_children(data, child_start, entry["end"], "HEVC visual sample entry")
    color = exactly_one(sample_children, "colr", "HEVC visual sample entry")
    require(color["end"] - color["payload"] == 11 and bytes(data[color["payload"]:color["payload"] + 4]) == b"nclx", "Expected one exact nclx CICP child.")
    transfer_offset = color["payload"] + 6
    previous = int.from_bytes(data[transfer_offset:transfer_offset + 2], "big")
    replacement = 1 if previous != 1 else 18
    data[transfer_offset:transfer_offset + 2] = replacement.to_bytes(2, "big")
    require(not target.exists(), f"Refusing to overwrite malformed-facts fixture {target}.")
    write_create_only(target, bytes(data))
    return {
        "sourcePath": str(source),
        "sourceSha256": sha256_file(source),
        "fixturePath": str(target),
        "fixtureSha256": sha256_file(target),
        "mutation": {
            "containerPath": "moov/trak[handler=vide]/mdia/minf/stbl/stsd/hvc1/colr[nclx]",
            "field": "transfer_characteristics",
            "originalValue": previous,
            "replacementValue": replacement,
            "sourceFileWasCopiedBeforeMutation": True,
        },
    }


def check_target_fps(report: dict[str, Any]) -> dict[str, Any]:
    truth = report["executionRecord"]["truth"]
    require(report["success"] is False and report.get("outputArtifact") is None, "TargetFps failure produced success or an artifact.")
    require(truth.get("failureCategory") == "Unsupported", "TargetFps failure category is not Unsupported.")
    require(truth.get("failureStage") == "InvalidRequest", "TargetFps did not fail before request dispatch.")
    require(truth.get("actualOperationKind") == "Unsupported", "TargetFps actual operation is not Unsupported.")
    require(truth.get("selectedCapability") == "Unknown", "TargetFps invented a selected backend capability.")
    require(truth.get("preservationOutcome") == "Unsupported", "TargetFps failure preservation outcome is not Unsupported.")
    require(not report.get("ownedResidues"), "TargetFps failure left converter-owned residue.")
    return {"caseId": "target-fps-positive", "checksPassed": True, "canonicalTruth": truth, "noOutput": True}


def check_malformed(report: dict[str, Any]) -> dict[str, Any]:
    truth = report["executionRecord"]["truth"]
    require(report["success"] is False and report.get("outputArtifact") is None, "Malformed CICP facts produced success or an artifact.")
    require(truth.get("failureCategory") == "SourceInspection", "Malformed CICP facts were not classified as SourceInspection.")
    require(truth.get("failureStage") == "SourceInspection", "Malformed CICP facts did not fail during source inspection.")
    require(truth.get("selectedCapability") == "Unknown", "Malformed CICP facts selected a backend.")
    require(truth.get("preservationOutcome") == "Unsupported", "Malformed CICP facts did not fail closed.")
    require(not report.get("ownedResidues"), "Malformed-facts failure left converter-owned residue.")
    return {"caseId": "ambiguous-or-malformed-preservation-facts", "checksPassed": True, "canonicalTruth": truth, "noOutput": True}


def check_cancellation(report: dict[str, Any]) -> dict[str, Any]:
    require(report.get("cancelledAfterStagingStarted") is True, "Cancellation was not requested after the Native staging object appeared.")
    require(report.get("operationCancelled") is True, "The conversion did not return cancellation.")
    require(report.get("operationSucceeded") is False, "Cancelled conversion unexpectedly succeeded.")
    require(report.get("noFinalArtifact") is True and report.get("stagingRemoved") is True, "Cancellation left a final or staging artifact.")
    require(not report.get("ownedResidues"), "Cancellation left converter-owned residue.")
    return {
        "caseId": "cancellation",
        "checksPassed": True,
        "truthfulCancellation": "OperationCanceledException after Native-owned staging was observed",
        "observedStaging": report.get("observedStaging"),
        "noFinalOrStagingResidue": True,
    }


def check_publish_failure(report: dict[str, Any]) -> dict[str, Any]:
    conversion = report["canonicalConversion"]
    truth = conversion["executionRecord"]["truth"]
    require(conversion["success"] is True, f"Control conversion for publish case failed: {conversion.get('errorMessage')}.")
    require(truth.get("actualOperationKind") == "ContainerRemux", "Control conversion did not produce truthful remux canonical truth.")
    require(truth.get("selectedCapability") == "VideoRemux", "Control conversion did not use VideoRemux.")
    publish = report["publishFailure"]
    require(publish.get("success") is False and publish.get("wasRemux") is False, "Caller-owned publication failure returned success/remux.")
    require("No-overwrite output publication failed" in (publish.get("errorMessage") or ""), "Facade did not report no-overwrite publication failure.")
    require(publish.get("callerBytesUnchanged") is True, "Caller-owned destination bytes changed.")
    require(publish.get("noConverterArtifact") is True and not publish.get("ownedResidues"), "Publish failure left a converter-owned artifact.")
    return {
        "caseId": "output-validation-or-publish-failure",
        "checksPassed": True,
        "canonicalConverterTruth": truth,
        "publishFailure": publish,
        "noPartialPublish": True,
    }


def check_sidecar_missing(report: dict[str, Any], build_dir: Path) -> dict[str, Any]:
    truth = report
    require(truth.get("success") is False and truth.get("outputExists") is False, "Missing-sidecar conversion produced output.")
    require(truth.get("failureCategory") == "BackendUnavailable", "Missing sidecar was not reported as BackendUnavailable.")
    require(truth.get("failureStage") == "BackendUnavailable", "Missing sidecar failure stage is incorrect.")
    require(truth.get("selectedCapability") == "VideoTranscodeHdr10Bit", "Missing sidecar lost the selected HDR capability.")
    require(truth.get("preservationOutcome") == "Unsupported", "Missing-sidecar preservation outcome is not Unsupported.")
    require(truth.get("backend") == "minimal-libav" and truth.get("backendVersion") == "not-packaged", "Missing-sidecar backend identity is not truthful.")
    require(truth.get("fallbackOccurred") is False, "Missing sidecar reports a fallback.")
    require(truth.get("residualOwnedOutput") is False, "Missing-sidecar attempt left converter output.")
    sidecar = build_dir / "x64" / "Release" / "net9.0-windows10.0.19041.0" / "win-x64" / "LivePhotoBox.Video.Libav.dll"
    require(not sidecar.exists(), f"Missing-sidecar package unexpectedly contains {sidecar}.")
    return {
        "caseId": "real-sidecar-missing",
        "checksPassed": True,
        "canonicalTruth": {key: truth.get(key) for key in (
            "failureCategory", "failureStage", "selectedCapability", "preservationOutcome",
            "backend", "backendVersion", "inputProfile", "outputProfile", "fallbackOccurred",
        )},
        "packageDirectory": str(build_dir),
        "sidecarAbsent": True,
        "noOutputOrResidue": True,
    }


def source_orphan_snapshot(root: Path) -> dict[str, Any]:
    path = root / ORPHAN_RELATIVE
    if not path.exists():
        return {"path": str(path), "exists": False, "size": None, "sha256": None}
    require(path.is_file(), "Known iteration-5 orphan path is no longer a file; preserve and report it.")
    return {"path": str(path), "exists": True, "size": path.stat().st_size, "sha256": sha256_file(path)}


def validate_record_id(record_id: str) -> None:
    require(bool(re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]{0,63}", record_id)), "Record id must be a unique safe identifier.")


def run_identity_preflight(args: argparse.Namespace) -> int:
    root = Path(__file__).resolve().parents[2]
    manifest, profile_path, erratum, erratum_path, erratum_sha256 = verify_manifest(root)
    record_id = args.record_id
    validate_record_id(record_id)
    run_root = root / ".ai-tmp/workspace/P5-R5" / record_id
    require(not run_root.exists(), f"Refusing to reuse preflight workspace {run_root}.")
    run_root.mkdir(parents=True, exist_ok=False)
    (run_root / "raw").mkdir()

    orphan_before = source_orphan_snapshot(root)
    runner = Runner(
        root=root,
        run_root=run_root,
        manifest=manifest,
        ffmpeg=Path(),
        ffprobe=Path(),
        dotnet="",
        driver_dll=Path(),
        command_records=[],
        source_paths={},
        source_hashes_before={},
        tool_version_records={},
    )
    partial: dict[str, Any] = {
        "schemaVersion": 1,
        "runnerVersion": RUNNER_VERSION,
        "profileId": manifest["profileId"],
        "recordId": record_id,
        "state": "running",
        "workspace": str(run_root),
        "manifestSha256": sha256_file(profile_path),
        "toolIdentityErratum": {
            "id": erratum["erratumId"],
            "path": relative(erratum_path, root),
            "sha256": erratum_sha256,
            "targetField": erratum["targetField"],
        },
        "p4SourceManifestSha256": sha256_file(root / manifest["sourceManifest"]["path"]),
        "knownOrphanBefore": orphan_before,
        "commands": runner.command_records,
    }
    try:
        sources, source_hashes = validate_sources(root, manifest)
        runner.source_paths = sources
        runner.source_hashes_before = source_hashes
        runner.ffmpeg = resolve_tool(args.ffmpeg, "FFMPEG_EXE", "ffmpeg")
        runner.ffprobe = resolve_tool(args.ffprobe, "FFPROBE_EXE", "ffprobe")
        frozen_tools = manifest["independentValidation"]
        runner.tool_version_records["ffprobe"] = validate_tool(
            runner, "ffprobe", runner.ffprobe, frozen_tools["ffprobe"]
        )
        runner.tool_version_records["ffmpeg"] = validate_tool(
            runner,
            "ffmpeg",
            runner.ffmpeg,
            frozen_tools["ffmpeg"],
            expected_version=erratum["correctedValue"],
        )
        require(
            [item["label"] for item in runner.command_records] == ["ffprobe-version", "ffmpeg-version"],
            "Identity preflight ran a command outside the two pinned tool version checks.",
        )
        final_source_hashes = {source_id: sha256_file(path) for source_id, path in sources.items()}
        require(final_source_hashes == source_hashes, "A hash-locked cached source changed during identity preflight.")
        p4_manifest_hash = sha256_file(root / manifest["sourceManifest"]["path"])
        require(p4_manifest_hash == EXPECTED_P4_MANIFEST_SHA256, "P4-v1 manifest changed during identity preflight.")
        orphan_after = source_orphan_snapshot(root)
        require(orphan_before == orphan_after, "The explicitly preserved iteration-5 orphan changed during identity preflight.")
        report = {
            **partial,
            "state": "identity-preflight-pass",
            "p4SourceManifestSha256": p4_manifest_hash,
            "sourceHashesBefore": source_hashes,
            "sourceHashesAfter": final_source_hashes,
            "tools": runner.tool_version_records,
            "requiredRouteIds": sorted(EXPECTED_ROUTES),
            "requiredFailureCaseIds": sorted(EXPECTED_FAILURES),
            "skipPolicy": manifest["independentValidation"]["skipPolicy"],
            "routesExecuted": 0,
            "failureCasesExecuted": 0,
            "skipCount": 0,
            "productConversionCommandsExecuted": 0,
            "mediaConversionStarted": False,
            "huaweiExtractionStarted": False,
            "knownOrphan": {
                "before": orphan_before,
                "after": orphan_after,
                "preserved": True,
                "unresolvedExternalR5Blocker": orphan_before.get("exists") is True,
            },
            "commands": runner.command_records,
        }
        report_path = run_root / "preflight-report.json"
        write_create_only(report_path, json_bytes(report))
        print(json.dumps({
            "state": report["state"],
            "report": str(report_path),
            "manifestSha256": report["manifestSha256"],
            "erratumSha256": report["toolIdentityErratum"]["sha256"],
            "routesExecuted": report["routesExecuted"],
            "failureCasesExecuted": report["failureCasesExecuted"],
            "skipCount": report["skipCount"],
            "mediaConversionStarted": report["mediaConversionStarted"],
        }, ensure_ascii=False))
        return 0
    except Exception as ex:
        partial["state"] = "identity-preflight-failed"
        partial["failure"] = {"type": type(ex).__name__, "message": str(ex)}
        partial["tools"] = runner.tool_version_records
        partial["commands"] = runner.command_records
        try:
            write_create_only(run_root / "preflight-failure-report.json", json_bytes(partial))
        except FileExistsError:
            pass
        print(json.dumps({
            "state": "identity-preflight-failed",
            "report": str(run_root / "preflight-failure-report.json"),
            "error": str(ex),
            "commandsCompleted": len(runner.command_records),
        }, ensure_ascii=False), file=sys.stderr)
        return 1


def run_matrix(args: argparse.Namespace) -> int:
    root = Path(__file__).resolve().parents[2]
    manifest, profile_path, erratum, erratum_path, erratum_sha256 = verify_manifest(root)
    record_id = args.record_id
    validate_record_id(record_id)
    run_root = root / ".ai-tmp/workspace/P5-R5" / record_id
    require(not run_root.exists(), f"Refusing to reuse matrix workspace {run_root}.")
    run_root.mkdir(parents=True, exist_ok=False)
    (run_root / "raw").mkdir()

    sources, source_hashes = validate_sources(root, manifest)
    ffmpeg = resolve_tool(args.ffmpeg, "FFMPEG_EXE", "ffmpeg")
    ffprobe = resolve_tool(args.ffprobe, "FFPROBE_EXE", "ffprobe")
    dotnet = shutil.which("dotnet")
    require(dotnet is not None, "dotnet was not found on PATH.")
    driver_dll = root / "tools/p5-r5-video-validation/route-driver/bin/x64/Release/net9.0-windows10.0.19041.0/P5R5.RouteDriver.dll"
    require(driver_dll.is_file(), "Route driver Release DLL is missing; build it before running the matrix.")
    orphan_before = source_orphan_snapshot(root)
    runner = Runner(
        root=root,
        run_root=run_root,
        manifest=manifest,
        ffmpeg=ffmpeg,
        ffprobe=ffprobe,
        dotnet=dotnet,
        driver_dll=driver_dll,
        command_records=[],
        source_paths=sources,
        source_hashes_before=source_hashes,
        tool_version_records={},
    )
    partial: dict[str, Any] = {
        "schemaVersion": 1,
        "runnerVersion": RUNNER_VERSION,
        "profileId": "P5-R5-v1",
        "recordId": record_id,
        "state": "running",
        "workspace": str(run_root),
        "manifestSha256": sha256_file(profile_path),
        "toolIdentityErratum": {
            "id": erratum["erratumId"],
            "path": relative(erratum_path, root),
            "sha256": erratum_sha256,
            "targetField": erratum["targetField"],
        },
        "p4SourceManifestSha256": sha256_file(root / manifest["sourceManifest"]["path"]),
        "sourceHashesBefore": source_hashes,
        "knownOrphanBefore": orphan_before,
        "routeResults": [],
        "failureResults": [],
        "commands": runner.command_records,
    }
    try:
        frozen_tools = manifest["independentValidation"]
        runner.tool_version_records["ffprobe"] = validate_tool(runner, "ffprobe", ffprobe, frozen_tools["ffprobe"])
        runner.tool_version_records["ffmpeg"] = validate_tool(
            runner, "ffmpeg", ffmpeg, frozen_tools["ffmpeg"], expected_version=erratum["correctedValue"]
        )
        partial["tools"] = runner.tool_version_records
        print(json.dumps({
            "event": "preflight-passed",
            "profileId": manifest["profileId"],
            "sourceManifestSha256": manifest["sourceManifest"]["sha256"],
            "ffprobeSha256": runner.tool_version_records["ffprobe"]["sha256"],
            "ffmpegSha256": runner.tool_version_records["ffmpeg"]["sha256"],
            "sourceIds": sorted(sources),
        }, ensure_ascii=False), flush=True)

        extraction_root = run_root / "huawei-extraction"
        extraction = runner.route_driver(
            "canonical-huawei-extraction",
            [
                "extract-huawei", str(sources["huawei-parent-jpeg"]), str(extraction_root),
                EXPECTED_PARENT_SHA256, str(EXPECTED_PARENT_BYTES),
            ],
            timeout=300,
        )
        require(extraction.get("parentSha256") == EXPECTED_PARENT_SHA256, "Canonical Huawei extraction used the wrong parent.")
        require(extraction.get("range", {}).get("sourceIndex") == 0, "Canonical Huawei extraction source index changed.")
        require(extraction.get("range") == {
            "offset": EXPECTED_HUAWEI_RANGE_OFFSET,
            "length": EXPECTED_HUAWEI_RANGE_BYTES,
            "sourceIndex": extraction.get("range", {}).get("sourceIndex"),
            "sha256": EXPECTED_HUAWEI_VIDEO_SHA256,
        }, "Canonical Huawei extraction range or derived SHA-256 changed.")
        require(extraction.get("artifactBytes") == EXPECTED_HUAWEI_RANGE_BYTES, "Canonical Huawei extracted byte count changed.")
        require(extraction.get("artifactSha256") == EXPECTED_HUAWEI_VIDEO_SHA256, "Canonical Huawei extracted video identity changed.")
        huawei_path = Path(extraction["path"]).resolve()
        require(huawei_path.is_file(), "Canonical Huawei extracted video is missing.")
        extraction_probe = runner.probe(huawei_path, "canonical-huawei-independent-probe")
        huawei_video = one_stream(extraction_probe, "video")
        require(huawei_video.get("codec_name") == "hevc" and huawei_video.get("profile") == "Main 10", "Independent Huawei source profile differs from frozen Main10.")
        require(huawei_video.get("pix_fmt") == "yuv420p10le", "Independent Huawei source is not 10-bit.")
        require(huawei_video.get("color_primaries") == "bt2020" and huawei_video.get("color_transfer") == "arib-std-b67" and huawei_video.get("color_space") == "bt2020nc" and huawei_video.get("color_range") == "pc", "Independent Huawei source CICP/range differs from the frozen facts.")
        require(rotation_degrees(huawei_video) == -90, "Independent Huawei source rotation differs from the frozen -90 degrees.")
        extraction_probe_command = runner.command_records[-1]
        partial["canonicalHuaweiExtraction"] = {
            "driver": extraction,
            "independentProbe": {
                "command": extraction_probe_command,
                "video": tool_fields(extraction_probe)[0],
                "audioAndMebxStreams": tool_fields(extraction_probe)[1:],
                "rawProbeOutput": runner.command_records[-1]["stdout"],
            },
        }
        print(json.dumps({
            "event": "canonical-huawei-extraction-passed",
            "parentSha256": extraction.get("parentSha256"),
            "range": extraction.get("range"),
            "derivedVideoSha256": extraction.get("artifactSha256"),
        }, ensure_ascii=False), flush=True)

        malformed_fixture_path = run_root / "fixtures" / "huawei-malformed-cicp.mp4"
        malformed_fixture_path.parent.mkdir(parents=True, exist_ok=False)
        malformed_fixture = mutate_huawei_cicp_conflict(huawei_path, malformed_fixture_path)
        partial["malformedFactsFixture"] = malformed_fixture
        source_probe_cache: dict[str, dict[str, Any]] = {
            "huawei-parent-jpeg": extraction_probe,
        }
        source_probe_commands: dict[str, dict[str, Any]] = {
            "huawei-parent-jpeg": extraction_probe_command,
        }
        route_results: list[dict[str, Any]] = []
        for route in manifest["requiredRoutes"]:
            route_id = route["id"]
            logical_source_id = route["source"]
            source_path = huawei_path if route.get("derivedSource") else sources[logical_source_id]
            route_source_probe = source_probe_cache.get(logical_source_id)
            if route_source_probe is None:
                route_source_probe = runner.probe(source_path, f"{route_id}-independent-source-probe")
                source_probe_cache[logical_source_id] = route_source_probe
                source_probe_commands[logical_source_id] = runner.command_records[-1]
            route_directory = run_root / "routes" / route_id
            target_container = route["targetContainer"]
            target_codec = route["targetCodec"]
            driver = runner.route_driver(
                route_id,
                ["route", route_id, str(source_path), str(route_directory)],
                timeout=1200,
            )
            require(driver.get("id") == route_id, f"{route_id} driver report does not match its requested route.")
            artifact = driver.get("outputArtifact")
            if route_id == "huawei-main10-hlg-to-h264-preservation-required":
                require(driver.get("success") is False and artifact is None, "Huawei H.264 preservation route should fail closed without output.")
                truth = driver["executionRecord"]["truth"]
                record = driver["executionRecord"]
                require(truth.get("failureCategory") == "Unsupported", "Huawei H.264 failure was not Unsupported.")
                require(truth.get("failureStage") == "OutputValidation", "Huawei H.264 failure stage is not OutputValidation.")
                require(truth.get("preservationOutcome") == "Unsupported", "Huawei H.264 preservation result is not Unsupported.")
                require(truth.get("selectedCapability") == "VideoTranscodeHdr10Bit", "Huawei H.264 did not select the HDR capability.")
                require(truth.get("backendName") == "minimal-libav" and truth.get("backendVersion") == "9.0.1", "Huawei H.264 did not record the selected minimal-libav version.")
                require(record.get("hardwareMode") == "SoftwareForced" and truth.get("fallbackOccurred") is False, "Huawei H.264 failure reports fallback or wrong hardware mode.")
                require(truth.get("outputProfile") == "Unknown", "Huawei H.264 failure claimed an unverified output profile.")
                require(not driver.get("ownedResidues"), "Huawei H.264 failure left a final or Native staging artifact.")
                route_results.append({
                    "routeId": route_id,
                    "checksPassed": True,
                    "expectedFailure": True,
                    "truth": truth,
                    "actualAttempt": {
                        "backend": truth.get("backendName"),
                        "version": truth.get("backendVersion"),
                        "selectedCapability": truth.get("selectedCapability"),
                        "hardwareMode": record.get("hardwareMode"),
                        "outputCandidateObserved": True,
                        "noMfRetry": True,
                    },
                    "noOutputOrResidue": True,
                    "sourceSha256": sha256_file(source_path),
                    "sourceProbe": {
                        "command": source_probe_commands[logical_source_id],
                        "streamInventory": tool_fields(route_source_probe),
                    },
                })
                partial["routeResults"] = route_results
                print(json.dumps({
                    "event": "route-passed",
                    "routeId": route_id,
                    "passedRouteCount": sum(1 for item in route_results if item["checksPassed"]),
                    "requiredRouteCount": 9,
                }, ensure_ascii=False), flush=True)
                continue

            require(isinstance(artifact, dict) and Path(artifact["path"]).is_file(), f"{route_id} output artifact is missing.")
            output_path = Path(artifact["path"]).resolve()
            require(output_path.is_relative_to(route_directory.resolve()), f"{route_id} output escaped its isolated matrix workspace.")
            output_hash = sha256_file(output_path)
            require(output_hash == artifact.get("sha256"), f"{route_id} driver output hash does not match disk.")
            output_probe = runner.probe(output_path, f"{route_id}-independent-output-probe")
            output_probe_command = runner.command_records[-1]
            if route["targetCodec"] == "copy":
                checks = check_remux_route(route_id, target_container, route_source_probe, output_probe, source_path, output_path, driver)
            elif logical_source_id == "huawei-parent-jpeg":
                checks = check_huawei_route(runner, route_id, source_path, output_path, route_source_probe, output_probe, driver)
            else:
                checks = check_mf_route(runner, route_id, target_container, target_codec, source_path, output_path, route_source_probe, output_probe, driver)
            checks["request"] = {"targetContainer": target_container, "targetCodec": target_codec}
            checks["canonicalTruth"] = driver["executionRecord"]["truth"]
            checks["productExecution"] = driver["executionRecord"]
            checks["sourceProbe"] = {
                "command": source_probe_commands[logical_source_id],
                "streamInventory": tool_fields(route_source_probe),
            }
            checks["outputProbe"] = {
                "command": output_probe_command,
                "streamInventory": tool_fields(output_probe),
                "format": output_probe["format"],
            }
            route_results.append(checks)
            partial["routeResults"] = route_results
            print(json.dumps({
                "event": "route-passed",
                "routeId": route_id,
                "passedRouteCount": sum(1 for item in route_results if item["checksPassed"]),
                "requiredRouteCount": 9,
            }, ensure_ascii=False), flush=True)

        failure_results: list[dict[str, Any]] = []
        target_fps_dir = run_root / "failures" / "target-fps-positive"
        target_fps = runner.route_driver(
            "failure-target-fps-positive",
            ["failure", "target-fps-positive", str(sources["apple-motion-video"]), str(target_fps_dir)],
        )
        failure_results.append(check_target_fps(target_fps))
        partial["failureResults"] = failure_results
        print(json.dumps({"event": "failure-case-passed", "caseId": "target-fps-positive", "passedFailureCount": len(failure_results), "requiredFailureCount": 5}), flush=True)

        malformed_dir = run_root / "failures" / "ambiguous-or-malformed-preservation-facts"
        malformed = runner.route_driver(
            "failure-malformed-preservation-facts",
            ["failure", "ambiguous-or-malformed-preservation-facts", str(malformed_fixture_path), str(malformed_dir)],
        )
        failure_results.append(check_malformed(malformed))
        partial["failureResults"] = failure_results
        print(json.dumps({"event": "failure-case-passed", "caseId": "ambiguous-or-malformed-preservation-facts", "passedFailureCount": len(failure_results), "requiredFailureCount": 5}), flush=True)

        cancellation_dir = run_root / "failures" / "cancellation"
        cancellation = runner.route_driver(
            "failure-cancellation",
            ["failure", "cancellation", str(huawei_path), str(cancellation_dir)],
            timeout=1200,
        )
        failure_results.append(check_cancellation(cancellation))
        partial["failureResults"] = failure_results
        print(json.dumps({"event": "failure-case-passed", "caseId": "cancellation", "passedFailureCount": len(failure_results), "requiredFailureCount": 5}), flush=True)

        publish_dir = run_root / "failures" / "output-validation-or-publish-failure"
        publish = runner.route_driver(
            "failure-output-publish",
            ["failure", "output-validation-or-publish-failure", str(sources["apple-motion-video"]), str(publish_dir)],
            timeout=1200,
        )
        failure_results.append(check_publish_failure(publish))
        partial["failureResults"] = failure_results
        print(json.dumps({"event": "failure-case-passed", "caseId": "output-validation-or-publish-failure", "passedFailureCount": len(failure_results), "requiredFailureCount": 5}), flush=True)

        missing_sidecar_build = run_root / "missing-sidecar-build"
        missing_sidecar_base_output = missing_sidecar_build / "bin"
        missing_sidecar_cmd = [
            dotnet, "build",
            str(root / "tools/p5-r5-video-validation/managed-smoke/P5R5.ManagedSmoke.csproj"),
            "-c", "Release", "-p:Platform=x64", "-p:SkipNativeBuild=true",
            f"-p:BaseOutputPath={str(missing_sidecar_base_output) + os.sep}",
            "--no-restore", "-nologo", "-v:minimal",
        ]
        _, build_stdout, build_stderr = runner.require_command_success("missing-sidecar-package-build", missing_sidecar_cmd, timeout=600)
        managed_dll = missing_sidecar_base_output / "x64" / "Release" / "net9.0-windows10.0.19041.0" / "win-x64" / "P5R5.ManagedSmoke.dll"
        require(managed_dll.is_file(), "Missing-sidecar smoke DLL was not created in its isolated package.")
        output_dir = run_root / "failures" / "real-sidecar-missing" / "output"
        output_dir.parent.mkdir(parents=True, exist_ok=False)
        smoke_argv = [
            dotnet, str(managed_dll), "missing-sidecar", str(huawei_path), str(output_dir),
        ]
        _, smoke_stdout, smoke_stderr = runner.require_command_success("real-missing-sidecar-runtime", smoke_argv, timeout=300)
        missing_report = parse_json(smoke_stdout, "managed missing-sidecar smoke")
        failure_results.append(check_sidecar_missing(missing_report, missing_sidecar_base_output))
        partial["failureResults"] = failure_results
        print(json.dumps({"event": "failure-case-passed", "caseId": "real-sidecar-missing", "passedFailureCount": len(failure_results), "requiredFailureCount": 5}), flush=True)
        partial["missingSidecarBuild"] = {
            "stdoutSha256": sha256_bytes(build_stdout),
            "stderrSha256": sha256_bytes(build_stderr),
            "packageOutput": str(missing_sidecar_base_output),
            "sidecarDll": str(missing_sidecar_base_output / "x64" / "Release" / "net9.0-windows10.0.19041.0" / "win-x64" / "LivePhotoBox.Video.Libav.dll"),
            "runtimeSmoke": missing_report,
            "runtimeStdoutSha256": sha256_bytes(smoke_stdout),
            "runtimeStderrSha256": sha256_bytes(smoke_stderr),
        }

        require({item["routeId"] for item in route_results} == EXPECTED_ROUTES, "Route execution set is incomplete.")
        require(len(route_results) == 9 and all(item["checksPassed"] for item in route_results), "At least one required route did not pass.")
        require({item["caseId"] for item in failure_results} == EXPECTED_FAILURES, "Required failure-case execution set is incomplete.")
        require(len(failure_results) == 5 and all(item["checksPassed"] for item in failure_results), "At least one required failure case did not pass.")
        require(sha256_file(sources["huawei-parent-jpeg"]) == EXPECTED_PARENT_SHA256, "Huawei parent changed during the matrix.")
        require(sha256_file(huawei_path) == EXPECTED_HUAWEI_VIDEO_SHA256, "Canonical Huawei extracted source changed during the matrix.")
        require(sha256_file(malformed_fixture_path) == malformed_fixture["fixtureSha256"], "Malformed fixture changed after the recorded CICP mutation.")
        orphan_after = source_orphan_snapshot(root)
        require(orphan_before == orphan_after, "The explicitly preserved iteration-5 orphan changed during the matrix.")
        final_source_hashes = {source_id: sha256_file(path) for source_id, path in sources.items()}
        require(final_source_hashes == source_hashes, "A hash-locked cached source changed during the matrix.")
        p4_manifest_hash = sha256_file(root / manifest["sourceManifest"]["path"])
        require(p4_manifest_hash == EXPECTED_P4_MANIFEST_SHA256, "P4-v1 manifest changed during the matrix.")
        source_code_paths = [
            root / "tools/p5-r5-video-validation/validate.py",
            erratum_path,
            root / "tools/p5-r5-video-validation/route-driver/Program.cs",
            root / "tools/p5-r5-video-validation/route-driver/P5R5.RouteDriver.csproj",
            root / "tools/p5-r5-video-validation/managed-smoke/Program.cs",
            root / "tools/p5-r5-video-validation/managed-smoke/P5R5.ManagedSmoke.csproj",
        ]
        source_code_hashes = {relative(path, root): sha256_file(path) for path in source_code_paths}
        report = {
            "schemaVersion": 1,
            "runnerVersion": RUNNER_VERSION,
            "profileId": manifest["profileId"],
            "recordId": record_id,
            "state": "matrix-pass",
            "workspace": str(run_root),
            "manifest": {
                "path": relative(profile_path, root),
                "sha256": sha256_file(profile_path),
                "sourceManifestPath": manifest["sourceManifest"]["path"],
                "sourceManifestSha256": p4_manifest_hash,
            },
            "toolIdentityErratum": partial["toolIdentityErratum"],
            "runnerAndDriverSourceHashes": source_code_hashes,
            "tools": runner.tool_version_records,
            "sourceHashesBefore": source_hashes,
            "sourceHashesAfter": final_source_hashes,
            "canonicalHuaweiExtraction": extraction,
            "huaweiIndependentProbe": {
                "video": next(item for item in tool_fields(extraction_probe) if item["codecType"] == "video"),
                "sourceFacts": {
                    "audio": [item for item in tool_fields(extraction_probe) if item["codecType"] == "audio"],
                    "mebx": [item for item in tool_fields(extraction_probe) if item["codecTag"] == "mebx"],
                },
                "independentProbeCommand": extraction_probe_command,
            },
            "malformedFactsFixture": malformed_fixture,
            "requiredRouteCount": 9,
            "passedRouteCount": len(route_results),
            "requiredFailureCount": 5,
            "passedFailureCount": len(failure_results),
            "skipCount": 0,
            "routeResults": route_results,
            "failureResults": failure_results,
            "commands": runner.command_records,
            "knownOrphan": {
                "before": orphan_before,
                "after": orphan_after,
                "preserved": True,
                "unresolvedExternalR5Blocker": orphan_before.get("exists") is True,
            },
            "statusNote": (
                "The matrix pass is not R5/P5 acceptance. The separately recorded iteration-5 orphan was absent before and after this matrix."
                if not orphan_before.get("exists")
                else "The matrix pass is not R5/P5 acceptance. The preserved iteration-5 orphan remains a separately recorded closeout blocker."
            ),
        }
        report_path = run_root / "report.json"
        write_create_only(report_path, json_bytes(report))
        print(json.dumps({
            "state": report["state"],
            "report": str(report_path),
            "requiredRouteCount": report["requiredRouteCount"],
            "passedRouteCount": report["passedRouteCount"],
            "requiredFailureCount": report["requiredFailureCount"],
            "passedFailureCount": report["passedFailureCount"],
            "skipCount": report["skipCount"],
            "unresolvedExternalR5Blocker": report["knownOrphan"]["unresolvedExternalR5Blocker"],
        }, ensure_ascii=False))
        return 0
    except Exception as ex:
        partial["state"] = "matrix-failed"
        partial["failure"] = {"type": type(ex).__name__, "message": str(ex)}
        partial["tools"] = runner.tool_version_records
        partial["commands"] = runner.command_records
        try:
            write_create_only(run_root / "failure-report.json", json_bytes(partial))
        except FileExistsError:
            pass
        print(json.dumps({
            "state": "matrix-failed",
            "report": str(run_root / "failure-report.json"),
            "error": str(ex),
            "commandsCompleted": len(runner.command_records),
        }, ensure_ascii=False), file=sys.stderr)
        return 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--record-id", required=True, help="Unique create-only run ID; use a stable, explicit ID.")
    parser.add_argument("--ffmpeg", help="Optional exact path to the frozen ffmpeg.exe.")
    parser.add_argument("--ffprobe", help="Optional exact path to the frozen ffprobe.exe.")
    parser.add_argument(
        "--preflight-only",
        action="store_true",
        help="Verify frozen profile, source and tool identities without extraction or product conversion.",
    )
    args = parser.parse_args()
    if args.preflight_only:
        return run_identity_preflight(args)
    return run_matrix(args)


if __name__ == "__main__":
    raise SystemExit(main())
