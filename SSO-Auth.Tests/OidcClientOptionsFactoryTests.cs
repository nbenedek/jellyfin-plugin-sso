using System;
using System.Linq;
using System.Net.Http;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcClientOptionsFactoryTests
{
    [Fact]
    public void ChallengeAndCallbackUseTheSameStrictDiscoveryPolicy()
    {
        var config = new OidConfig
        {
            OidEndpoint = " https://idp.example.org/realms/media ",
            OidClientId = " example-client ",
            OidSecret = " example-secret ",
            OidScopes = new[] { "email", "groups" },
            DisableHttps = true,
            DoNotValidateIssuerName = true,
            DoNotValidateEndpoints = true,
            DoNotLoadProfile = true,
            DisablePushedAuthorization = true
        };

        var options = OidcClientOptionsFactory.Create(
            config,
            "https://jellyfin.example.org/sso/OID/redirect/example",
            config.OidScopes,
            NullLoggerFactory.Instance,
            () => new HttpClient());

        Assert.Equal("https://idp.example.org/realms/media", options.Authority);
        Assert.Equal("example-client", options.ClientId);
        Assert.Equal("example-secret", options.ClientSecret);
        Assert.Equal("openid profile email groups", options.Scope);
        Assert.True(options.Policy.Discovery.RequireHttps);
        Assert.True(options.Policy.Discovery.ValidateIssuerName);
        Assert.False(options.Policy.Discovery.ValidateEndpoints);
        Assert.False(options.LoadProfile);
        Assert.True(options.DisablePushedAuthorization);
        Assert.Contains("https://idp.example.org", options.Policy.Discovery.AdditionalEndpointBaseAddresses);
        Assert.Equal("https://jellyfin.example.org/sso/OID/redirect/example", options.RedirectUri);
    }

    [Fact]
    public void NormalSettingsRetainStrictIssuerAndEndpointValidation()
    {
        var config = new OidConfig { OidEndpoint = "https://idp.example.org/realms/media" };
        var options = OidcClientOptionsFactory.Create(
            config,
            "https://jellyfin.example.org/sso/OID/redirect/example",
            Array.Empty<string>(),
            NullLoggerFactory.Instance,
            () => new HttpClient());

        Assert.True(options.Policy.Discovery.RequireHttps);
        Assert.True(options.Policy.Discovery.ValidateIssuerName);
        Assert.True(options.Policy.Discovery.ValidateEndpoints);
        Assert.Equal("openid profile", options.Scope);
        Assert.True(options.LoadProfile);
    }
}
