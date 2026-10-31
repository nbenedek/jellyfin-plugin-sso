using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Applies provider-managed permissions to a Jellyfin user policy.
/// </summary>
public static class OidcAuthorizationPolicy
{
    /// <summary>
    /// Updates only provider-managed permissions. When authorization management is disabled,
    /// no policy permissions or preferences are modified.
    /// </summary>
    /// <param name="policy">Existing Jellyfin user policy.</param>
    /// <param name="enableAuthorization">Whether provider authorization management is enabled.</param>
    /// <param name="isAdministrator">Whether administrator access is granted.</param>
    /// <param name="enableAllFolders">Whether the user can access every library.</param>
    /// <param name="enabledFolders">Library identifiers granted by the provider.</param>
    /// <param name="enableLiveTv">Whether Live TV access is granted.</param>
    /// <param name="enableLiveTvManagement">Whether Live TV management is granted.</param>
    /// <returns>Whether authorization management was applied.</returns>
    public static bool Apply(
        UserPolicy policy,
        bool enableAuthorization,
        bool isAdministrator,
        bool enableAllFolders,
        IEnumerable<string> enabledFolders,
        bool enableLiveTv,
        bool enableLiveTvManagement)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!enableAuthorization)
        {
            return false;
        }

        policy.IsAdministrator = isAdministrator;
        policy.EnableAllFolders = enableAllFolders;
        policy.EnabledFolders = enableAllFolders
            ? Array.Empty<Guid>()
            : (enabledFolders ?? Array.Empty<string>())
                .Select(folder => Guid.TryParse(folder, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToArray();
        policy.EnableLiveTvAccess = enableLiveTv;
        policy.EnableLiveTvManagement = enableLiveTvManagement;
        return true;
    }

    /// <summary>
    /// Preserves policy data that is not fully represented in the Jellyfin user DTO.
    /// Access schedules are copied as new entities for the policy persistence API.
    /// </summary>
    /// <param name="policy">Policy to persist.</param>
    /// <param name="userId">The associated user identifier.</param>
    /// <param name="enableLyricManagement">The existing lyric management permission.</param>
    public static void PrepareForPersistence(UserPolicy policy, Guid userId, bool enableLyricManagement)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User ID cannot be empty.", nameof(userId));
        }

        policy.EnableLyricManagement = enableLyricManagement;
        policy.AccessSchedules = (policy.AccessSchedules ?? Array.Empty<AccessSchedule>())
            .Select(schedule => new AccessSchedule(
                schedule.DayOfWeek,
                schedule.StartHour,
                schedule.EndHour,
                userId))
            .ToArray();
    }
}
