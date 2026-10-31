# Optional emphasis for an existing Jellyfin SSO login link

**Status:** optional CSS recipe, not automatically installed. Verify the
actual SSO link markup on development Jellyfin instance before enabling.

If your login page uses an `<a href="/sso/OID/start/PROVIDER">` or
`<a href="/sso/OID/p/PROVIDER">` link, you can add the following to
Jellyfin Dashboard → Branding → Custom CSS:

```css
/* Emphasize only explicit external SSO links, not the entire login page. */
#loginPage a[href*="/sso/OID/start/"],
#loginPage a[href*="/sso/OID/p/"] {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 0.85rem 1rem;
  min-height: 44px;
  margin: 0.75rem auto;
  border-radius: 0.5rem;
  background: #2f744c;
  color: #fff;
  text-align: center;
  font-weight: 650;
  text-decoration: none;
  max-width: 28rem;
}
#loginPage a[href*="/sso/OID/start/"]:focus-visible,
#loginPage a[href*="/sso/OID/p/"]:focus-visible {
  outline: 3px solid currentColor;
  outline-offset: 3px;
}
```

This CSS makes existing SSO links primary, but leaves Jellyfin's username and
password form and administrator recovery login fully functional. It will have
no effect if the existing SSO button uses a different element/URL or is loaded
in a native app that does not apply server branding CSS. Inspect the actual
DOM in Firefox before modifying selectors.

Avoid broad selectors that hide `.manualLoginForm`, `.btnManual`,
`#txtManualName` or `#txtManualPassword`, as those can lock out
administrators if Keycloak is unavailable.
