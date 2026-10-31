#!/usr/bin/env python3
"""Offline, administrator-approved legacy SSO to OIDC subject-link migration.

Requires Python 3.11+. Uses only the standard library and does not access the
Keycloak or Jellyfin APIs. DRY RUN is the default; never start Jellyfin while
editing its configuration file.
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import io
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
from collections import Counter, defaultdict
from datetime import datetime, timezone
from urllib.parse import urlsplit
from uuid import UUID
import xml.etree.ElementTree as ET


def tag(node: ET.Element) -> str:
    return node.tag.rsplit("}", 1)[-1]


def direct(node: ET.Element, name: str) -> ET.Element | None:
    return next((child for child in node if tag(child) == name), None)


def required_child(node: ET.Element, name: str) -> ET.Element:
    found = direct(node, name)
    if found is None:
        raise ValueError(f"Missing XML element: {name}")
    return found


def leaf(node: ET.Element) -> str:
    leaves = [x.text.strip() for x in node.iter() if len(x) == 0 and x.text and x.text.strip()]
    if len(leaves) != 1:
        raise ValueError("Expected exactly one value in XML dictionary entry")
    return leaves[0]


def read_map(cfg: ET.Element, name: str) -> tuple[dict[str, str], dict[str, ET.Element]]:
    parent = direct(cfg, name)
    values: dict[str, str] = {}
    nodes: dict[str, ET.Element] = {}
    if parent is None:
        return values, nodes
    for item in parent:
        if tag(item) != "item":
            raise ValueError(f"Unexpected XML node in {name}")
        key = leaf(required_child(item, "key"))
        if key in values:
            raise ValueError(f"Duplicate XML dictionary key in {name}")
        values[key] = leaf(required_child(item, "value"))
        nodes[key] = item
    return values, nodes


def find_provider(root: ET.Element, provider: str) -> ET.Element:
    configs = next((n for n in root.iter() if tag(n) == "OidConfigs"), None)
    if configs is None:
        raise ValueError("SSO OidConfigs not found in configuration")
    matches = []
    for item in configs:
        if tag(item) != "item" or leaf(required_child(item, "key")) != provider:
            continue
        value = required_child(item, "value")
        candidates = [node for node in value if direct(node, "OidEndpoint") is not None]
        if len(candidates) != 1:
            raise ValueError("Expected a single OIDC provider configuration value")
        matches.extend(candidates)
    if len(matches) != 1:
        raise ValueError(f"Expected exactly one OIDC provider named {provider}")
    return matches[0]


def issuer_valid(issuer: str) -> bool:
    try:
        parsed = urlsplit(issuer)
        return (parsed.scheme == "https" and bool(parsed.hostname) and
                not parsed.username and not parsed.password and
                not parsed.query and not parsed.fragment)
    except ValueError:
        return False


def identity_key(issuer: str, subject: str) -> str:
    # .NET string.Length counts UTF-16 code units, not Unicode code points.
    length = lambda s: len(s.encode("utf-16-le")) // 2
    data = f"{length(issuer)}:{issuer}{length(subject)}:{subject}".encode("utf-8")
    return "oidc-v1-" + hashlib.sha256(data).hexdigest()


def load_directory(data: bytes, filename: str) -> list[dict]:
    if filename.lower().endswith(".csv"):
        rows = list(csv.DictReader(io.StringIO(data.decode("utf-8-sig"))))
    else:
        contents = json.loads(data.decode("utf-8-sig"))
        rows = contents.get("users") if isinstance(contents, dict) else contents
    if not isinstance(rows, list) or any(not isinstance(x, dict) for x in rows):
        raise ValueError("Export must be a JSON user array, realm JSON with users, or CSV")
    if not rows:
        raise ValueError("User export is empty; refusing to treat it as authoritative")
    return rows


def canonical_uuid(value: str) -> str | None:
    if not isinstance(value, str) or not re.fullmatch(
        r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", value
    ):
        return None
    try:
        parsed = UUID(value)
        return str(parsed) if parsed.int else None
    except ValueError:
        return None


def build_plan(config_data: bytes, directory_data: bytes, filename: str,
               provider: str, issuer: str, match_field: str) -> tuple[ET.Element, list[dict], str]:
    if not issuer_valid(issuer):
        raise ValueError("OIDC issuer must be a valid HTTPS URL without userinfo/query/fragment")
    root = ET.fromstring(config_data)
    cfg = find_provider(root, provider)
    endpoint = required_child(cfg, "OidEndpoint").text
    if (endpoint or "").strip() != issuer:
        raise ValueError("Supplied issuer must exactly equal provider OidEndpoint")
    enabled = direct(cfg, "Enabled")
    if enabled is None or (enabled.text or "").strip().lower() != "true":
        raise ValueError("OIDC provider is not enabled")

    legacy, _ = read_map(cfg, "CanonicalLinks")
    modern, _ = read_map(cfg, "SubjectLinks")
    directory = load_directory(directory_data, filename)

    by_name: dict[str, list[dict]] = defaultdict(list)
    by_subject: Counter[str] = Counter()
    for entry in directory:
        name, subject = entry.get(match_field), entry.get("id")
        if isinstance(name, str) and name.strip() and isinstance(subject, str):
            by_name[name].append(entry)
            by_subject[subject] += 1

    legacy_owners = Counter(legacy.values())
    modern_owners = defaultdict(set)
    for key, user_id in modern.items():
        normalized = canonical_uuid(user_id)
        if normalized is None:
            raise ValueError("Existing SubjectLinks contains an invalid Jellyfin GUID")
        modern_owners[normalized].add(key)

    rows: list[dict] = []
    for username, raw_guid in sorted(legacy.items()):
        guid = canonical_uuid(raw_guid)
        row = {"username": username, "jellyfin_user_id": guid or raw_guid,
               "status": "", "reason": "", "oidc_subject": None, "identity_key": None}
        candidates = by_name.get(username, [])
        if guid is None:
            row.update(status="blocked", reason="invalid_legacy_jellyfin_guid")
        elif legacy_owners[raw_guid] > 1:
            row.update(status="blocked", reason="multiple_legacy_names_share_jellyfin_account")
        elif not candidates:
            row.update(status="unmatched", reason="no_exact_directory_match")
        elif len(candidates) != 1:
            row.update(status="blocked", reason="ambiguous_directory_name")
        else:
            entry = candidates[0]
            subject = entry.get("id")
            if not canonical_uuid(subject):
                row.update(status="blocked", reason="invalid_keycloak_subject_id")
            elif by_subject[subject] != 1:
                row.update(status="blocked", reason="duplicate_keycloak_subject")
            elif entry.get("enabled") in (False, "false", "False", "0", 0):
                row.update(status="blocked", reason="keycloak_user_disabled")
            else:
                key = identity_key(issuer, subject)
                row["oidc_subject"], row["identity_key"] = subject, key
                if key in modern and canonical_uuid(modern[key]) != guid:
                    row.update(status="blocked", reason="oidc_identity_linked_to_another_account")
                elif key in modern:
                    row.update(status="already", reason="existing_subject_link_matches")
                elif modern_owners[guid]:
                    row.update(status="blocked", reason="jellyfin_account_has_other_subject_links")
                else:
                    row.update(status="ready", reason="unique_exact_legacy_and_directory_match")
        rows.append(row)

    config_hash = hashlib.sha256(config_data).hexdigest()
    directory_hash = hashlib.sha256(directory_data).hexdigest()
    approval_input = {
        "provider": provider, "issuer": issuer, "match_field": match_field,
        "config_sha256": config_hash, "directory_sha256": directory_hash,
        "rows": rows,
    }
    approval = hashlib.sha256(json.dumps(
        approval_input, sort_keys=True, separators=(",", ":")
    ).encode("utf-8")).hexdigest()
    return root, rows, approval


def add_map_entry(cfg: ET.Element, field: str, key: str, value: str, value_type: str) -> None:
    parent = direct(cfg, field)
    if parent is None:
        parent = ET.SubElement(cfg, field)
    existing, _ = read_map(cfg, field)
    if key in existing:
        raise ValueError("Duplicate subject mapping; configuration may have changed")
    item = ET.SubElement(parent, "item")
    key_wrapper = ET.SubElement(item, "key")
    ET.SubElement(key_wrapper, "string").text = key
    value_wrapper = ET.SubElement(item, "value")
    ET.SubElement(value_wrapper, value_type).text = value


def migrate(root: ET.Element, provider: str, rows: list[dict]) -> int:
    cfg = find_provider(root, provider)
    _, legacy_nodes = read_map(cfg, "CanonicalLinks")
    count = 0
    for row in rows:
        if row["status"] != "ready":
            continue
        user, subject_key, username = row["jellyfin_user_id"], row["identity_key"], row["username"]
        if username not in legacy_nodes:
            raise ValueError("Legacy mapping disappeared while preparing migration")
        add_map_entry(cfg, "SubjectLinks", subject_key, user, "guid")
        add_map_entry(cfg, "SubjectNames", subject_key, username, "string")
        required_child(cfg, "CanonicalLinks").remove(legacy_nodes[username])
        count += 1
    return count


def safe_write(path: Path, new_data: bytes, original: bytes, backup_dir: Path) -> Path:
    if path.is_symlink() or not path.is_file():
        raise ValueError("Configuration must be a regular non-symlink file")
    if path.read_bytes() != original:
        raise ValueError("Configuration changed since planning; no changes made")
    original_stat = path.stat()
    backup_dir.mkdir(parents=True, exist_ok=True, mode=0o700)
    if backup_dir.is_symlink():
        raise ValueError("Backup directory may not be a symlink")
    os.chmod(backup_dir, 0o700)
    name = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S-%f")
    backup = backup_dir / (path.name + "." + name + ".bak")
    fd = os.open(backup, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "wb") as output:
        output.write(original)
        output.flush()
        os.fsync(output.fileno())
    tmp_name = None
    try:
        fd, tmp_name = tempfile.mkstemp(prefix=".sso-migration-", dir=path.parent)
        with os.fdopen(fd, "wb") as output:
            os.fchmod(output.fileno(), stat.S_IMODE(original_stat.st_mode))
            if os.geteuid() == 0:
                os.fchown(output.fileno(), original_stat.st_uid, original_stat.st_gid)
            output.write(new_data)
            output.flush()
            os.fsync(output.fileno())
        if path.read_bytes() != original:
            raise ValueError("Configuration changed during backup; no changes made")
        os.replace(tmp_name, path)
        tmp_name = None
        directory_fd = os.open(path.parent, os.O_RDONLY)
        try:
            os.fsync(directory_fd)
        finally:
            os.close(directory_fd)
        return backup
    finally:
        if tmp_name is not None:
            os.unlink(tmp_name)


def print_plan(rows: list[dict], approval: str) -> None:
    counts = Counter(x["status"] for x in rows)
    print("Legacy SSO → immutable OIDC identity migration")
    print("PLAN: dry run unless --apply is explicitly approved")
    print("  ".join(f"{name}: {counts[name]}" for name in ("ready", "already", "unmatched", "blocked")))
    for row in rows[:80]:
        print(f"  {row['status']:9} {row['username']:<35} {row['reason']}")
    if len(rows) > 80:
        print(f"  ... {len(rows) - 80} additional rows (use --report for full details)")
    print("APPROVAL_HASH=" + approval)


def write_report(path: Path, payload: dict) -> None:
    if path.exists() and path.is_symlink():
        raise ValueError("Report cannot overwrite a symlink")
    path.parent.mkdir(parents=True, exist_ok=True)
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as output:
        os.fchmod(output.fileno(), 0o600)
        json.dump(payload, output, indent=2)
        output.write("\n")


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config", type=Path, default=Path("/var/lib/jellyfin/plugins/configurations/SSO-Auth.xml"))
    parser.add_argument("--users", type=Path, required=True, help="Keycloak JSON user array, realm JSON with users, or CSV")
    parser.add_argument("--provider", required=True)
    parser.add_argument("--issuer", required=True, help="Exact OIDC issuer/authority URL")
    parser.add_argument("--match-field", choices=("username", "email"), default="username")
    parser.add_argument("--report", type=Path, help="Optional detailed JSON dry-run report (mode 0600)")
    parser.add_argument("--apply", action="store_true", help="Write changes only after explicit plan approval")
    parser.add_argument("--approve", help="Approval hash printed by an identical dry run")
    parser.add_argument("--allow-partial", action="store_true", help="Explicitly permit unmatched/blocked rows to remain unchanged")
    parser.add_argument("--backup-dir", type=Path, default=Path("/root/sso-migration-backups"))
    return parser.parse_args(argv)


def main(argv=None) -> int:
    args = parse_args(argv)
    try:
        if args.apply:
            raise ValueError(
                "Offline XML apply is disabled pending validation against "
                "Jellyfin's native configuration deserializer. "
                "Do not edit SSO-Auth.xml with this tool."
            )
        if args.config.is_symlink() or args.users.is_symlink():
            raise ValueError("Configuration and export must not be symlinks")
        config_data, directory_data = args.config.read_bytes(), args.users.read_bytes()
        root, rows, approval = build_plan(config_data, directory_data, args.users.name,
                                          args.provider, args.issuer, args.match_field)
        print_plan(rows, approval)
        if args.report:
            write_report(args.report, {
                "provider": args.provider, "issuer": args.issuer,
                "match_field": args.match_field, "approval_hash": approval, "rows": rows,
            })
            print("Report: " + str(args.report))
        if not args.apply:
            return 0
        if os.geteuid() != 0:
            raise ValueError("Apply requires root privileges")
        if args.approve != approval:
            raise ValueError("Approval hash missing/mismatched; rerun dry run and review the plan")
        if any(row["status"] in ("unmatched", "blocked") for row in rows) and not args.allow_partial:
            raise ValueError("Unresolved legacy entries; review report and pass --allow-partial explicitly")
        if subprocess.run(["systemctl", "is-active", "--quiet", "jellyfin"],
                          check=False).returncode == 0:
            raise ValueError("Stop jellyfin.service before applying an offline migration")
        ready = sum(row["status"] == "ready" for row in rows)
        if not ready:
            print("No migrations required; configuration unchanged")
            return 0
        count = migrate(root, args.provider, rows)
        if count != ready:
            raise ValueError("Internal plan mismatch; no changes made")
        output = io.BytesIO()
        ET.ElementTree(root).write(output, encoding="utf-8", xml_declaration=True)
        output_bytes = output.getvalue()
        # Verify the serialized data contains every planned subject mapping.
        verify_cfg = find_provider(ET.fromstring(output_bytes), args.provider)
        mappings, _ = read_map(verify_cfg, "SubjectLinks")
        for row in rows:
            if row["status"] == "ready" and canonical_uuid(mappings[row["identity_key"]]) != row["jellyfin_user_id"]:
                raise ValueError("Serialized identity mapping verification failed")
        backup = safe_write(args.config, output_bytes, config_data, args.backup_dir)
        print(f"APPLIED={count} BACKUP={backup}")
        print("Jellyfin is still stopped; start it, then verify existing account logins.")
        return 0
    except (ValueError, OSError, ET.ParseError, json.JSONDecodeError, UnicodeError, csv.Error) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
