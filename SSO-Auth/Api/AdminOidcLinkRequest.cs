using Jellyfin.Plugin.SSO_Auth.Security;

namespace Jellyfin.Plugin.SSO_Auth.Api;

/// <summary>
/// Administrator-supplied identity details for explicitly linking an existing Jellyfin account.
/// The subject is the immutable OIDC subject (Keycloak user ID), not a display username.
/// </summary>
public sealed class AdminOidcLinkRequest
{
    /// <summary>Gets or sets the exact issuer from the OIDC provider.</summary>
    public string Issuer { get; set; }

    /// <summary>Gets or sets the immutable OIDC subject.</summary>
    public string Subject { get; set; }

    /// <summary>Gets or sets the display username for the link.</summary>
    public string Username { get; set; }

    /// <summary>
    /// Checks the administrator-supplied mapping fields before attempting persistence.
    /// </summary>
    /// <returns>Whether all fields are usable.</returns>
    public bool IsValid()
    {
        return OidcIdentityKey.IsValidIssuer(Issuer)
            && !string.IsNullOrWhiteSpace(Subject)
            && Subject.Length <= 1024
            && !string.IsNullOrWhiteSpace(Username)
            && Username.Length <= 256;
    }
}
