# SSO Authentication for Jellyfin

An OpenID Connect (OIDC) authentication plugin for **Jellyfin 10.11.x**, maintained as a security-focused fork of the archived [original Jellyfin SSO project](https://github.com/9p4/jellyfin-plugin-sso).

This fork supports OIDC sign-in with one-time login transactions, issuer/subject identity matching, administrator-controlled account linking, and a modernized Jellyfin administrator interface. It does **not** support SAML. Users cannot link their own Jellyfin accounts through a public linking page.

## Release status

**4.1.0.0** is accepted on a development Jellyfin instance, including browser and Android sign-in, permissions, restart persistence, and 8/8 public security smoke tests. Production promotion remains a separate administrative step.

See [CHANGELOG.md](CHANGELOG.md) for release notes and [Release candidate / installation guidance](docs/RELEASE-CANDIDATE.md) for installation, configuration preservation, acceptance checks, and rollback.

## Installation and updates

Until the fork's GitHub release manifest is published and verified, use the CI-generated **SSO Authentication 4.1.0.0** ZIP from the [Release Candidate Package workflow](https://github.com/nbenedek/jellyfin-plugin-sso/actions/workflows/release-candidate.yml). The ZIP must contain `SSO-Auth.dll`, `Duende.IdentityModel.dll`, `Duende.IdentityModel.OidcClient.dll`, and `meta.json`.

Back up the existing versioned plugin directory and `plugins/configurations/SSO-Auth.xml`, stop Jellyfin, replace the versioned plugin directory with the **complete** ZIP, and restart Jellyfin. Do not delete the configuration or install two versions concurrently. Verify the plugin version in Dashboard → Plugins and confirm an OIDC login after upgrading.

The fork's planned plugin repository URL is:

`https://raw.githubusercontent.com/nbenedek/jellyfin-plugin-sso/manifest-release/manifest.json`

**Do not add that URL until the branch and manifest actually exist and have been verified.** The archived upstream catalog is not the update source for this fork.

## Configuring an identity provider

Use **Jellyfin Dashboard → Plugins → SSO Authentication** to configure your OIDC provider. The provider must expose standards-compliant HTTPS discovery metadata and an issuer that matches validated ID tokens. Existing credentials and identity mappings are preserved by supported upgrades; do not commit secrets, account IDs, or personal settings to the public repository.

### Administrator-only account linking

Administrators can associate an existing Jellyfin account with the exact **issuer (`iss`)** and **subject (`sub`)** of an OIDC identity. Display names, email addresses, and usernames are not substitute identity keys. An existing identity mapping cannot be silently reassigned.

See the [configuration and identity-linking release notes](docs/RELEASE-CANDIDATE.md).

## Legacy migration

The [Keycloak-only offline migration guide](docs/CLI-MIGRATION.md) describes the supported .NET 9 utility for explicitly reviewed migrations from older username-based links. The former Python XML writer is retired. The migration utility is not included in the runtime plugin ZIP.

## Development and validation

- [Development roadmap](docs/ROADMAP.md)
- [Security audit](docs/SECURITY-AUDIT.md)
- [Stage 6 administrator UI checks](docs/STAGE6-UI.md)
- [Release candidate validation](docs/RELEASE-CANDIDATE.md)
- [Provider guidance](providers.md) (review for applicability to the OIDC-only fork)

The project retains the original plugin's license and attribution. Historical upstream instructions, changelogs and code remain available through the Git history and the [archived upstream repository](https://github.com/9p4/jellyfin-plugin-sso).
