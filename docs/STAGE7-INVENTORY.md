# Stage 7 — legacy cleanup and dependency reduction

**Status:** inventory prepared; refactors require CI and development Jellyfin instance validation.
**Accepted starting point:** Stage 6 passed 8/8 user-reported live checks on 2026-10-31. Keep production unchanged until Stage 8.

## Non-negotiable behavior

- Preserve validated issuer + subject as the login identity. Never authenticate by display username or silently adopt a legacy `CanonicalLinks` mapping.
- Preserve `SubjectLinks`, `SubjectNames`, `CanonicalLinks`, the plugin GUID, and backward-readable XML configuration across save, restart, and upgrade.
- Keep administrator elevation on configuration, provider mutations and identity linking; public OIDC routes must remain narrowly scoped.
- Keep both the Keycloak login path and native Jellyfin administrator recovery login.
- Do not change user library policy when the plugin's managed authorization feature is disabled.
- Preserve one-use, provider-bound, expiring OIDC state and exchange credentials.

## Code inventory and recommended order

1. **Characterization tests before structural changes.** Cover callback HTML generation, paths for web/native clients, error handling for missing localStorage credentials, and response parsing. `SSO-Auth/WebResponse.cs` is approximately 479 lines of embedded JavaScript, device/browser detection, and HTML. It is a high-risk legacy integration boundary, not safe to replace in one step. Its current generated client string includes a hardcoded Jellyfin Web version (`10.8.0`), which merits separate compatibility testing rather than a blind version change.
2. **Controller separation.** `SSO-Auth/Api/SSOController.cs` is approximately 934 lines and currently mixes callback/challenge, public token exchange, provider administration, identity resolution, and user authorization. Extract pure, testable functions first; split into public and elevated controllers only after route and policy regression tests. Preserve the existing public URLs.
3. **Configuration compatibility.** `SSO-Auth/Config/PluginConfiguration.cs` still defines the legacy `DisableHttps` and `DoNotValidateIssuerName` XML properties. Keep them deserializable while ensuring authentication always enforces TLS and issuer validation. Do not remove legacy migration fields until migration inventory is complete.
4. **Dependency review.** The plugin directly references Duende.IdentityModel.OidcClient 6.0.1 and Newtonsoft.Json 13.0.3 in `SSO-Auth/SSO-Auth.csproj`. Identify actual call sites and transitive constraints before replacement/removal. Avoid simultaneous client-library updates and security-sensitive refactoring.
5. **Migration script retirement.** The unsafe Python XML writer is retired. The supported offline migration path is the standalone .NET 9 `SSO-Auth.Migration` CLI. Native .NET tests cover XML round-tripping, identity collisions, approval hashes and controlled application; no plugin runtime migration behavior changes.
6. **Login emphasis remains independent.** `docs/LOGIN-EMPHASIS.md` provides optional CSS only. Verify the actual login button markup in web and Android environments before adding styling. Never hide manual administrator login.

## Proposed Stage 7 sequence

- **7A:** Add characterization/regression tests for the callback HTML and public/admin route contracts. No behavior changes.
- **7B:** Move pure role-claim interpretation and authorization calculations from the controller into testable services with equivalence tests.
- **7C:** Reduce duplicated OIDC client configuration and endpoint validation while retaining security invariants.
- **7D:** Consolidate legacy migration tooling, clean obsolete UI/assets, and review unused packages with a targeted build/test cycle for each change.
- **7E:** Live test against Keycloak on development Jellyfin instance with both migrated accounts, disabled-provider negative cases, provider edits, restart persistence, and native admin recovery. Report the exact tested commit.

## Outstanding separate gate

Before Stage 8 production promotion, audit any remaining unmigrated legacy accounts. Stage 6 acceptance confirmed both already-migrated accounts survive settings edits and restarts; it does not prove every historical account has been migrated.

## CI and live-release gates

- `dotnet build --configuration Release --no-restore --warnaserror`
- `dotnet test --configuration Release --no-build`
- `node --test tests/stage6-ui.test.mjs`
- `development Jellyfin instance`: stage 4 negative access + enabled Keycloak sign-in, repeat sign-in, admin recovery, migration preservation, correct library permissions after restart.
