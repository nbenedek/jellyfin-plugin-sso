# Stage 8 — security review and release gates

**Scope:** Jellyfin SSO OIDC-only fork, intended for Jellyfin 10.11.x.
This document records a code review and automated protections; it is **not**
a claim of a completed independent penetration test.

## Validated protections in source and tests

- Only cryptographically validated issuer/subject identities are used for
  account resolution. Username-based legacy aliases do not authenticate.
- OIDC protocol states and exchange tokens are bounded, provider-bound,
  expiring and consumed once. Self-service account linking is disabled.
- Elevated administrator policy protects provider configuration, management
  and account-linking API routes. Wrong method requests do not mutate state.
- Public provider discovery excludes disabled providers.
- The client requires HTTPS and validates issuer identity even if deprecated
  bypass configuration fields are present.
- Disabled SSO-managed authorization does not overwrite manually configured
  Jellyfin library permissions.
- Provider saves preserve server-owned link dictionaries and cannot import
  client-supplied associations. Dedicated elevated linking routes remain
  responsible for adding and removing associations.
- Malformed nested role-claim JSON does not grant roles or administrator access.
- Native XML migration tests verify configuration preservation, identity
  collision rejection and one-use approval hashes.

## Residual risks and operational constraints

- OIDC callback URLs rely on the host and proxy headers supplied to Jellyfin.
  Reverse proxies must reject unrecognized Host values and correctly configure
  trusted forwarded headers. Verify the actual public redirect URI on the
  development server and guard against host-header poisoning.
- The browser handoff relies on Jellyfin Web local storage and a legacy iframe
  bootstrap. Test Firefox, Chrome and Android on the intended version. The
  callback HTML is deliberately not rewritten during this release candidate.
- The legacy `DoNotValidateEndpoints` option remains for provider discovery
  compatibility; it must never disable HTTPS or issuer validation. Review
  exceptions for every enabled provider.
- The OIDC client and JSON library dependencies remain in use. Upgrading them
  without a separate compatibility matrix risks login breakage; inspect NuGet
  audit output when a supported package upgrade is considered.
- Jellyfin is a stateful application. Ensure plugin and configuration backups
  are consistent, then verify restart behavior and native administrator
  recovery.
- No live administrative API or cross-client test can be claimed as passed
  merely because CI passed. Exact commit validation is mandatory.
- Existing unmigrated legacy accounts require independent administrative
  inventory and explicit migration or decommission decisions.

## Release/no-release decision

**Go** only after GitHub CI and release-package checksum pass, all required
live tests in [RELEASE-CANDIDATE.md](RELEASE-CANDIDATE.md) pass for the same
revision, remaining legacy accounts are reviewed, and rollback is tested.
Until then the release candidate is development-only.
