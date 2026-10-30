using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.SSO_Auth.Security;
using MediaBrowser.Model.Users;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

public sealed class OidcAuthorizationPolicyTests
{
    [Fact]
    public void DisabledAuthorizationLeavesAllPermissionsAndPreferencesUnchanged()
    {
        var existingFolder = Guid.NewGuid();
        var policy = new UserPolicy
        {
            IsAdministrator = true,
            EnableAllFolders = false,
            EnabledFolders = new[] { existingFolder },
            EnableLiveTvAccess = false,
            EnableLiveTvManagement = true,
            EnableContentDownloading = false,
            EnableLyricManagement = true
        };

        var changed = OidcAuthorizationPolicy.Apply(
            policy,
            false,
            false,
            true,
            new[] { Guid.NewGuid().ToString() },
            true,
            false);

        Assert.False(changed);
        Assert.True(policy.IsAdministrator);
        Assert.False(policy.EnableAllFolders);
        Assert.Equal(new[] { existingFolder }, policy.EnabledFolders);
        Assert.False(policy.EnableLiveTvAccess);
        Assert.True(policy.EnableLiveTvManagement);
        Assert.False(policy.EnableContentDownloading);
        Assert.True(policy.EnableLyricManagement);
    }

    [Fact]
    public void EnabledAuthorizationPersistsRestrictedLibrarySelection()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var policy = new UserPolicy
        {
            EnableAllFolders = true,
            IsAdministrator = true,
            EnableContentDownloading = false
        };

        var changed = OidcAuthorizationPolicy.Apply(
            policy,
            true,
            false,
            false,
            new[] { first.ToString(), second.ToString(), first.ToString(), "invalid-id", Guid.Empty.ToString() },
            false,
            false);

        Assert.True(changed);
        Assert.False(policy.EnableAllFolders);
        Assert.Equal(new[] { first, second }, policy.EnabledFolders);
        Assert.False(policy.IsAdministrator);
        Assert.False(policy.EnableLiveTvAccess);
        Assert.False(policy.EnableLiveTvManagement);
        Assert.False(policy.EnableContentDownloading);
    }

    [Fact]
    public void EnabledAuthorizationCanSwitchToAllLibrariesExplicitly()
    {
        var policy = new UserPolicy
        {
            EnableAllFolders = false,
            EnabledFolders = new[] { Guid.NewGuid() },
            EnableLiveTvAccess = false,
            EnableLiveTvManagement = false
        };

        Assert.True(OidcAuthorizationPolicy.Apply(
            policy,
            true,
            true,
            true,
            Array.Empty<string>(),
            true,
            true));

        Assert.True(policy.EnableAllFolders);
        Assert.Empty(policy.EnabledFolders);
        Assert.True(policy.IsAdministrator);
        Assert.True(policy.EnableLiveTvAccess);
        Assert.True(policy.EnableLiveTvManagement);
    }

    [Fact]
    public void EmptyAllowedLibrariesDoNotBroadenAccess()
    {
        var policy = new UserPolicy();

        Assert.True(OidcAuthorizationPolicy.Apply(
            policy,
            true,
            false,
            false,
            null!,
            false,
            false));

        Assert.False(policy.EnableAllFolders);
        Assert.Empty(policy.EnabledFolders);
        Assert.False(policy.IsAdministrator);
    }

    [Fact]
    public void PersistencePreparationPreservesUnrelatedPolicyData()
    {
        var userId = Guid.NewGuid();
        var schedule = new AccessSchedule(DynamicDayOfWeek.Weekday, 8, 17, userId);
        var policy = new UserPolicy
        {
            EnableAllFolders = false,
            EnabledFolders = new[] { Guid.NewGuid() },
            AccessSchedules = new[] { schedule },
            EnableLyricManagement = false
        };

        var originalFolder = policy.EnabledFolders.Single();

        OidcAuthorizationPolicy.PrepareForPersistence(policy, userId, true);

        Assert.True(policy.EnableLyricManagement);
        Assert.False(policy.EnableAllFolders);
        Assert.Equal(originalFolder, policy.EnabledFolders.Single());
        var detached = Assert.Single(policy.AccessSchedules);
        Assert.NotSame(schedule, detached);
        Assert.Equal(userId, detached.UserId);
        Assert.Equal(DynamicDayOfWeek.Weekday, detached.DayOfWeek);
        Assert.Equal(8, detached.StartHour);
        Assert.Equal(17, detached.EndHour);
        Assert.Equal(0, detached.Id);
    }

    [Fact]
    public void PersistencePreparationHandlesNoAccessSchedules()
    {
        var policy = new UserPolicy { AccessSchedules = null! };

        OidcAuthorizationPolicy.PrepareForPersistence(policy, Guid.NewGuid(), false);

        Assert.NotNull(policy.AccessSchedules);
        Assert.Empty(policy.AccessSchedules);
        Assert.False(policy.EnableLyricManagement);
    }

    [Fact]
    public void PersistencePreparationRejectsMissingUserId()
    {
        Assert.Throws<ArgumentException>(() =>
            OidcAuthorizationPolicy.PrepareForPersistence(new UserPolicy(), Guid.Empty, false));
    }
}
