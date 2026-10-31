"""Regression tests for the release-package verifier; no network required."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location(
    "verify_release_package", ROOT / "tools" / "verify_release_package.py"
)
verify = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verify)


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.archive = Path(self.temp.name) / "sso-authentication_4.1.0.0.zip"
        self.meta = {
            "version": "4.1.0.0",
            "guid": verify.GUID,
            "targetAbi": verify.TARGET_ABI,
            "name": "SSO Authentication",
            "owner": "nbenedek",
            "description": "OpenID Connect sign-in. Documentation at https://github.com/nbenedek/jellyfin-plugin-sso",
            "changelog": "4.1.0.0 — OIDC security release\\n- One-time transactions\\n- Issuer/subject links\\n- Admin only linking\\n- UI update",
        }

    def build(self, *, omit=None, extra=None, metadata=None):
        contents = {name: b"MZ" + b"x" * 32 for name in verify.REQUIRED_DLLS}
        contents["meta.json"] = json.dumps(
            self.meta if metadata is None else metadata
        ).encode()
        if omit:
            del contents[omit]
        if extra:
            contents[extra] = b"unexpected"
        with zipfile.ZipFile(self.archive, "w") as z:
            for name, content in contents.items():
                z.writestr(name, content)

    def test_valid_package(self):
        self.build()
        self.assertEqual("4.1.0.0", verify.verify_package(
            self.archive, "4.1.0.0"
        )["version"])

    def test_rejects_missing_metadata(self):
        self.build(omit="meta.json")
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")

    def test_rejects_unexpected_artifacts(self):
        self.build(extra="unwanted.dll")
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")

    def test_rejects_wrong_version(self):
        self.build(metadata={**self.meta, "version": "4.0.0.4"})
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")

    def test_rejects_wrong_plugin_guid(self):
        self.build(metadata={**self.meta, "guid": "wrong-guid"})
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")

    def test_rejects_missing_assembly(self):
        self.build(omit="Duende.IdentityModel.dll")
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")

    def test_rejects_missing_changelog(self):
        self.build(metadata={**self.meta, "changelog": ""})
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")

    def test_rejects_old_description(self):
        self.build(metadata={**self.meta, "description": "See https://github.com/9p4/jellyfin-plugin-sso"})
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")

    def test_rejects_duplicate_entries(self):
        self.build()
        with zipfile.ZipFile(self.archive, "a") as z:
            z.writestr("meta.json", json.dumps(self.meta))
        with self.assertRaises(ValueError):
            verify.verify_package(self.archive, "4.1.0.0")


if __name__ == "__main__":
    unittest.main()
