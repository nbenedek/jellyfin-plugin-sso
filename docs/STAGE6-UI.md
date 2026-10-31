# Stage 6: SSO Authentication administrator UI

**State:** implemented on the development branch; not yet installed or
browser-tested on `development Jellyfin instance`.

## Dashboard navigation: no web patch required

Jellyfin Web **10.11.6** already renders `PluginDrawerSection` from
`GetConfigurationPages(enableInMainMenu: true)` and uses each page's
`DisplayName`. The SSO configuration page now sets:

```csharp
Name = "SSO-Auth",
DisplayName = "SSO Authentication",
EnableInMainMenu = true,
```

This should show **SSO Authentication** directly in the Dashboard's
**Plugins** section. It opens the same admin configuration page as
Dashboard → Plugins → SSO-Auth → Settings. There is no new URL, new
controller, additional permission bypass, or duplicate identity-linking UI.
All sensitive SSO configuration and identity-linking API operations retain
the server-side elevated administrator policy.

## Settings changes

- Explicit **Load Provider** and **Configure a new provider** actions. Loading
  another provider clears all fields first (including false checkboxes,
  previously typed values and advanced options).
- OIDC issuer must be HTTPS and cannot include URL credentials, query or
  fragment. The callback port override must be 1–65535 when specified.
- Client secret is shown as a password field with a reveal control; a blank
  input on an existing provider preserves the saved secret, but new providers
  require one. Existing administrators still receive the secret from the
  original Jellyfin configuration API; masking is a UI improvement, **not
  a backend secret-transport redesign**.
- Save reloads a fresh configuration and retains existing subject-identity
  dictionaries. It does not create, migrate or relink accounts.
- Advanced options and account-linking controls are collapsible. The latter
  remain administrator-restricted through server APIs.
- Deleting a provider requires typing its name because its identity mappings
  are removed too.

## Login emphasis

Jellyfin Web 10.11 has a built-in login page and may host an SSO button added
via Custom Branding/Custom CSS or an external integration. Merely adding the
SSO configuration page to the dashboard does **not** inject a login button.
We do not modify Jellyfin's login templates or disable native login, since the
administrator recovery path must survive. A conservative opt-in CSS recipe is
in [LOGIN-EMPHASIS.md](LOGIN-EMPHASIS.md); it requires verifying the actual
SSO button DOM on development Jellyfin instance before use. Automatic login styling is deferred
until that DOM is known.

## Development acceptance procedure

1. Re-enable `example-oidc` if still disabled after Stage 4. Verify a clean
   Keycloak login and a working native admin login.
2. Back up the working plugin binary and `SSO-Auth.xml`; deploy the
   development plugin **only on development Jellyfin instance**, leaving production unchanged.
3. Hard-refresh Firefox. Enter Dashboard: confirm a
   **SSO Authentication** entry appears under Plugins and opens the page.
   Check Plugins → SSO-Auth → Settings still opens the same page.
4. As a non-admin, try direct page navigation; verify SSO configuration and
   identity-link APIs still reject access. The page's presence alone is not
   access control.
5. Load one provider with an unchecked boolean, then another with a checked
   value and switch back. Confirm no stale field values remain.
6. Expand Advanced and Identity Links, verify those fields are retained after
   load. Save a harmless change, refresh and restart Jellyfin. Confirm all
   `SubjectLinks`, `SubjectNames`, `CanonicalLinks`, client ID, scopes
   and library mappings persist. Preserve access to both migrated users.
7. Check the original Keycloak login, repeat login, manual admin login and
   library permissions after restart. Check the login page on both desktop
   and Android before accepting CSS changes.

Do not save or paste the SSO client secret or exchange credentials into a
support conversation. On error, restore the backed-up plugin DLL and config
before altering provider settings manually.
