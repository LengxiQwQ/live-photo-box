#!/usr/bin/env python3
"""Acquire and verify the versioned R4 upstream sample provenance lock.

`refresh` is the only online step. Ordinary validation uses the tracked lock
and deterministic, hash-locked sample cache offline.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path
from typing import Any


EXPECTED_PROFILE = "P5-R4-v1"
LOCK_SCHEMA = 1
README_PATH = "README.md"
LICENSE_PATH = "LICENSE"
README_EVIDENCE = ["Nikon Z8", "Adobe Lightroom Classic", "different_file_formats_in_sdr_and_hdr", "Creative Commons Zero (CC0) license"]
LICENSE_EVIDENCE = ["Creative Commons Legal Code", "CC0 1.0 Universal"]


class SourceLockError(RuntimeError):
    pass


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest().upper()


def _git_blob_sha1(data: bytes) -> str:
    header = b"blob " + str(len(data)).encode("ascii") + b"\0"
    return hashlib.sha1(header + data).hexdigest()


def _raw_url(repository: str, revision: str, path: str) -> str:
    return f"https://raw.githubusercontent.com/{repository}/{revision}/{path}"


def _fetch(url: str) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": "LivePhotoBox-P5-R4-SourceLock/1"})
    try:
        with urllib.request.urlopen(request, timeout=45) as response:
            if response.status != 200:
                raise SourceLockError(f"Upstream fetch returned HTTP {response.status}: {url}")
            return response.read()
    except (OSError, urllib.error.URLError) as exc:
        raise SourceLockError(f"Unable to fetch pinned upstream source {url}: {exc}") from exc


def _required_provenance(manifest: dict[str, Any]) -> tuple[str, str, list[dict[str, Any]]]:
    validation = manifest.get("validation", {})
    if manifest.get("profile") != EXPECTED_PROFILE or manifest.get("schemaVersion") != LOCK_SCHEMA:
        raise SourceLockError("Unexpected profile or manifest schema for the P5-R4 source lock.")
    upstream_samples = [
        sample for sample in manifest.get("samples", [])
        if not sample.get("provenance", {}).get("p4Reference")
    ]
    if len(upstream_samples) != 2:
        raise SourceLockError("R4-v1 must bind exactly the two pinned Nikon upstream samples.")
    first = upstream_samples[0]["provenance"]
    repository = first.get("repository")
    revision = first.get("revision")
    if not isinstance(repository, str) or not isinstance(revision, str) or not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise SourceLockError("Manifest is missing a repository or full immutable upstream commit SHA.")
    for sample in upstream_samples:
        provenance = sample["provenance"]
        if provenance.get("repository") != repository or provenance.get("revision") != revision:
            raise SourceLockError("R4 upstream samples must use the same repository and pinned commit.")
        source_path = provenance.get("sourcePath")
        if not isinstance(source_path, str) or source_path.startswith("/") or ".." in Path(source_path).parts:
            raise SourceLockError(f"Unsafe upstream source path for {sample.get('id')}.")
        if provenance.get("sourceUrl") != f"https://github.com/{repository}/blob/{revision}/{source_path}":
            raise SourceLockError(f"Source URL is not pinned to the declared commit for {sample.get('id')}.")
        if provenance.get("gitLfsObjectSha256", "").upper() != sample.get("sha256", "").upper():
            raise SourceLockError(f"Manifest LFS object hash disagrees with cached sample {sample.get('id')}.")
        if provenance.get("gitLfsObjectByteSize") != sample.get("byteSize"):
            raise SourceLockError(f"Manifest LFS object size disagrees with cached sample {sample.get('id')}.")
        if not re.fullmatch(r"[0-9a-f]{40}", provenance.get("gitLfsPointerBlobSha1", "")):
            raise SourceLockError(f"Manifest is missing the Git LFS pointer blob identity for {sample.get('id')}.")
        if provenance.get("license") != "CC0-1.0":
            raise SourceLockError(f"Manifest CC0 license claim is not explicit for {sample.get('id')}.")
    readme_url = f"https://github.com/{repository}/blob/{revision}/{README_PATH}"
    license_url = f"https://github.com/{repository}/blob/{revision}/{LICENSE_PATH}"
    if any(sample["provenance"].get("readmeUrl") != readme_url or sample["provenance"].get("licenseUrl") != license_url for sample in upstream_samples):
        raise SourceLockError("Manifest README/LICENSE URLs do not resolve to the pinned commit paths.")
    if not validation.get("upstreamSourceLock"):
        raise SourceLockError("Manifest does not name the versioned upstream source lock.")
    return repository, revision, upstream_samples


def _parse_pointer(text: str, sample: dict[str, Any]) -> dict[str, Any]:
    lines = text.splitlines()
    if len(lines) != 3 or lines[0] != "version https://git-lfs.github.com/spec/v1":
        raise SourceLockError(f"Malformed Git LFS pointer for {sample['id']}.")
    oid_match = re.fullmatch(r"oid sha256:([0-9a-f]{64})", lines[1])
    size_match = re.fullmatch(r"size ([0-9]+)", lines[2])
    if not oid_match or not size_match:
        raise SourceLockError(f"Malformed Git LFS object declaration for {sample['id']}.")
    provenance = sample["provenance"]
    object_sha = oid_match.group(1).upper()
    object_size = int(size_match.group(1))
    pointer_bytes = text.encode("ascii")
    pointer_git_sha = _git_blob_sha1(pointer_bytes)
    if object_sha != sample["sha256"].upper() or object_size != sample["byteSize"]:
        raise SourceLockError(f"Git LFS pointer and manifest object identity disagree for {sample['id']}.")
    if pointer_git_sha != provenance["gitLfsPointerBlobSha1"].lower():
        raise SourceLockError(f"Git LFS pointer blob SHA-1 disagrees with the pinned commit for {sample['id']}.")
    return {
        "text": text,
        "gitBlobSha1": pointer_git_sha,
        "sha256": _sha256(pointer_bytes),
        "objectSha256": object_sha,
        "objectByteSize": object_size,
    }


def _source_record(raw_bytes: bytes, url: str, path: str, evidence_tokens: list[str], spdx_id: str | None = None) -> dict[str, Any]:
    text = raw_bytes.decode("utf-8")
    for token in evidence_tokens:
        if token.casefold() not in text.casefold():
            raise SourceLockError(f"Pinned {path} does not contain its required evidence marker: {token!r}.")
    record = {
        "path": path,
        "rawUrl": url,
        "gitBlobSha1": _git_blob_sha1(raw_bytes),
        "sha256": _sha256(raw_bytes),
        "byteSize": len(raw_bytes),
        "evidenceTokens": evidence_tokens,
    }
    if spdx_id:
        record["spdxId"] = spdx_id
    return record


def refresh_lock(root: Path, manifest: dict[str, Any], lock_path: Path) -> dict[str, Any]:
    repository, revision, samples = _required_provenance(manifest)
    locked_samples = []
    for sample in samples:
        source_path = sample["provenance"]["sourcePath"]
        raw_url = _raw_url(repository, revision, source_path)
        pointer_text = _fetch(raw_url).decode("ascii")
        locked_samples.append({
            "sampleId": sample["id"],
            "sourcePath": source_path,
            "rawUrl": raw_url,
            "gitLfsPointer": _parse_pointer(pointer_text, sample),
        })

    readme = _source_record(
        _fetch(_raw_url(repository, revision, README_PATH)),
        _raw_url(repository, revision, README_PATH), README_PATH, README_EVIDENCE,
    )
    license_record = _source_record(
        _fetch(_raw_url(repository, revision, LICENSE_PATH)),
        _raw_url(repository, revision, LICENSE_PATH), LICENSE_PATH, LICENSE_EVIDENCE, "CC0-1.0",
    )
    lock = {
        "schemaVersion": LOCK_SCHEMA,
        "profile": EXPECTED_PROFILE,
        "repository": repository,
        "revision": revision,
        "readme": readme,
        "license": license_record,
        "samples": locked_samples,
    }
    _validate_lock(lock, manifest)
    resolved_lock = lock_path.resolve()
    try:
        resolved_lock.relative_to(root.resolve())
    except ValueError as exc:
        raise SourceLockError("Source-lock output must stay inside the repository.") from exc
    resolved_lock.parent.mkdir(parents=True, exist_ok=True)
    resolved_lock.write_text(json.dumps(lock, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return lock


def _validate_lock(lock: dict[str, Any], manifest: dict[str, Any]) -> None:
    repository, revision, samples = _required_provenance(manifest)
    if lock.get("schemaVersion") != LOCK_SCHEMA or lock.get("profile") != EXPECTED_PROFILE:
        raise SourceLockError("Unexpected upstream source-lock schema/profile.")
    if lock.get("repository") != repository or lock.get("revision") != revision:
        raise SourceLockError("Upstream source lock does not match the manifest repository/commit.")
    readme = lock.get("readme", {})
    license_record = lock.get("license", {})
    for record, path, evidence_tokens in (
        (readme, README_PATH, README_EVIDENCE),
        (license_record, LICENSE_PATH, LICENSE_EVIDENCE),
    ):
        if record.get("path") != path or record.get("rawUrl") != _raw_url(repository, revision, path):
            raise SourceLockError(f"Upstream source lock does not pin {path} to the declared commit.")
        if not re.fullmatch(r"[0-9a-f]{40}", record.get("gitBlobSha1", "")) or not re.fullmatch(r"[0-9A-F]{64}", record.get("sha256", "")):
            raise SourceLockError(f"Upstream source lock has an invalid {path} content identity.")
        if not isinstance(record.get("byteSize"), int) or record["byteSize"] <= 0 or record.get("evidenceTokens") != evidence_tokens:
            raise SourceLockError(f"Upstream source lock is missing its {path} evidence rule.")
    if license_record.get("spdxId") != "CC0-1.0":
        raise SourceLockError("Upstream source lock does not identify the CC0-1.0 license.")

    lock_samples = {item.get("sampleId"): item for item in lock.get("samples", [])}
    expected_ids = {sample["id"] for sample in samples}
    if set(lock_samples) != expected_ids:
        raise SourceLockError("Upstream source lock must cover exactly the two pinned Nikon samples.")
    for sample in samples:
        entry = lock_samples[sample["id"]]
        source_path = sample["provenance"]["sourcePath"]
        if entry.get("sourcePath") != source_path or entry.get("rawUrl") != _raw_url(repository, revision, source_path):
            raise SourceLockError(f"Upstream source lock URL/path mismatch for {sample['id']}.")
        pointer = entry.get("gitLfsPointer", {})
        parsed = _parse_pointer(pointer.get("text", ""), sample)
        if pointer != parsed:
            raise SourceLockError(f"Stored Git LFS pointer identity is inconsistent for {sample['id']}.")


def verify_offline_lock(root: Path, manifest: dict[str, Any], lock_path: Path) -> dict[str, Any]:
    try:
        lock_bytes = lock_path.read_bytes()
    except OSError as exc:
        raise SourceLockError(f"Versioned upstream source lock is missing: {lock_path}.") from exc
    try:
        lock = json.loads(lock_bytes)
    except json.JSONDecodeError as exc:
        raise SourceLockError(f"Versioned upstream source lock is not valid JSON: {exc}.") from exc
    if not isinstance(lock, dict):
        raise SourceLockError("Versioned upstream source lock must contain a JSON object.")
    _validate_lock(lock, manifest)
    try:
        relative_lock = str(lock_path.resolve().relative_to(root.resolve())).replace("\\", "/")
    except ValueError as exc:
        raise SourceLockError("Versioned upstream source lock must remain inside the repository.") from exc
    return {
        "path": relative_lock,
        "sha256": _sha256(lock_bytes),
        "repository": lock["repository"],
        "revision": lock["revision"],
        "verifiedSampleIds": sorted(item["sampleId"] for item in lock["samples"]),
        "samples": {item["sampleId"]: item for item in lock["samples"]},
    }


def verify_cached_samples(root: Path, manifest: dict[str, Any]) -> list[dict[str, Any]]:
    results = []
    cache_root = (root / ".ai-tmp/cache/samples/P5-R4-v1").resolve()
    for sample in manifest.get("samples", []):
        relative_path = Path(sample["cachePath"])
        if relative_path.is_absolute() or ".." in relative_path.parts:
            raise SourceLockError(f"Unsafe R4 sample cache path for {sample.get('id')}.")
        path = (root / relative_path).resolve()
        try:
            path.relative_to(cache_root)
        except ValueError as exc:
            raise SourceLockError(f"R4 sample escaped the canonical P5-R4-v1 cache for {sample.get('id')}.") from exc
        if not path.is_file():
            raise SourceLockError(f"Hash-locked R4 sample cache file is missing: {sample['id']}.")
        data = path.read_bytes()
        digest = _sha256(data)
        if digest != sample["sha256"].upper() or len(data) != sample["byteSize"]:
            raise SourceLockError(f"Hash-locked R4 sample cache identity mismatch: {sample['id']}.")
        results.append({"sampleId": sample["id"], "sha256": digest, "byteSize": len(data)})
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", choices=("refresh", "verify"), required=True)
    parser.add_argument("--root", default=".")
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--lock")
    args = parser.parse_args()
    root = Path(args.root).resolve()
    try:
        manifest_path = (root / args.manifest).resolve()
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        configured_lock = manifest.get("validation", {}).get("upstreamSourceLock")
        if not configured_lock:
            raise SourceLockError("Manifest does not name the versioned upstream source lock.")
        lock_path = (root / (args.lock or configured_lock)).resolve()
        if args.mode == "refresh":
            refresh_lock(root, manifest, lock_path)
        lock_result = verify_offline_lock(root, manifest, lock_path)
        cache_result = verify_cached_samples(root, manifest) if args.mode == "verify" else []
        print(json.dumps({
            "status": "PASS",
            "mode": args.mode,
            "profile": EXPECTED_PROFILE,
            "lock": {key: value for key, value in lock_result.items() if key != "samples"},
            "verifiedCacheSamples": cache_result,
        }, ensure_ascii=False, indent=2))
        return 0
    except (SourceLockError, OSError, UnicodeError, ValueError, KeyError, json.JSONDecodeError) as exc:
        print(f"P5-R4 source-lock {args.mode} BLOCKED: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
