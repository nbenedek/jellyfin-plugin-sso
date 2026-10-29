# OIDC-only security baseline

This fork keeps the useful Jellyfin integration from the archived SSO plugin while
incrementally replacing security-sensitive internals.

## Baseline decisions

- OpenID Connect is the only supported SSO protocol.
- The custom SAML implementation is not carried forward.
- Remote avatar fetching from claim-derived URLs is removed.
- The descriptive OIDC callback path is canonical:
  `/sso/OID/redirect/{provider}`.
- Existing OIDC account-linking behavior remains temporarily for migration
  compatibility. Identity mapping will be redesigned separately.
- The current controller state dictionary remains active for this baseline commit,
  but a provider-bound, expiring, one-time transaction store is introduced with
  regression tests and will replace it in the next security refactor.

## Planned separation

The monolithic controller will be split into:

- public OIDC login endpoints;
- administrator-only configuration endpoints;
- OIDC client/discovery creation;
- claims and role mapping;
- stable external identity mapping;
- Jellyfin authorization synchronization;
- persisted restart reconciliation;
- short-lived login transaction storage.

## Security invariants for subsequent work

1. Stable external identity is based on validated OIDC issuer + subject, not username.
2. A matching Jellyfin username never silently proves account ownership.
3. Protocol state is not the browser's Jellyfin-session exchange credential.
4. Login transactions are provider-bound, short-lived, and one-time.
5. Administrative APIs are protected by Jellyfin elevation policy at controller level.
6. Client secrets are never returned to the browser after storage.
7. SSO authorization changes only permission areas explicitly configured as managed.
8. Restart reconciliation restores the last validated managed authorization snapshot.
