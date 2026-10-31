using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.SSO_Auth.Api;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Security;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcApiSecurityTests
{
    [Theory]
    [InlineData(nameof(SSOController.OidPost), "GET")]
    [InlineData(nameof(SSOController.OidChallenge), "GET")]
    [InlineData(nameof(SSOController.OidAuth), "POST")]
    [InlineData(nameof(SSOController.OidProviderNames), "GET")]
    public void PublicAuthenticationActionsExplicitlyAllowAnonymous(string actionName, string expectedVerb)
    {
        var method = typeof(SSOController).GetMethod(actionName);
        Assert.NotNull(method);
        Assert.NotNull(method.GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.Null(method.GetCustomAttribute<AuthorizeAttribute>());
        var verbs = method.GetCustomAttributes<HttpMethodAttribute>()
            .SelectMany(attribute => attribute.HttpMethods)
            .Distinct()
            .ToArray();
        Assert.Equal(expectedVerb, Assert.Single(verbs));
    }

    [Theory]
    [InlineData(nameof(SSOController.OidAdd), "POST")]
    [InlineData(nameof(SSOController.OidDel), "DELETE")]
    [InlineData(nameof(SSOController.OidProviders), "GET")]
    [InlineData(nameof(SSOController.Unregister), "POST")]
    [InlineData(nameof(SSOController.AddCanonicalLink), "POST")]
    [InlineData(nameof(SSOController.DeleteCanonicalLink), "DELETE")]
    [InlineData(nameof(SSOController.GetOidLinksByUser), "GET")]
    public void AdministrativeActionsRequireElevationAndCorrectHttpMethod(string actionName, string expectedVerb)
    {
        var method = typeof(SSOController).GetMethod(actionName);
        Assert.NotNull(method);
        var policy = method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(policy);
        Assert.Equal(Policies.RequiresElevation, policy.Policy);
        Assert.Null(method.GetCustomAttribute<AllowAnonymousAttribute>());

        var verbs = method.GetCustomAttributes<HttpMethodAttribute>()
            .SelectMany(attribute => attribute.HttpMethods)
            .ToArray();
        Assert.Contains(expectedVerb, verbs);
        if (expectedVerb != "GET")
        {
            Assert.DoesNotContain("GET", verbs);
        }
    }

    [Theory]
    [InlineData("https://idp.example/realms/media", true)]
    [InlineData("http://idp.example/realms/media", false)]
    [InlineData("https://name:secret@idp.example/realms/media", false)]
    [InlineData("https://idp.example/realms/media?token=secret", false)]
    [InlineData("https://idp.example/realms/media#fragment", false)]
    [InlineData("not-a-url", false)]
    [InlineData("", false)]
    public void SecureOidcPolicyRequiresHttpsAuthority(string authority, bool expected)
    {
        var config = new OidConfig { OidEndpoint = authority };
        Assert.Equal(expected, OidcProviderSecurityPolicy.IsSecureForAuthentication(config));
    }

    [Fact]
    public void LegacyValidationBypassFlagsDoNotBlockSecureProviders()
    {
        var config = new OidConfig { OidEndpoint = "https://idp.example/realms/media" };
        Assert.True(OidcProviderSecurityPolicy.IsSecureForAuthentication(config));

        // The public OIDC challenge and callback hard-code RequireHttps and
        // ValidateIssuerName to true. Legacy flags cannot weaken these checks,
        // and must not break valid providers during a plugin upgrade.
        config.DisableHttps = true;
        config.DoNotValidateIssuerName = true;
        Assert.True(OidcProviderSecurityPolicy.IsSecureForAuthentication(config));

        // Even legacy settings never allow an insecure authority URL.
        config.OidEndpoint = "http://idp.example/realms/media";
        Assert.False(OidcProviderSecurityPolicy.IsSecureForAuthentication(config));

        config.OidEndpoint = "https://idp.example/realms/media";
        config.DoNotValidateEndpoints = true;
        Assert.True(OidcProviderSecurityPolicy.IsSecureForAuthentication(config));
    }

    [Fact]
    public void NullProviderDoesNotMeetSecureOidcPolicy()
    {
        Assert.False(OidcProviderSecurityPolicy.IsSecureForAuthentication(null));
    }
}
