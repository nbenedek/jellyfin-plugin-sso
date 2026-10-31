using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using Duende.IdentityModel.OidcClient;
using Jellyfin.Plugin.SSO_Auth.Config;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Builds matching secure OIDC client settings for challenge and callback flows.
/// </summary>
public static class OidcClientOptionsFactory
{
    /// <summary>
    /// Creates OIDC options with shared discovery and issuer security policy.
    /// </summary>
    /// <param name="config">The provider's stored configuration.</param>
    /// <param name="redirectUri">The callback URI.</param>
    /// <param name="scopes">The original caller's extra scopes.</param>
    /// <param name="loggerFactory">Logger factory for OIDC diagnostics.</param>
    /// <param name="httpClientFactory">Creates an HTTP client for OIDC discovery.</param>
    /// <returns>Options for an OIDC client.</returns>
    public static OidcClientOptions Create(
        OidConfig config,
        string redirectUri,
        IEnumerable<string> scopes,
        ILoggerFactory loggerFactory,
        Func<HttpClient> httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(httpClientFactory);

        var options = new OidcClientOptions
        {
            Authority = config.OidEndpoint?.Trim(),
            ClientId = config.OidClientId?.Trim(),
            ClientSecret = config.OidSecret?.Trim(),
            RedirectUri = redirectUri,
            Scope = string.Join(" ", scopes.Prepend("openid profile")),
            DisablePushedAuthorization = config.DisablePushedAuthorization,
            LoggerFactory = loggerFactory,
            LoadProfile = !config.DoNotLoadProfile,
            HttpClientFactory = o =>
            {
                var client = httpClientFactory();
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(assembly.Location);
                string version = fvi.FileVersion;
                client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-Plugin-SSO-Auth +{version} (https://github.com/9p4/jellyfin-plugin-sso)");
                return client;
            }
        };

        var authority = new Uri(config.OidEndpoint?.Trim());
        options.Policy.Discovery.AdditionalEndpointBaseAddresses.Add(authority.GetLeftPart(UriPartial.Authority));
        options.Policy.Discovery.ValidateEndpoints = !config.DoNotValidateEndpoints;
        options.Policy.Discovery.RequireHttps = true;
        options.Policy.Discovery.ValidateIssuerName = true;
        return options;
    }
}
