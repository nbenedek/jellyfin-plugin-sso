using System;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Security;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcProviderConfigurationSafetyTests
{
    [Fact]
    public void EditingProviderPreservesAllServerOwnedIdentityMappings()
    {
        var id = Guid.NewGuid();
        var current = new OidConfig { Enabled = true, OidEndpoint = "https://idp.example.org/realms/media" };
        current.CanonicalLinks["legacy"] = id;
        current.SubjectLinks["oidc-v1-key"] = id;
        current.SubjectNames["oidc-v1-key"] = "label";

        var replacement = new OidConfig { Enabled = false, OidEndpoint = current.OidEndpoint };
        replacement.SubjectLinks["oidc-v1-key"] = Guid.NewGuid();
        replacement.SubjectNames["oidc-v1-key"] = "forged";

        OidcProviderConfigurationSafety.PreserveLinks(current, replacement);

        Assert.False(replacement.Enabled);
        Assert.Equal(id, replacement.CanonicalLinks["legacy"]);
        Assert.Equal(id, replacement.SubjectLinks["oidc-v1-key"]);
        Assert.Equal("label", replacement.SubjectNames["oidc-v1-key"]);
        Assert.Same(current.SubjectLinks, replacement.SubjectLinks);
    }

    [Fact]
    public void CreatingProviderRejectsPreloadedIdentityMappings()
    {
        var replacement = new OidConfig { Enabled = true };
        replacement.CanonicalLinks["legacy"] = Guid.NewGuid();
        replacement.SubjectLinks["oidc-v1-key"] = Guid.NewGuid();
        replacement.SubjectNames["oidc-v1-key"] = "forged";

        OidcProviderConfigurationSafety.PreserveLinks(null, replacement);

        Assert.Empty(replacement.CanonicalLinks);
        Assert.Empty(replacement.SubjectLinks);
        Assert.Empty(replacement.SubjectNames);
    }

    [Fact]
    public void MissingReplacementCannotModifyConfiguration()
    {
        Assert.Throws<ArgumentNullException>(() => OidcProviderConfigurationSafety.PreserveLinks(new OidConfig(), null));
    }
}
