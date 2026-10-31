# Keycloak-only offline migration of legacy Jellyfin SSO accounts

**Safety note:** The former Python XML writer was retired after a settings-loss
incident. Its implementation is no longer included in this repository. Only
the native .NET migration CLI described below is supported.

The replacement is a standalone **Keycloak-specific .NET 9 CLI**:
`jellyfin-sso-keycloak-migrate`. It imports the Keycloak Admin API's minimal
user JSON export and maps identities into the plugin's standard OIDC
issuer/subject links. **It is not a generic OIDC migration CLI.** Other
identity providers must supply their own verified export adapters; do not
reuse this utility with their directory exports. The CLI cannot independently
prove that a file came from Keycloak; verify the export and realm yourself.
It does not add anything to the Jellyfin UI or require a persistent Keycloak
Admin API connection.

## Safety design

- Loads SSO-Auth.xml with `XmlSerializer(typeof(PluginConfiguration))`, the
  same serializable configuration model used by Jellyfin, including custom
  `SerializableDictionary` and OIDC provider settings.
- Before planning or writing, requires a full **native .NET round-trip** of all
  recognized settings. Unknown XML fields and unsafe XML declarations fail
  closed rather than being silently discarded.
- Migrations use the existing `OidcIdentityKey.Create` and
  `OidcIdentityLinks.TryLink` implementation, not a separately reimplemented
  hashing or XML algorithm.
- Never creates Jellyfin users, overwrites a subject's existing owner, changes
  permissions, or silently matches unmatched identities.
- Exact, case-sensitive matching only; email is a separate opt-in match field.
- Requires a review hash tied to the **exact configuration and directory
  export bytes**. If either changes, the previous approval is invalid.
- Requires Jellyfin stopped and root for apply; creates a private timestamped
  backup, verifies the serialized result, preserves ownership and permissions,
  then atomically replaces the configuration file.
- Does not verify that the target Jellyfin GUID exists in the Jellyfin user
  database; **review that separately** before migration.

## Install on a development Jellyfin instance

The standalone tool is in `SSO-Auth.Migration`. It is built and tested by
GitHub Actions on every push alongside the plugin. Install it separately from
the SSO plugin: **do not replace the working SSO-Auth.dll**.

On development Jellyfin instance, with .NET 9 SDK available, publish:

```bash
dotnet publish SSO-Auth.Migration/SSO-Auth.Migration.csproj -c Release -o /opt/jellyfin-sso-keycloak-migration
```

Run it with:

```bash
dotnet /opt/jellyfin-sso-keycloak-migration/Jellyfin.SSO.KeycloakMigration.dll --help
```

Install the provided launcher so the command name itself is unambiguous:

```bash
install -o root -g root -m 0755 tools/jellyfin-sso-keycloak-migrate /usr/local/sbin/jellyfin-sso-keycloak-migrate
jellyfin-sso-keycloak-migrate --help
```

Retire any previously installed legacy Python launcher
(`/usr/local/sbin/jellyfin-sso-migrate`) after confirming that the .NET
Keycloak-specific command works. Do not overwrite the working Jellyfin plugin.

## Export users from Keycloak

Use a **minimal directory export** as previously gathered:

```json
[
  {
    "id": "11111111-1111-4111-8111-111111111111",
    "username": "alice@example.com",
    "email": "alice@example.com",
    "enabled": true
  }
]
```

Only JSON user arrays (or a JSON realm object containing `users`) are
supported by the replacement. Never transfer passwords, client secrets, or
a full realm export when a minimal user list will suffice. Keep the export
and reports mode 0600 and delete when no longer needed.

## Native dry run

```bash
jellyfin-sso-keycloak-migrate \
  --users /root/keycloak-users.json \
  --provider example-oidc \
  --issuer https://idp.example.org/realms/media \
  --report /root/sso-migration-native-preview.json
```

This checks whether the **recovered, real** plugin configuration can load and
re-serialize with no lost settings, then prints the proposed mappings. It
does not stop Jellyfin or modify the configuration.

Each legacy mapping is classified as:

- `ready`: one exact enabled directory match, no conflicting subject link.
- `already`: the same identity is already linked to the expected Jellyfin ID.
- `unmatched`: no exact directory match; no migration.
- `blocked`: disabled user, duplicate username or subject, duplicate legacy
  ownership, or competing subject ownership.

The report includes personal account identifiers. Inspect each proposed match
against a trusted Keycloak directory and the account it should own. **A
matching username is not by itself proof of ownership.** Only an
administrator can approve the migration.

## Apply (only after validating the new dry run)

For the first live trial, migrate **one reviewed legacy-only test
identity**, with `--only-user 'EXACT_LEGACY_USERNAME'` added to the new dry
run. The resulting plan contains exactly that user, and the approval hash is
bound to that one-user selection. Review the result and copy the **new**
`APPROVAL_HASH`, not the old Python hash or the unfiltered .NET preview hash.
A full batch must not be the first live test of the replacement.

Stop Jellyfin before using the same flags with `--apply`.

```bash
systemctl stop jellyfin
jellyfin-sso-keycloak-migrate \
  --users /root/keycloak-users.json \
  --provider example-oidc \
  --issuer https://idp.example.org/realms/media \
  --only-user 'EXACT_LEGACY_USERNAME' \
  --apply --approve ONE_USER_NATIVE_APPROVAL_HASH
systemctl start jellyfin
```

To preview a single account, add `--only-user 'EXACT_LEGACY_USERNAME'` to
the dry-run command as well, and verify that it reports exactly one `ready`
mapping. Do not change options between dry run and apply.

If any selected entries are blocked or unmatched, the CLI also requires
explicit `--allow-partial`. It will leave those entries unchanged.

The CLI prints `APPLIED=N BACKUP=/root/sso-migration-backups/...`.
The backup contains secrets; keep it private.

After starting Jellyfin, verify the SSO settings, native administrator recovery
login, successful Keycloak login to the original Jellyfin user ID, authorization,
and repeat login after restart. If any settings are missing, immediately stop
Jellyfin and restore the migration backup before changing settings in the UI.

**Production promotion is prohibited until this validation passes on
development Jellyfin instance.**
