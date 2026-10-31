# Stage 4 API security verification

The Jellyfin API controller uses the route prefix `/sso`.

| Endpoint                                                | Method | Authentication requirement                           |
| ------------------------------------------------------- | ------ | ---------------------------------------------------- |
| `/sso/OID/start/{provider}` and `/sso/OID/p/{provider}` | GET    | Public OIDC challenge; valid enabled provider only   |
| `/sso/OID/redirect/{provider}`                          | GET    | Public OIDC callback; verified state required        |
| `/sso/OID/Auth/{provider}`                              | POST   | Public exchange; short-lived one-time token required |
| `/sso/OID/GetNames`                                     | GET    | Public; only enabled provider names                  |
| `/sso/OID/Get`                                          | GET    | Elevated Jellyfin administrator                      |
| `/sso/OID/Add/{provider}`                               | POST   | Elevated Jellyfin administrator                      |
| `/sso/OID/Del/{provider}`                               | DELETE | Elevated Jellyfin administrator; GET must not mutate |
| `/sso/Unregister/{username}`                            | POST   | Elevated Jellyfin administrator                      |
| `/sso/oid/Link/{provider}/{userId}`                     | POST   | Elevated Jellyfin administrator                      |
| `/sso/oid/links/{userId}`                               | GET    | Elevated Jellyfin administrator                      |
| `/sso/oid/Link/{provider}/{userId}/{name}`              | DELETE | Elevated Jellyfin administrator                      |

## Manual negative-access test matrix

On `development Jellyfin instance`, test without authentication and with a non-administrator
Jellyfin session. Administrator endpoints should reject these requests (usually
401 without authentication and 403 when authenticated without elevation).
Do not test provider DELETE against an active provider.

1. Request GET `/sso/OID/Get` as an unauthenticated visitor and a normal user;
   verify provider configuration and secrets are not returned.
2. Attempt POST to `/sso/OID/Add/test` as a normal user; verify no provider is created.
3. Request GET `/sso/OID/Del/example-oidc` using an administrator session; expect
   method not allowed, **not** deletion. Recheck `example-oidc` remains configured.
4. Attempt the identity-link POST, list GET, and unlink DELETE as a normal user;
   verify access is denied and mappings are unchanged.
5. Request public `/sso/OID/GetNames` and check disabled providers are absent.
6. Request an unknown challenge provider and a POST exchange with no body;
   expect handled errors instead of HTTP 500.
7. Test standard OIDC login, authorization and native Jellyfin administrator
   access following deployment. Test that the existing issuer/subject mappings
   and per-user library permissions are unchanged.

## Security policy

OIDC authority URLs must use HTTPS; HTTPS discovery and issuer-name validation
are always enforced for sign-in. The deprecated `DisableHttps` and
`DoNotValidateIssuerName` settings remain loadable for backward compatibility
but are ignored by the authentication path, even when set, so upgrading does
not lock users out for stale settings. The identity provider must still
successfully pass the enforced HTTPS and issuer checks. Endpoint-host
validation exceptions remain available for identity providers with nonstandard
discovery metadata and should be used sparingly.

This stage does not change username/subject identity resolution or the user's
existing Jellyfin authorization policy.

## Diagnosing an unexpected identity-resolution HTTP 409

A correctly validated OIDC login can still fail at the handoff if the resolved
issuer/subject identity key is absent from the running provider configuration, or
if its linked Jellyfin user no longer exists. This failure is intentionally
**not** resolved using the username.

On a failed login, check Jellyfin's logs for `OIDC resolver rejected sign-in`.
Diagnostic entries include the provider, a 12-hex-character prefix of the
SHA-256-derived opaque identity key, whether the validated issuer exactly
matches the configured authority, and boolean collision indicators.
The raw issuer, subject, username, token, and client secret are never logged.

The fingerprint is the first 12 hex characters of the SHA-256 identity key
computed from the exact OIDC `iss` and `sub` values. If the stored key prefix
does not match the login key prefix, check the exact issuer and subject at the
identity provider, rather than relinking or weakening identity security.

These diagnostic logs do not alter account mappings or permission policies.
