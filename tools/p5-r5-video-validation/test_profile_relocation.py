"""Adversarial checks for exact frozen bytes despite administrative path migration."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest

DIRECTORY = Path(__file__).resolve().parent
ROOT = DIRECTORY.parents[1]
spec = importlib.util.spec_from_file_location("p5_r5_validation", DIRECTORY / "validate.py")
validator = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = validator
spec.loader.exec_module(validator)


class FrozenRelocationTests(unittest.TestCase):
    def setUp(self):
        self.erratum = json.loads((ROOT / validator.PATH_RELOCATION_ERRATUM).read_text(encoding="utf-8"))

    def test_both_current_files_reconstruct_exact_frozen_hashes(self):
        for row in self.erratum["files"]:
            with self.subTest(path=row["path"]):
                raw = (ROOT / row["path"]).read_bytes()
                original = validator.reconstruct_frozen_profile(raw, row, self.erratum)
                self.assertEqual(row["frozenSha256"], hashlib.sha256(original).hexdigest().upper())
                identity = validator.frozen_profile_identity(ROOT, ROOT / row["path"], row["frozenSha256"])
                self.assertEqual(row["currentSha256"], identity["actualSha256"])

    def test_full_acceptance_checks_still_require_all_routes_and_failures(self):
        manifest, *_ = validator.verify_active_manifest(ROOT, "P7-R4-HEVC-v2")
        self.assertEqual(11, len(manifest["requiredRoutes"]))
        self.assertEqual(5, len(manifest["requiredFailureCases"]))
        self.assertTrue(all(r["required"] and not r["skipAllowed"] for r in manifest["requiredRoutes"]))

    def test_tampered_depth_cannot_be_rebaselined_as_path_relocation(self):
        row = copy.deepcopy(self.erratum["files"][1])
        raw = (ROOT / row["path"]).read_bytes()
        changed = raw.replace(b'"expectedOutputDepth": 10', b'"expectedOutputDepth": 8')
        self.assertNotEqual(raw, changed)
        row["currentSha256"] = hashlib.sha256(changed).hexdigest().upper()
        with self.assertRaisesRegex(validator.ValidationError, "exact frozen bytes"):
            validator.reconstruct_frozen_profile(changed, row, self.erratum)

    def test_unknown_path_change_is_rejected(self):
        row = self.erratum["files"][0]
        raw = (ROOT / row["path"]).read_bytes().replace(b"source-probes.json", b"unapproved-source.json")
        with self.assertRaisesRegex(validator.ValidationError, "raw identity"):
            validator.reconstruct_frozen_profile(raw, row, self.erratum)

    def test_migrated_route_requires_explicit_minimal_libav_projection(self):
        validator.require_route_backend({"backend": "MinimalLibav"}, True, "migrated")
        for backend in ("Unknown", "WindowsMediaFoundation", None, "minimal-libav"):
            with self.subTest(backend=backend), self.assertRaises(validator.ValidationError):
                validator.require_route_backend({"backend": backend}, True, "migrated")

    def test_original_mf_route_still_requires_mf_projection(self):
        validator.require_route_backend({"backend": "WindowsMediaFoundation"}, False, "mf")
        with self.assertRaises(validator.ValidationError):
            validator.require_route_backend({"backend": "MinimalLibav"}, False, "mf")

    def test_negative_package_is_create_only_and_excludes_only_sidecar(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            build = root / "build"
            build.mkdir()
            for name in ("LivePhotoBox.Video.Libav.dll", "LivePhotoBox.Native.dll", "P5R5.ManagedSmoke.dll"):
                (build / name).write_bytes(name.encode())
            package = root / "negative"
            files = validator.create_missing_sidecar_package(build, package)
            self.assertEqual(2, len(files))
            self.assertFalse((package / "LivePhotoBox.Video.Libav.dll").exists())
            self.assertTrue((build / "LivePhotoBox.Video.Libav.dll").exists())
            for row in files:
                self.assertEqual((build / row["path"]).read_bytes(), (package / row["path"]).read_bytes())
            with self.assertRaises(FileExistsError):
                validator.create_missing_sidecar_package(build, package)

    def test_negative_package_requires_positive_sidecar_prerequisite(self):
        with tempfile.TemporaryDirectory() as temporary:
            with self.assertRaises(validator.ValidationError):
                validator.create_missing_sidecar_package(Path(temporary), Path(temporary) / "negative")


if __name__ == "__main__":
    unittest.main()
