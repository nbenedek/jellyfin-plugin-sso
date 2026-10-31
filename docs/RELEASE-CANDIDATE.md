# Jellyfin SSO 4.1.0.0 — release candidate

**Status:** release candidate passed development live acceptance on 2026-10-31;
production promotion remains gated. This is an OIDC-only fork targeting Jellyfin 10.11.x
and .NET 9. No automatic production installation or repository publication
occurs from development-branch builds. Legacy nightly publication now requires
an explicit manual workflow invocation; a production release requires an
explicit GitHub Release publication.

## What changed

- One-time provider-bound authorization transactions and replay-resistant token
  exchange, validated issuer/subject identity links and administrator-only
  account linking
- Restart-safe permission persistence and an improved administrator settings UI
- Extracted claim interpretation and shared OIDC client creation
- Server-side preservation of all identity-link dictionaries during provider
  configuration saves; clients cannot inject mappings through provider creation
- Malformed nested role claims fail closed
- Unsafe Python XML migration tooling removed; the Keycloak-specific offline
  .NET migration utility remains available separately

## Build and artifact verification

The **Release Candidate Package** GitHub Actions workflow builds and tests
the development branch, packages using the Jellyfin plugin repository manager,
checks that the ZIP contains exactly the expected assemblies and `meta.json`,
verifies the package metadata, then verifies SHA-256 and uploads a ZIP and
`SHA256SUMS` as a build artifact. It
does not publish a GitHub Release or deploy to Jellyfin. The assembly and
`build.yaml` versions are both `4.1.0.0`. Before unpacking or distributing the
artifact, run:

```bash
sha256sum -c SHA256SUMS
```

Keep the original ZIP and the recorded commit SHA for traceability. An artifact
is not a production release.

## Development installation

Work on a nonproduction Jellyfin server. Record the artifact's commit SHA.
Before replacing any files, locate the _actual_ plugin directory and config
path. Do not assume the configuration belongs inside the plugin directory.

```bash
find /var/lib/jellyfin/plugins -type f -name 'SSO-Auth.dll' -print
find /var/lib/jellyfin /etc/jellyfin -type f -name 'SSO-Auth.xml' -print 2>/dev/null
```

Make a root-only backup of the **entire existing plugin directory** and
the XML configuration. The JPRM ZIP contains `SSO-Auth.dll`,
`Duende.IdentityModel.OidcClient.dll`, `Duende.IdentityModel.dll` and
**`meta.json`** (not `manifest.json`). Stop Jellyfin and install all four
files together into a plugin directory named for the new version, retaining
the plugin GUID. Remove or move aside the old versioned plugin directory to
avoid loading two copies; preserve the separate `SSO-Auth.xml` configuration.
Then start Jellyfin and confirm that the plugin manager reports 4.1.0.0.
Keep the native administrator account available for recovery. Never edit
identity dictionaries by hand or restore an XML file over live Jellyfin
configuration while the service is running.

## Automated smoke tests (read-only)

For a development endpoint with an enabled test provider:

```bash
bash tools/stage8-smoke.sh https://jellyfin.example.org provider-name
```

The test intentionally sends invalid state and exchange tokens and checks
that public and administrator routes are properly restricted. It does not
invoke any setting mutations or log in with credentials.

## Required live acceptance before promotion

- Keycloak/other enabled OIDC login and repeat login preserve the exact
  existing Jellyfin user identity; both migrated accounts remain linked
- A display username rename does not create or reassign a Jellyfin account
- Native Jellyfin administrator login continues to work
- Restart and provider save preserve `SubjectLinks`, `SubjectNames`,
  `CanonicalLinks` and manually configured library access when SSO-managed
  authorization is disabled
- A provider-save request without link dictionaries cannot erase existing
  links; a new provider cannot preload identity mappings
- Disabled or unknown providers, expired/replayed exchange tokens and
  self-service linking are rejected
- Non-admin access to provider configuration and link-management endpoints is
  forbidden; publicly reachable legacy linking page remains absent
- Browser and Android login flows work; no callback overlays or auth loops
- Development: no remaining legacy accounts (operator confirmed 2026-10-31)
- Production: review any remaining unmigrated legacy accounts before promotion

## Rollback and release gate

Back up plugin files and XML first. On failure stop Jellyfin, restore the
previous plugin directory and original XML from the same backup set, restart
and verify native administrator login, mapped SSO accounts, and library access.
Do not overwrite active config files with the service running.

Production promotion is **not** automated. It requires a human-verified
development acceptance report for the exact artifact commit, a migration
inventory, a clean CI run, a reviewed security checklist, and an approved
maintenance window with the rollback backup already taken.

See [SECURITY-AUDIT.md](SECURITY-AUDIT.md) for findings and residual risks.

## Development validation record

The operator confirmed completion of the full manual acceptance matrix against
4.1.0.0, based on commit `ce6dc4e89d8439c63c4964f0bb8f29512c79170a`.
The Stage 8 unauthenticated smoke tests passed 8/8 after the server became
ready; Keycloak and local administrator login, both migrated identities,
manual library access, provider saves, restart persistence and web/Android
flows were confirmed. No further invalid/missing authentication-provider
warnings appeared after deployment. This does not constitute production
acceptance or a completed independent penetration test.

## Packaging notes

The package is intended for a Jellyfin plugin-directory installation, not a
`.NET` publish output. Its `meta.json` is the authoritative installed-plugin
metadata; updating only the DLL leaves Jellyfin displaying the previous version.
The ZIP must contain exactly these four files: `SSO-Auth.dll`,
`Duende.IdentityModel.dll`, `Duende.IdentityModel.OidcClient.dll`, and
`meta.json`. The repository's `tools/verify_release_package.py` checks
version, target ABI, plugin GUID, package layout and nonempty assemblies.

Do not install an additional copy alongside a different version; move the
previous version directory to a private backup location first. The plugin
configuration at `plugins/configurations/SSO-Auth.xml` is not packaged and must
not be replaced with generic defaults. After successful installation, verify
both the reported plugin version and the authentication flow before promotion.
