using System;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Defines minimum transport and issuer-validation requirements for public OIDC authentication.
/// </summary>
public static class OidcProviderSecurityPolicy
{
    /// <summary>
    /// Gets the safe configuration error presented when the provider cannot be used for sign-in.
    /// </summary>
    public const string ConfigurationError = "OIDC sign-in requires an HTTPS authority with issuer validation enabled.";

    /// <summary>
    /// Checks whether the authority URL permits a securely validated OIDC login.
    /// </summary>
    /// <param name="config">The configured identity provider.</param>
    /// <returns>Whether this provider meets minimum authentication requirements.</returns>
    public static bool IsSecureForAuthentication(OidConfig config)
    {
        // Older installations may retain deprecated bypass switches. These flags
        // are ignored here because the OIDC client unconditionally requires HTTPS
        // and validates the issuer. Do not lock existing users out based on them.
        if (config is null)
        {
            return false;
        }

        if (!Uri.TryCreate(config.OidEndpoint?.Trim(), UriKind.Absolute, out var authority))
        {
            return false;
        }

        // Endpoint-host validation is a separate discovery-compatibility setting.
        // A discovery exception must never relax the HTTPS or issuer requirements.
        return string.Equals(authority.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrEmpty(authority.UserInfo)
            && string.IsNullOrEmpty(authority.Query)
            && string.IsNullOrEmpty(authority.Fragment);
    }
}
