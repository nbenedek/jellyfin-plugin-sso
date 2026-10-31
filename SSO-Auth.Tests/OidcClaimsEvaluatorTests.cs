using System;
using System.Collections.Generic;
using System.Security.Claims;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Security;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcClaimsEvaluatorTests
{
    [Fact]
    public void NoRequiredRolesPreservesUsernameAndDefaultFolders()
    {
        var config = new OidConfig
        {
            DefaultUsernameClaim = "preferred_username",
            EnabledFolders = new[] { "library-a" },
            EnableLiveTv = true
        };
        var evaluation = OidcClaimsEvaluator.Evaluate(config, new[] { new Claim("preferred_username", "viewer") });
        Assert.True(evaluation.IsValid);
        Assert.Equal("viewer", evaluation.Username);
        Assert.Equal(new[] { "library-a" }, evaluation.Folders);
        Assert.True(evaluation.EnableLiveTv);
        Assert.False(evaluation.IsAdministrator);
    }

    [Fact]
    public void NestedRealmRolesMapLibrariesAndAdministrator()
    {
        var config = new OidConfig
        {
            RoleClaim = "realm_access.roles",
            Roles = new[] { "viewer" },
            AdminRoles = new[] { "administrator" },
            EnableFolderRoles = true,
            FolderRoleMapping = new List<FolderRoleMap>
            {
                new() { Role = "viewer", Folders = new List<string> { "library-a" } },
            },
        };
        var evaluation = OidcClaimsEvaluator.Evaluate(config, new[]
        {
            new Claim("preferred_username", "viewer"),
            new Claim("realm_access", "{\"roles\":[\"viewer\",\"administrator\"]}"),
        });
        Assert.True(evaluation.IsValid);
        Assert.Equal(new[] { "library-a" }, evaluation.Folders);
        Assert.True(evaluation.IsAdministrator);
    }

    [Fact]
    public void MissingRequiredRoleDeniesLogin()
    {
        var config = new OidConfig { RoleClaim = "roles", Roles = new[] { "required" } };
        var evaluation = OidcClaimsEvaluator.Evaluate(config, new[]
        {
            new Claim("preferred_username", "viewer"),
            new Claim("roles", "unrelated"),
        });
        Assert.False(evaluation.IsValid);
        Assert.False(evaluation.IsAdministrator);
    }

    [Fact]
    public void SubjectFallbackRemainsAvailableWhenNoRoleIsRequired()
    {
        var config = new OidConfig();
        var evaluation = OidcClaimsEvaluator.Evaluate(config, new[] { new Claim("sub", "opaque-subject") });
        Assert.True(evaluation.IsValid);
        Assert.Equal("opaque-subject", evaluation.Username);
    }

    [Fact]
    public void FlatRoleClaimEnablesLiveTvAccessWithoutAdministratorRights()
    {
        var config = new OidConfig
        {
            RoleClaim = "groups",
            Roles = new[] { "viewer" },
            EnableLiveTvRoles = true,
            LiveTvRoles = new[] { "viewer" },
        };
        var evaluation = OidcClaimsEvaluator.Evaluate(config, new[]
        {
            new Claim("preferred_username", "viewer"),
            new Claim("groups", "viewer"),
        });
        Assert.True(evaluation.IsValid);
        Assert.True(evaluation.EnableLiveTv);
        Assert.False(evaluation.EnableLiveTvManagement);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"roles\":\"admin\"}")]
    [InlineData("{\"roles\":[null]}")]
    public void InvalidNestedRoleClaimsNeverGrantPrivileges(string raw)
    {
        var config = new OidConfig
        {
            RoleClaim = "realm_access.roles",
            Roles = new[] { "viewer" },
            AdminRoles = new[] { "administrator" },
            EnableFolderRoles = true,
            FolderRoleMapping = new List<FolderRoleMap>
            {
                new() { Role = "viewer", Folders = new List<string> { "library-a" } },
            },
        };

        var evaluated = OidcClaimsEvaluator.Evaluate(config, new[]
        {
            new Claim("preferred_username", "viewer"),
            new Claim("realm_access", raw),
        });

        Assert.False(evaluated.IsValid);
        Assert.False(evaluated.IsAdministrator);
        Assert.Empty(evaluated.Folders);
    }
}
