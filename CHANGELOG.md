# Changelog

## 4.2.0.1 — Client IP reporting fix (2026-11-20)

- Populate Jellyfin's authentication request remote endpoint from the trusted ASP.NET Core connection address.
- Fix blank IP fields in successful OIDC authentication and online-session activity entries.
- Do not parse untrusted forwarded-IP headers directly; proxy trust remains configured in Jellyfin.
- No changes to OIDC identity linking, existing accounts, or folder permissions.
- Targets Jellyfin 12.x and .NET 10; 4.2.0.0 remains available for rollback, and 4.1.0.0 remains for Jellyfin 10.11.x.


This changelog describes the actively maintained OIDC-only fork. Earlier releases and historical SAML functionality belong to the archived [upstream project](https://github.com/9p4/jellyfin-plugin-sso), not to the current fork.

## 4.1.0.0 — OIDC-only security and usability release (2026-10-31)

### Authentication and identity

- Replaced legacy sign-in handling with expiring, one-time, provider-bound OIDC login transactions and replay-resistant exchange tokens.
- Use validated issuer (`iss`) and subject (`sub`) as persistent identity keys instead of relying on a mutable username.
- Restrict creating, reviewing and deleting identity links to authenticated Jellyfin administrators; disable public self-service account linking.
- Enforce provider configuration and access restrictions on the backend.
- Fail closed on malformed nested role claims rather than leaking errors or silently widening authorization.

### Authorization and stability

- Preserve existing `SubjectLinks`, `SubjectNames` and `CanonicalLinks` when provider settings are edited.
- Preserve manually configured Jellyfin library permissions when SSO-managed authorization is disabled.
- Fix authorization behavior across Jellyfin service restarts, with regression and live validation.
- Keep native Jellyfin administrator sign-in available for recovery.

### Administration and maintainability

- Modernize Jellyfin's administrator configuration page, including native navigation and expandable advanced settings.
- Refactor shared OIDC configuration and role-claim interpretation to reduce duplicated code.
- Retire the unsafe Python XML migration writer. Retain a separate, tested .NET 9 **Keycloak-specific** offline migration utility; it is not bundled into the runtime plugin.

### Packaging and verification

- Target Jellyfin 10.11.x and .NET 9; versioned assembly and `meta.json`.
- Include the plugin DLL and its two OIDC dependencies in the runtime ZIP.
- Add package structure/metadata regression checks and SHA-256 checksum generation to CI.
- Automated build, security regression and UI tests completed; the operator also accepted the development instance's live sign-in, client, restart and permission checks.

### Upgrade notes

Back up both the installed plugin directory and `plugins/configurations/SSO-Auth.xml`. Replace the **entire** versioned plugin package, not only `SSO-Auth.dll`. Do not run two versions simultaneously. Keep the configuration and identity mappings intact. No stable GitHub plugin repository manifest has yet been published for automatic installation.

See [Release candidate / installation guidance](docs/RELEASE-CANDIDATE.md).
