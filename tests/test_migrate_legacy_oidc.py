"""Regression tests for the offline legacy OIDC migration CLI."""
import importlib.util
import io
import json
from pathlib import Path
import tempfile
import unittest
from uuid import UUID
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("migrate_legacy_oidc", ROOT / "tools" / "migrate_legacy_oidc.py")
m = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(m)

ISSUER = "https://accounts.example/realms/applications"
PROVIDER = "testcorn"
GUID1 = "f1ea4312-9af6-4bd4-8796-e51849fe4ecf"
GUID2 = "828e48a5-7603-43fd-aa8c-addf761cc1e0"
SUB1 = "bc90474d-f6e9-448e-85a5-a12ad2892af6"
SUB2 = "77e74d1b-f6e9-448e-85a5-a12ad2892af6"


def fixture(legacy=None, modern=None, issuer=ISSUER):
    """The serialized XML dictionary shape used by SerializableDictionary."""
    root = ET.Element("PluginConfiguration")
    providers = ET.SubElement(root, "OidConfigs")
    item = ET.SubElement(providers, "item")
    ET.SubElement(ET.SubElement(item, "key"), "string").text = PROVIDER
    cfg = ET.SubElement(ET.SubElement(item, "value"), "PluginConfiguration")
    ET.SubElement(cfg, "OidEndpoint").text = issuer
    ET.SubElement(cfg, "Enabled").text = "true"
    for field, records, value_type in (
        ("CanonicalLinks", legacy or {}, "guid"),
        ("SubjectLinks", modern or {}, "guid"),
    ):
        parent = ET.SubElement(cfg, field)
        for key, value in records.items():
            entry = ET.SubElement(parent, "item")
            ET.SubElement(ET.SubElement(entry, "key"), "string").text = key
            ET.SubElement(ET.SubElement(entry, "value"), value_type).text = value
    return ET.tostring(root, encoding="utf-8")


def users(*entries):
    return json.dumps(list(entries)).encode()


def plan(xml, directory, filename="users.json", field="username"):
    return m.build_plan(xml, directory, filename, PROVIDER, ISSUER, field)


class MigrationTests(unittest.TestCase):
    def test_one_existing_user_migrates_without_changing_jellyfin_id(self):
        xml = fixture({"alice@example.com": GUID1})
        data = users({"username": "alice@example.com", "id": SUB1, "enabled": True})
        root, rows, approval = plan(xml, data)
        self.assertEqual(rows[0]["status"], "ready")
        self.assertEqual(64, len(approval))
        self.assertEqual(1, m.migrate(root, PROVIDER, rows))
        cfg = m.find_provider(root, PROVIDER)
        modern, _ = m.read_map(cfg, "SubjectLinks")
        legacy, _ = m.read_map(cfg, "CanonicalLinks")
        self.assertEqual(GUID1, modern[m.identity_key(ISSUER, SUB1)])
        self.assertNotIn("alice@example.com", legacy)
        names, _ = m.read_map(cfg, "SubjectNames")
        self.assertEqual("alice@example.com", names[m.identity_key(ISSUER, SUB1)])
        roundtrip = ET.tostring(root)
        _, rows_again, _ = plan(roundtrip, data)
        self.assertEqual([], rows_again)

    def test_unmatched_user_is_not_migrated(self):
        root, rows, _ = plan(fixture({"alice@example.com": GUID1}),
                             users({"username": "bob@example.com", "id": SUB2}))
        self.assertEqual("unmatched", rows[0]["status"])
        self.assertEqual(0, m.migrate(root, PROVIDER, rows))

    def test_duplicate_external_names_fail_closed(self):
        _, rows, _ = plan(fixture({"alice@example.com": GUID1}),
                          users({"username": "alice@example.com", "id": SUB1},
                                {"username": "alice@example.com", "id": SUB2}))
        self.assertEqual("blocked", rows[0]["status"])
        self.assertEqual("ambiguous_directory_name", rows[0]["reason"])

    def test_duplicate_external_subjects_fail_closed(self):
        _, rows, _ = plan(fixture({"alice@example.com": GUID1}),
                          users({"username": "alice@example.com", "id": SUB1},
                                {"username": "bob@example.com", "id": SUB1}))
        self.assertEqual("blocked", rows[0]["status"])
        self.assertEqual("duplicate_keycloak_subject", rows[0]["reason"])

    def test_existing_subject_owned_by_another_jellyfin_user_is_not_overwritten(self):
        key = m.identity_key(ISSUER, SUB1)
        xml = fixture({"alice@example.com": GUID1}, {key: GUID2})
        _, rows, _ = plan(xml, users({"username": "alice@example.com", "id": SUB1}))
        self.assertEqual("blocked", rows[0]["status"])
        self.assertEqual("oidc_identity_linked_to_another_account", rows[0]["reason"])

    def test_existing_subject_on_same_user_is_idempotent(self):
        key = m.identity_key(ISSUER, SUB1)
        xml = fixture({"alice@example.com": GUID1}, {key: GUID1})
        root, rows, _ = plan(xml, users({"username": "alice@example.com", "id": SUB1}))
        self.assertEqual("already", rows[0]["status"])
        self.assertEqual(0, m.migrate(root, PROVIDER, rows))

    def test_other_subject_already_on_account_requires_review(self):
        key = m.identity_key(ISSUER, SUB2)
        xml = fixture({"alice@example.com": GUID1}, {key: GUID1})
        _, rows, _ = plan(xml, users({"username": "alice@example.com", "id": SUB1}))
        self.assertEqual("blocked", rows[0]["status"])
        self.assertEqual("jellyfin_account_has_other_subject_links", rows[0]["reason"])

    def test_keycloak_disabled_user_is_never_migrated(self):
        _, rows, _ = plan(fixture({"alice@example.com": GUID1}),
                          users({"username": "alice@example.com", "id": SUB1, "enabled": False}))
        self.assertEqual("blocked", rows[0]["status"])

    def test_exact_issuer_is_required(self):
        with self.assertRaisesRegex(ValueError, "exactly equal"):
            plan(fixture({"alice@example.com": GUID1}, issuer=ISSUER + "/"),
                 users({"username": "alice@example.com", "id": SUB1}))

    def test_account_with_multiple_legacy_names_requires_review(self):
        xml = fixture({"alice": GUID1, "alice@example.com": GUID1})
        _, rows, _ = plan(xml, users({"username": "alice", "id": SUB1},
                                     {"username": "alice@example.com", "id": SUB2}))
        self.assertTrue(all(r["status"] == "blocked" for r in rows))

    def test_email_match_is_explicit_not_automatic(self):
        xml = fixture({"alice@example.com": GUID1})
        directory = users({"username": "alice", "email": "alice@example.com", "id": SUB1})
        self.assertEqual("unmatched", plan(xml, directory)[1][0]["status"])
        self.assertEqual("ready", plan(xml, directory, field="email")[1][0]["status"])

    def test_directory_csv_supported(self):
        data = b"id,username,email,enabled\n" + SUB1.encode() + b",alice@example.com,,true\n"
        _, rows, _ = plan(fixture({"alice@example.com": GUID1}), data, filename="users.csv")
        self.assertEqual("ready", rows[0]["status"])

    def test_unicode_identity_hash_uses_dotnet_utf16_length(self):
        import hashlib
        issuer = "https://example.test/\U0001f600"
        subject = "\U0001f600"
        raw = f"{len(issuer.encode('utf-16-le')) // 2}:{issuer}2:{subject}".encode()
        self.assertEqual("oidc-v1-" + hashlib.sha256(raw).hexdigest(),
                         m.identity_key(issuer, subject))

    def test_plan_hash_changes_when_export_changes(self):
        xml = fixture({"alice@example.com": GUID1})
        first = plan(xml, users({"username": "alice@example.com", "id": SUB1}))[2]
        second = plan(xml, users({"username": "alice@example.com", "id": SUB2}))[2]
        self.assertNotEqual(first, second)

    def test_apply_is_disabled_until_native_xml_validation(self):
        from contextlib import redirect_stderr
        stderr = io.StringIO()
        with redirect_stderr(stderr):
            result = m.main([
                "--users", "/nonexistent-export.json",
                "--provider", PROVIDER,
                "--issuer", ISSUER,
                "--apply",
                "--approve", "invalid"
            ])
        self.assertEqual(1, result)
        self.assertIn("Offline XML apply is disabled", stderr.getvalue())

    def test_atomic_write_keeps_backup_and_original_permissions(self):
        with tempfile.TemporaryDirectory() as dirname:
            root = Path(dirname)
            config = root / "SSO-Auth.xml"
            backup_dir = root / "backups"
            config.write_bytes(b"<original/>")
            config.chmod(0o600)
            saved = m.safe_write(config, b"<changed/>", b"<original/>", backup_dir)
            self.assertEqual(b"<changed/>", config.read_bytes())
            self.assertEqual(b"<original/>", saved.read_bytes())
            self.assertEqual(0o600, config.stat().st_mode & 0o777)
            self.assertEqual(0o600, saved.stat().st_mode & 0o777)

    def test_atomic_write_rejects_changed_configuration(self):
        with tempfile.TemporaryDirectory() as dirname:
            root = Path(dirname)
            config = root / "SSO-Auth.xml"
            config.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "changed"):
                m.safe_write(config, b"new", b"original", root / "backups")
            self.assertEqual(b"changed", config.read_bytes())


if __name__ == "__main__":
    unittest.main()
