using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Migration;
using Jellyfin.Plugin.SSO_Auth.Security;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class NativeMigrationTests
{
    private const string Issuer = "https://accounts.example/realms/applications";
    private const string Provider = "testcorn";
    private const string Username = "alice@example.com";
    private const string Subject = "bc90474d-f6e9-448e-85a5-a12ad2892af6";
    private static readonly Guid JellyfinUserId = Guid.Parse("f1ea4312-9af6-4bd4-8796-e51849fe4ecf");

    [Fact]
    public void SerializedConfigurationPreservesEveryNormalPluginSetting()
    {
        var cfg = CreateConfiguration();
        MigrationEngine.VerifyRoundTrip(cfg);
        var xml = MigrationEngine.Serialize(cfg);
        var restored = MigrationEngine.Deserialize(xml);
        var provider = restored.OidConfigs[Provider];

        Assert.Equal("redacted-secret-preserve", provider.OidSecret);
        Assert.Equal("client-id-preserve", provider.OidClientId);
        Assert.Equal("preferred_username", provider.DefaultUsernameClaim);
        Assert.Equal("roles", provider.RoleClaim);
        Assert.True(provider.EnableAuthorization);
        Assert.Equal(new[] { "Movies", "Series" }, provider.EnabledFolders);
        Assert.Equal(new[] { "admin" }, provider.AdminRoles);
        Assert.Equal(new[] { "group-member" }, provider.Roles);
        Assert.Single(provider.FolderRoleMapping);
        Assert.Equal("english-speaking-jellyfin-user", provider.FolderRoleMapping[0].Role);
        Assert.Equal(new[] { "Movies", "Series" }, provider.FolderRoleMapping[0].Folders);
        Assert.Equal("openid", provider.OidScopes[0]);
        Assert.Equal(JellyfinUserId, provider.CanonicalLinks[Username]);
    }

    [Fact]
    public void PreviewLeavesConfigurationUnmodified()
    {
        var cfg = CreateConfiguration();
        var xml = MigrationEngine.Serialize(cfg);
        var plan = MigrationEngine.Plan(cfg, xml, Export(Username, Subject), Provider, Issuer, "username");

        Assert.Equal("ready", Assert.Single(plan.Rows).Status);
        Assert.Equal(64, plan.ApprovalHash.Length);
        Assert.Equal(JellyfinUserId, cfg.OidConfigs[Provider].CanonicalLinks[Username]);
        Assert.Empty(cfg.OidConfigs[Provider].SubjectLinks);
        Assert.Equal(xml, MigrationEngine.Serialize(cfg));
    }

    [Fact]
    public void NativeMigrationPreservesConfigurationAndUserId()
    {
        var cfg = CreateConfiguration();
        var xml = MigrationEngine.Serialize(cfg);
        var plan = MigrationEngine.Plan(cfg, xml, Export(Username, Subject), Provider, Issuer, "username");

        Assert.Equal(1, MigrationEngine.ApplyInMemory(cfg, plan, Provider));
        MigrationEngine.VerifyRoundTrip(cfg);
        var migrated = MigrationEngine.Deserialize(MigrationEngine.Serialize(cfg));
        var oid = migrated.OidConfigs[Provider];
        var key = OidcIdentityKey.Create(Issuer, Subject);

        Assert.Equal(JellyfinUserId, oid.SubjectLinks[key]);
        Assert.Equal(Username, oid.SubjectNames[key]);
        Assert.False(oid.CanonicalLinks.ContainsKey(Username));
        Assert.Equal("redacted-secret-preserve", oid.OidSecret);
        Assert.Equal("client-id-preserve", oid.OidClientId);
        Assert.Equal("english-speaking-jellyfin-user", oid.FolderRoleMapping[0].Role);
        Assert.Equal(new[] { "Movies", "Series" }, oid.EnabledFolders);
        Assert.True(oid.EnableAuthorization);

        // Replanning from the already-migrated XML is a no-op.
        var postXml = MigrationEngine.Serialize(migrated);
        var postPlan = MigrationEngine.Plan(migrated, postXml, Export(Username, Subject),
            Provider, Issuer, "username");
        Assert.Empty(postPlan.Rows);
    }

    [Fact]
    public void SingleAccountDryRunAndApplyLeavesOtherLegacyUsersUntouched()
    {
        var cfg = CreateConfiguration();
        var secondJellyfinId = Guid.NewGuid();
        cfg.OidConfigs[Provider].CanonicalLinks["bob@example.com"] = secondJellyfinId;
        var export = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { id = Subject, username = Username, enabled = true },
            new { id = Guid.NewGuid().ToString(), username = "bob@example.com", enabled = true }
        });
        var xml = MigrationEngine.Serialize(cfg);
        var singlePlan = MigrationEngine.Plan(cfg, xml, export, Provider, Issuer,
            "username", onlyUsername: Username);
        var bulkPlan = MigrationEngine.Plan(cfg, xml, export, Provider, Issuer, "username");

        Assert.Single(singlePlan.Rows);
        Assert.Equal("ready", singlePlan.Rows[0].Status);
        Assert.NotEqual(singlePlan.ApprovalHash, bulkPlan.ApprovalHash);
        Assert.Equal(1, MigrationEngine.ApplyInMemory(cfg, singlePlan, Provider));

        var restored = MigrationEngine.Deserialize(MigrationEngine.Serialize(cfg));
        Assert.Equal(secondJellyfinId, restored.OidConfigs[Provider].CanonicalLinks["bob@example.com"]);
        Assert.False(restored.OidConfigs[Provider].CanonicalLinks.ContainsKey(Username));
        Assert.Equal(JellyfinUserId, restored.OidConfigs[Provider].SubjectLinks[
            OidcIdentityKey.Create(Issuer, Subject)]);
    }

    [Fact]
    public void AlreadyLinkedIdentityDoesNotChangeExistingMapping()
    {
        var cfg = CreateConfiguration();
        var oid = cfg.OidConfigs[Provider];
        var key = OidcIdentityKey.Create(Issuer, Subject);
        oid.SubjectLinks[key] = JellyfinUserId;
        var xml = MigrationEngine.Serialize(cfg);
        var plan = MigrationEngine.Plan(cfg, xml, Export(Username, Subject), Provider, Issuer, "username");

        Assert.Equal("already", Assert.Single(plan.Rows).Status);
        Assert.Equal(0, MigrationEngine.ApplyInMemory(cfg, plan, Provider));
        Assert.Equal(JellyfinUserId, oid.SubjectLinks[key]);
    }

    [Fact]
    public void ConflictingSubjectOwnerIsBlocked()
    {
        var cfg = CreateConfiguration();
        var key = OidcIdentityKey.Create(Issuer, Subject);
        cfg.OidConfigs[Provider].SubjectLinks[key] = Guid.NewGuid();

        var plan = Plan(cfg, Export(Username, Subject));
        Assert.Equal("blocked", Assert.Single(plan.Rows).Status);
        Assert.Equal("oidc_identity_linked_to_another_account", plan.Rows[0].Reason);
        Assert.Equal(0, MigrationEngine.ApplyInMemory(cfg, plan, Provider));
    }

    [Fact]
    public void ExistingOtherSubjectForSameAccountIsBlocked()
    {
        var cfg = CreateConfiguration();
        cfg.OidConfigs[Provider].SubjectLinks[
            OidcIdentityKey.Create(Issuer, Guid.NewGuid().ToString())] = JellyfinUserId;
        var plan = Plan(cfg, Export(Username, Subject));

        Assert.Equal("jellyfin_account_has_other_subject_links", Assert.Single(plan.Rows).Reason);
    }

    [Fact]
    public void DuplicateUsernameInDirectoryIsBlocked()
    {
        var cfg = CreateConfiguration();
        var export = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { id = Subject, username = Username, enabled = true },
            new { id = Guid.NewGuid().ToString(), username = Username, enabled = true }
        });
        Assert.Equal("ambiguous_directory_name", Assert.Single(Plan(cfg, export).Rows).Reason);
    }

    [Fact]
    public void DisabledKeycloakUserIsBlocked()
    {
        var cfg = CreateConfiguration();
        Assert.Equal("keycloak_user_disabled",
            Assert.Single(Plan(cfg, Export(Username, Subject, enabled: false)).Rows).Reason);
    }

    [Fact]
    public void NoUsernameMatchRemainsUnmatched()
    {
        var cfg = CreateConfiguration();
        var plan = Plan(cfg, Export("another@example.com", Subject));
        Assert.Equal("unmatched", Assert.Single(plan.Rows).Status);
    }

    [Fact]
    public void WrongIssuerAndProviderCannotMigrate()
    {
        var cfg = CreateConfiguration();
        var xml = MigrationEngine.Serialize(cfg);
        var export = Export(Username, Subject);
        Assert.Throws<InvalidDataException>(
            () => MigrationEngine.Plan(cfg, xml, export, Provider, Issuer + "/other", "username"));
        Assert.Throws<InvalidDataException>(
            () => MigrationEngine.Plan(cfg, xml, export, "other-provider", Issuer, "username"));
    }

    [Fact]
    public void ChangedDirectoryOrConfigChangesApprovalHash()
    {
        var cfg = CreateConfiguration();
        var xml = MigrationEngine.Serialize(cfg);
        var original = MigrationEngine.Plan(cfg, xml, Export(Username, Subject), Provider, Issuer, "username");
        var changedExport = MigrationEngine.Plan(cfg, xml,
            Export(Username, Guid.NewGuid().ToString()), Provider, Issuer, "username");
        Assert.NotEqual(original.ApprovalHash, changedExport.ApprovalHash);

        var changedConfig = xml.Concat(new byte[] { (byte)' ' }).ToArray();
        var alteredPlan = MigrationEngine.Plan(cfg, changedConfig, Export(Username, Subject),
            Provider, Issuer, "username");
        Assert.NotEqual(original.ApprovalHash, alteredPlan.ApprovalHash);
    }

    [Fact]
    public void UnknownConfigurationFieldsAreRejectedBeforeRewrite()
    {
        var xml = Encoding.UTF8.GetString(MigrationEngine.Serialize(CreateConfiguration()));
        var modified = xml.Replace("</PluginConfiguration>",
            "<UnknownSecretSetting>must_not_be_lost</UnknownSecretSetting></PluginConfiguration>",
            StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => MigrationEngine.Deserialize(Encoding.UTF8.GetBytes(modified)));
    }

    [Fact]
    public void XmlWithExternalEntityIsRejected()
    {
        var xml = Encoding.UTF8.GetString(MigrationEngine.Serialize(CreateConfiguration()));
        var malicious = "<!DOCTYPE PluginConfiguration [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" + xml;
        Assert.ThrowsAny<Exception>(() => MigrationEngine.Deserialize(Encoding.UTF8.GetBytes(malicious)));
    }

    [Fact]
    public void ApprovalHashIsStableForIdenticalInputs()
    {
        var cfg = CreateConfiguration();
        var xml = MigrationEngine.Serialize(cfg);
        var export = Export(Username, Subject);
        var plan1 = MigrationEngine.Plan(cfg, xml, export, Provider, Issuer, "username");
        var plan2 = MigrationEngine.Plan(cfg, xml, export, Provider, Issuer, "username");
        Assert.Equal(plan1.ApprovalHash, plan2.ApprovalHash);
    }

    private static MigrationPlan Plan(PluginConfiguration config, byte[] export)
        => MigrationEngine.Plan(config, MigrationEngine.Serialize(config), export,
            Provider, Issuer, "username");

    private static byte[] Export(string name, string subject, bool enabled = true)
        => JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { id = subject, username = name, enabled }
        });

    private static PluginConfiguration CreateConfiguration()
    {
        var config = new PluginConfiguration();
        var provider = new OidConfig
        {
            Enabled = true,
            OidEndpoint = Issuer,
            OidClientId = "client-id-preserve",
            OidSecret = "redacted-secret-preserve",
            DefaultUsernameClaim = "preferred_username",
            RoleClaim = "roles",
            EnableAuthorization = true,
            EnabledFolders = new[] { "Movies", "Series" },
            OidScopes = new[] { "openid", "profile" },
            AdminRoles = new[] { "admin" },
            Roles = new[] { "group-member" },
            FolderRoleMapping = new List<FolderRoleMap>
            {
                new()
                {
                    Role = "english-speaking-jellyfin-user",
                    Folders = new List<string> { "Movies", "Series" }
                }
            }
        };
        provider.CanonicalLinks[Username] = JellyfinUserId;
        config.OidConfigs[Provider] = provider;
        return config;
    }
}
