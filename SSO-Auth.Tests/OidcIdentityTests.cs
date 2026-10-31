using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Security;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcIdentityTests
{
    private const string Issuer = "https://accounts.example/realms/media";
    private const string Subject = "stable-keycloak-user-id";

    [Fact]
    public void IdentityKeyIsStableWhenDisplayUsernameChanges()
    {
        var initial = OidcIdentityKey.Create(Issuer, Subject);
        var afterRename = OidcIdentityKey.Create(Issuer, Subject);

        Assert.Equal(initial, afterRename);
        Assert.StartsWith("oidc-v1-", initial);
        Assert.DoesNotContain("stable-keycloak-user-id", initial);
        Assert.DoesNotContain("accounts.example", initial);
    }

    [Fact]
    public void IssuerAndSubjectAreBothPartOfIdentity()
    {
        Assert.NotEqual(
            OidcIdentityKey.Create(Issuer, Subject),
            OidcIdentityKey.Create("https://other.example/realms/media", Subject));
        Assert.NotEqual(
            OidcIdentityKey.Create(Issuer, Subject),
            OidcIdentityKey.Create(Issuer, "other-subject"));
        Assert.NotEqual(
            OidcIdentityKey.Create("https://example.org/ab", "c"),
            OidcIdentityKey.Create("https://example.org/a", "bc"));
    }

    [Fact]
    public void ValidatedIdentityTokenPayloadCanBeRead()
    {
        var token = MakeToken(Issuer, Subject);

        Assert.True(OidcIdentityKey.TryReadValidatedToken(token, out var issuer, out var subject));
        Assert.Equal(Issuer, issuer);
        Assert.Equal(Subject, subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("header.invalid-payload.signature")]
    public void InvalidIdentityTokenPayloadFailsClosed(string token)
    {
        Assert.False(OidcIdentityKey.TryReadValidatedToken(token, out _, out _));
    }

    [Fact]
    public void TokenIssuerMustBeHttps()
    {
        Assert.False(OidcIdentityKey.TryReadValidatedToken(
            MakeToken("http://accounts.example/realms/media", Subject), out _, out _));
    }

    [Fact]
    public void LegacyUsernameMappingDoesNotAuthenticateAnUnlinkedSubject()
    {
        var cfg = new OidConfig();
        var legacyUserId = Guid.NewGuid();
        cfg.CanonicalLinks["alice"] = legacyUserId;

        Assert.Equal(Guid.Empty, OidcIdentityLinks.Find(cfg, OidcIdentityKey.Create(Issuer, Subject)));
        Assert.Equal(legacyUserId, cfg.CanonicalLinks["alice"]);
    }

    [Fact]
    public void ExplicitVerifiedLinkMigratesLegacyMapping()
    {
        var cfg = new OidConfig();
        var id = Guid.NewGuid();
        var key = OidcIdentityKey.Create(Issuer, Subject);
        cfg.CanonicalLinks["alice"] = id;

        Assert.True(OidcIdentityLinks.TryLink(cfg, key, "alice", id));

        Assert.Equal(id, OidcIdentityLinks.Find(cfg, key));
        Assert.False(cfg.CanonicalLinks.ContainsKey("alice"));
        Assert.Equal("alice", cfg.SubjectNames[key]);
    }

    [Fact]
    public void SameSubjectCannotBeReassignedToAnotherUser()
    {
        var cfg = new OidConfig();
        var id = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var key = OidcIdentityKey.Create(Issuer, Subject);

        Assert.True(OidcIdentityLinks.TryLink(cfg, key, "alice", id));
        Assert.False(OidcIdentityLinks.TryLink(cfg, key, "bob", otherId));
        Assert.Equal(id, OidcIdentityLinks.Find(cfg, key));
        Assert.Equal("alice", cfg.SubjectNames[key]);
    }

    [Fact]
    public void UsernameCollisionDoesNotChangeSubjectOwnership()
    {
        var cfg = new OidConfig();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var aliceKey = OidcIdentityKey.Create(Issuer, "alice-subject");
        var bobKey = OidcIdentityKey.Create(Issuer, "bob-subject");

        Assert.True(OidcIdentityLinks.TryLink(cfg, aliceKey, "same-name", alice));
        Assert.True(OidcIdentityLinks.TryLink(cfg, bobKey, "same-name", bob));
        Assert.Equal(alice, OidcIdentityLinks.Find(cfg, aliceKey));
        Assert.Equal(bob, OidcIdentityLinks.Find(cfg, bobKey));
    }

    [Fact]
    public void RenamedLinkedIdentityPreservesAccountId()
    {
        var cfg = new OidConfig();
        var id = Guid.NewGuid();
        var key = OidcIdentityKey.Create(Issuer, Subject);

        Assert.True(OidcIdentityLinks.TryLink(cfg, key, "old-name", id));
        Assert.True(OidcIdentityLinks.TryLink(cfg, key, "new-name", id));

        Assert.Equal(id, OidcIdentityLinks.Find(cfg, key));
        Assert.Contains("new-name", OidcIdentityLinks.ListNames(cfg, id));
    }

    [Fact]
    public void UnlinkRemovesSubjectMappingWithoutChangingOtherUsers()
    {
        var cfg = new OidConfig();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var a = OidcIdentityKey.Create(Issuer, "a");
        var b = OidcIdentityKey.Create(Issuer, "b");
        Assert.True(OidcIdentityLinks.TryLink(cfg, a, "alice", alice));
        Assert.True(OidcIdentityLinks.TryLink(cfg, b, "bob", bob));

        Assert.Equal(OidcUnlinkResult.NotFound, OidcIdentityLinks.Unlink(cfg, alice, "bob"));
        Assert.Equal(OidcUnlinkResult.Removed, OidcIdentityLinks.Unlink(cfg, alice, "alice"));
        Assert.Equal(Guid.Empty, OidcIdentityLinks.Find(cfg, a));
        Assert.Equal(bob, OidcIdentityLinks.Find(cfg, b));
    }

    [Fact]
    public void AmbiguousDisplayNamesExposeDistinctUnlinkKeys()
    {
        var cfg = new OidConfig();
        var id = Guid.NewGuid();
        var a = OidcIdentityKey.Create(Issuer, "a");
        var b = OidcIdentityKey.Create(Issuer, "b");
        Assert.True(OidcIdentityLinks.TryLink(cfg, a, "same-name", id));
        Assert.True(OidcIdentityLinks.TryLink(cfg, b, "same-name", id));

        var names = OidcIdentityLinks.ListNames(cfg, id);
        Assert.Contains(a, names);
        Assert.Contains(b, names);
        Assert.Equal(OidcUnlinkResult.Ambiguous, OidcIdentityLinks.Unlink(cfg, id, "same-name"));
        Assert.Equal(OidcUnlinkResult.Removed, OidcIdentityLinks.Unlink(cfg, id, a));
        Assert.Equal(id, OidcIdentityLinks.Find(cfg, b));
    }

    [Fact]
    public void LegacyUnlinkRemovesOnlyAuthorizedMapping()
    {
        var cfg = new OidConfig();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        cfg.CanonicalLinks["alice"] = alice;
        cfg.CanonicalLinks["bob"] = bob;

        Assert.Equal(OidcUnlinkResult.NotFound, OidcIdentityLinks.Unlink(cfg, alice, "bob"));
        Assert.Equal(OidcUnlinkResult.Removed, OidcIdentityLinks.Unlink(cfg, alice, "alice"));
        Assert.Equal(bob, cfg.CanonicalLinks["bob"]);
    }

    [Fact]
    public void SubjectMappingsPersistAcrossXmlConfigurationRoundTrip()
    {
        var cfg = new OidConfig();
        var id = Guid.NewGuid();
        var key = OidcIdentityKey.Create(Issuer, Subject);
        cfg.CanonicalLinks["older-user"] = Guid.NewGuid();
        Assert.True(OidcIdentityLinks.TryLink(cfg, key, "current-user", id));

        var serializer = new XmlSerializer(typeof(OidConfig));
        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);
        using var reader = new StringReader(writer.ToString());
        var restored = Assert.IsType<OidConfig>(serializer.Deserialize(reader));

        Assert.Equal(id, OidcIdentityLinks.Find(restored, key));
        Assert.Equal("current-user", restored.SubjectNames[key]);
        Assert.True(restored.CanonicalLinks.ContainsKey("older-user"));
    }

    private static string MakeToken(string issuer, string subject)
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(new { iss = issuer, sub = subject })))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return header + "." + payload + ".signature";
    }
}
