#!/usr/bin/env python3
"""Validate a Jellyfin SSO plugin ZIP before publishing or installing it."""
from __future__ import annotations

import json
import pathlib
import sys
import zipfile

GUID = "505ce9d1-d916-42fa-86ca-673ef241d7df"
TARGET_ABI = "10.11.0.0"
REQUIRED_DLLS = {
    "SSO-Auth.dll",
    "Duende.IdentityModel.dll",
    "Duende.IdentityModel.OidcClient.dll",
}
EXPECTED_FILES = REQUIRED_DLLS | {"meta.json"}


def verify_package(archive: pathlib.Path, expected_version: str) -> dict:
    """Fail closed if metadata or any release artifact is missing or unexpected."""
    if not expected_version or not expected_version.replace(".", "").isdigit():
        raise ValueError("Expected a numeric dotted plugin version")
    if not archive.name.endswith(".zip"):
        raise ValueError("Release artifact must be a ZIP")
    if expected_version not in archive.name:
        raise ValueError("Archive filename does not contain the expected version")

    with zipfile.ZipFile(archive) as bundle:
        names = bundle.namelist()
        if len(names) != len(set(names)) or set(names) != EXPECTED_FILES:
            raise ValueError(f"Unexpected ZIP contents: {names!r}")
        if any(info.is_dir() or info.file_size == 0 for info in bundle.infolist()):
            raise ValueError("Package contains a directory or empty file")
        if bundle.testzip() is not None:
            raise ValueError("Package contains a corrupted ZIP entry")

        metadata = json.loads(bundle.read("meta.json"))
        if not isinstance(metadata, dict):
            raise ValueError("Invalid plugin metadata")
        expected = {
            "version": expected_version,
            "guid": GUID,
            "targetAbi": TARGET_ABI,
            "name": "SSO Authentication",
            "owner": "nbenedek",
        }
        for key, value in expected.items():
            if metadata.get(key) != value:
                raise ValueError(f"Incorrect {key}: {metadata.get(key)!r}")
        description = metadata.get("description", "")
        changelog = metadata.get("changelog", "")
        if not isinstance(description, str) or not isinstance(changelog, str):
            raise ValueError("Missing or invalid release description/changelog")
        if "OpenID Connect" not in description:
            raise ValueError("Release description must identify OpenID Connect")
        if "github.com/nbenedek/jellyfin-plugin-sso" not in description:
            raise ValueError("Release description must link to the maintained fork")
        if "github.com/9p4/jellyfin-plugin-sso" in description:
            raise ValueError("Release description links to archived upstream")
        if expected_version not in changelog or len(changelog.strip().splitlines()) < 5:
            raise ValueError("Missing detailed changelog for release version")
        for dll in REQUIRED_DLLS:
            if not bundle.read(dll).startswith(b"MZ"):
                raise ValueError(f"Invalid managed assembly: {dll}")

    return metadata


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: verify_release_package.py PATH_TO_ZIP VERSION", file=sys.stderr)
        return 2
    try:
        meta = verify_package(pathlib.Path(sys.argv[1]), sys.argv[2])
    except (OSError, ValueError, zipfile.BadZipFile, json.JSONDecodeError) as exc:
        print(f"FAIL: {exc}", file=sys.stderr)
        return 1
    print(f"PASS: {meta['name']} {meta['version']} with complete Jellyfin package")
    return 0


if __name__ == "__main__":
    sys.exit(main())
