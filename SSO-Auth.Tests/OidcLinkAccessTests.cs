using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.SSO_Auth.Api;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcLinkAccessTests
{
    [Theory]
    [InlineData(nameof(SSOController.AddCanonicalLink))]
    [InlineData(nameof(SSOController.DeleteCanonicalLink))]
    [InlineData(nameof(SSOController.GetOidLinksByUser))]
    public void AllIdentityLinkManagementEndpointsRequireElevation(string actionName)
    {
        var action = typeof(SSOController).GetMethod(actionName);
        Assert.NotNull(action);
        var authorization = action.GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(authorization);
        Assert.Equal(Policies.RequiresElevation, authorization.Policy);
    }

    [Fact]
    public void PublicLinkingPageAndAssetsAreNotRegistered()
    {
        var assembly = typeof(SSOPlugin).Assembly;

        Assert.Null(assembly.GetType("Jellyfin.Plugin.SSO_Auth.Views.SSOViewsController"));
        Assert.DoesNotContain(
            assembly.GetManifestResourceNames(),
            name => name.Contains(".Config.linking.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AdministratorLinkRequiresUsableIssuerSubjectAndUsername()
    {
        var request = new AdminOidcLinkRequest
        {
            Issuer = "https://accounts.example/realms/media",
            Subject = "immutable-subject",
            Username = "alice"
        };
        Assert.True(request.IsValid());

        request.Issuer = "http://accounts.example/realms/media";
        Assert.False(request.IsValid());

        request.Issuer = "https://accounts.example/realms/media?wrong=issuer";
        Assert.False(request.IsValid());

        request.Issuer = "https://accounts.example/realms/media";
        request.Subject = " ";
        Assert.False(request.IsValid());

        request.Subject = "immutable-subject";
        request.Username = string.Empty;
        Assert.False(request.IsValid());
    }

    [Fact]
    public void LinkRoutesDoNotExposePublicOidcLinkStart()
    {
        var routes = typeof(SSOController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SelectMany(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>())
            .Select(attribute => attribute.Template)
            .ToArray();

        Assert.DoesNotContain(routes, route => route is not null
            && route.Contains("LinkStart", StringComparison.OrdinalIgnoreCase));
    }
}
