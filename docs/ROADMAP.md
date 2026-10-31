# SSO Authentication development roadmap

This file tracks the `dev/oidc-security-refactor` branch. Jellyfin 10.11.6 is
the primary development target. Stage acceptance requires both automated
regression tests and live validation on development Jellyfin instance.

| Stage | Scope                                                                             | Status                                                       |
| ----- | --------------------------------------------------------------------------------- | ------------------------------------------------------------ |
| 1     | OIDC-only baseline; remove SAML and obsolete public assets                        | Complete                                                     |
| 2     | One-time, expiring, provider-bound OIDC login transactions                        | Complete                                                     |
| 3     | Stable issuer/subject identity; administrator-controlled account linking          | Complete, manually tested                                    |
| 4     | API authorization, method semantics, provider security, and negative-access tests | Complete, manually tested                                  |
| 5     | Jellyfin 10.11 authorization and restart persistence                              | Complete, manually tested                                    |
| 6     | Administrator interface modernization and navigation                              | Complete, manually tested (8/8 live checks passed)           |
| 7     | Legacy code cleanup and dependency reduction                                      | In progress; inventory and regression-first refactor         |
| 8     | End-to-end security audit, release packaging, versioning, dev-to-prod promotion   | Planned                                                      |

## Stage 4 acceptance

- Public OIDC challenge, callback, exchange and enabled-provider discovery remain operational.
- All configuration, provider deletion, authentication-provider switching and identity-link management endpoints require Jellyfin's elevated administrator policy.
- Provider deletion uses DELETE, never GET.
- Only enabled OIDC provider names are exposed from public discovery.
- Malformed or absent exchange credentials and unknown providers fail cleanly.
- HTTPS discovery and issuer-name validation cannot be bypassed by deprecated settings.
- Log messages never dump OIDC claim values for rejected sign-ins.
- Unit tests and Release build pass; run live unauthenticated and ordinary-user negative tests before declaring the stage complete.

## Stage 6: Dashboard administration

**Requirement:** An administrator must be able to open SSO Authentication
settings directly from the **Dashboard left navigation panel**, without
visiting Plugins → SSO Authentication → Settings.

Implementation acceptance:

- A clearly named SSO Authentication entry is discoverable in the Dashboard sidebar.
- Only administrators can access its settings, including the identity-linking UI.
- Retain or redirect the existing Plugins → Settings entry for compatibility.
- Preserve provider-neutral terminology and the native administrator recovery login.
- Simplify OIDC configuration and improve validation; no manual JSON editing.
- Prioritize the external SSO sign-in button without removing native Jellyfin login.

Implementation prepared for Jellyfin Web 10.11.6:

- Configuration page advertises `EnableInMainMenu = true` and displays
  "SSO Authentication" in the Dashboard's Plugins sidebar group.
- The existing Plugins → Settings page continues using the same `Name = SSO-Auth`.
- Provider form has explicit new/load actions, HTTPS issuer/port validation, a
  masked secret field, an advanced options disclosure, and resettable state.
- Saving fetches the current provider and preserves `SubjectLinks`,
  `SubjectNames`, and `CanonicalLinks` rather than rebuilding link objects.
- Administrator identity linking is collapsed by default, but its server-side
  elevation requirement is unchanged.

**Live acceptance on development Jellyfin instance passed (2026-10-31)**: dashboard sidebar and
legacy Settings navigation work; non-administrator configuration and identity-
linking access is denied; provider switching resets the editor; save and restart
preserve issuer/subject mappings, both migrated Keycloak users, and library
permissions; both Keycloak and native administrator login work. All 8/8 reported
acceptance checks passed. Production promotion remains gated on Stage 7/8.
The login-screen SSO button's actual markup still needs verification before
automatically injecting visual styling.

## Keycloak legacy-account migration — Keycloak-only CLI, not an admin UI feature

- [x] Standalone .NET offline CLI to import Keycloak user JSON and preview migrations.
- [x] Replace unsafe Python XML writer with a separate .NET 9 CLI using actual plugin configuration types.
- [x] Native XmlSerializer preservation tests, collisions, approval hash and private backups in CI.
- [x] Restore development Jellyfin instance settings after the Python XML rewrite (confirmed by user).
- [x] Run native .NET dry run against **recovered actual** development Jellyfin instance configuration (user confirmed).
- [x] Migrate one reviewed legacy account and verify functionality on development Jellyfin instance (user confirmed).
- [x] Complete the two legacy Keycloak account migrations on development Jellyfin instance (user confirmed).
- [x] Verify migrated users remain linked after an SSO settings edit and Jellyfin restart (Stage 6 live acceptance, 2026-10-31).
- [ ] Review any remaining unmigrated users before production promotion.

The Jellyfin Dashboard remains uncluttered: Stage 6 concerns regular SSO
settings and navigation only. Bulk legacy migration remains a CLI operation.
The Keycloak-specific directory JSON import adapter is not suitable for other
OIDC providers, although the resulting issuer/subject links are provider-neutral.

## Stage 7 cleanup checkpoint

Stage 6 accepted on development Jellyfin instance; preserve the 2026-10-31 development-branch
baseline until Stage 7 changes pass CI and live login tests. See
[STAGE7-INVENTORY.md](STAGE7-INVENTORY.md) for the scoped inventory, risk order,
and regression gates. Avoid deleting configuration schema properties solely
because they are obsolete at runtime: existing XML installations must remain
readable. Do not promote Stage 7 changes to production without Stage 8 audit.
