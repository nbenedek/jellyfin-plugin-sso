using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.SSO_Auth.Config;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Immutable outcome of interpreting validated OIDC claims using configured role mappings.
/// This does not resolve identities or modify Jellyfin permissions.
/// </summary>
/// <param name="IsValid">Whether the user's claims satisfy the configured login roles.</param>
/// <param name="Username">Display username selected by the legacy claim precedence rules.</param>
/// <param name="IsAdministrator">Whether the administrator role was matched.</param>
/// <param name="Folders">Configured library identifiers derived from roles.</param>
/// <param name="EnableLiveTv">Whether Live TV access was requested.</param>
/// <param name="EnableLiveTvManagement">Whether Live TV management was requested.</param>
public sealed record OidcClaimsEvaluation(
    bool IsValid,
    string Username,
    bool IsAdministrator,
    string[] Folders,
    bool EnableLiveTv,
    bool EnableLiveTvManagement);

/// <summary>
/// Interprets provider claims without modifying Jellyfin's persisted authorization policy.
/// </summary>
public static class OidcClaimsEvaluator
{
    /// <summary>
    /// Evaluates claim-based login eligibility and optional permission mappings.
    /// Preserves the pre-Stage-7 callback interpretation semantics.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="sourceClaims">Claims from an already validated OIDC principal.</param>
    /// <returns>The computed login eligibility and requested authorization settings.</returns>
    public static OidcClaimsEvaluation Evaluate(OidConfig config, IEnumerable<Claim> sourceClaims)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(sourceClaims);
        var claims = sourceClaims.ToArray();
        var folders = !config.EnableFolderRoles && config.EnabledFolders != null
            ? new List<string>(config.EnabledFolders)
            : new List<string>();

        bool enableLiveTv = config.EnableLiveTv;
        bool enableLiveTvManagement = config.EnableLiveTvManagement;
        bool isAdmin = false;
        bool isValid = false;
        string username = null;

        foreach (var claim in claims)
        {
            if (claim.Type == (config.DefaultUsernameClaim?.Trim() ?? "preferred_username"))
            {
                username = claim.Value;
                if (config.Roles == null || config.Roles.Length == 0)
                {
                    isValid = true;
                }
            }

            // Role processing
            // The regex matches any "." not preceded by a "\": a.b.c will be split into a, b, and c, but a.b\.c will be split into a, b.c (after processing the escaped dots)
            // We have to first process the RoleClaim string
            string[] segments = string.IsNullOrEmpty(config.RoleClaim) ? Array.Empty<string>() : Regex.Split(config.RoleClaim.Trim(), "(?<!\\\\)\\.");

            if (segments.Any())
            {
                // Now we make sure that any escaped "."s ("\.") are replaced with "."
                segments = segments.Select(i => i.Replace("\\.", ".")).ToArray();

                if (claim.Type == segments[0])
                {
                    List<string> roles;
                    // If we are not using JSON values, just use the raw info from the claim value
                    if (segments.Length == 1)
                    {
                        roles = new List<string> { claim.Value };
                    }
                    else
                    {
                        roles = ParseNestedRoles(claim.Value, segments);
                    }

                    foreach (string role in roles)
                    {
                        // Check if allowed to login based on roles
                        if (config.Roles != null && config.Roles.Any())
                        {
                            foreach (string validRoles in config.Roles)
                            {
                                if (role.Equals(validRoles))
                                {
                                    isValid = true;
                                }
                            }
                        }

                        // Check if admin based on roles
                        if (config.AdminRoles != null && config.AdminRoles.Any())
                        {
                            foreach (string validAdminRoles in config.AdminRoles)
                            {
                                if (role.Equals(validAdminRoles))
                                {
                                    isAdmin = true;
                                }
                            }
                        }

                        // Get allowed folders from roles
                        if (config.EnableFolderRoles)
                        {
                            foreach (FolderRoleMap folderRoleMap in config.FolderRoleMapping ?? new List<FolderRoleMap>())
                            {
                                if (role.Equals(folderRoleMap.Role?.Trim()))
                                {
                                    folders.AddRange(folderRoleMap.Folders);
                                }
                            }
                        }

                        if (config.EnableLiveTvRoles)
                        {
                            // Check if allowed Live TV based on roles
                            if (config.LiveTvRoles != null && config.LiveTvRoles.Any())
                            {
                                foreach (string validLiveTvRoles in config.LiveTvRoles)
                                {
                                    if (role.Equals(validLiveTvRoles))
                                    {
                                        enableLiveTv = true;
                                    }
                                }
                            }

                            // Check if allowed Live TV management based on roles
                            if (config.LiveTvManagementRoles != null && config.LiveTvManagementRoles.Any())
                            {
                                foreach (string validLiveTvManagementRoles in config.LiveTvManagementRoles)
                                {
                                    if (role.Equals(validLiveTvManagementRoles))
                                    {
                                        enableLiveTvManagement = true;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // If the provider doesn't support the preferred username claim, then use the sub claim
        if (!isValid)
        {
            foreach (var claim in claims)
            {
                if (claim.Type == "sub")
                {
                    username = claim.Value;
                    if (config.Roles == null || config.Roles.Length == 0)
                    {
                        isValid = true;
                    }
                }
            }
        }

        return new OidcClaimsEvaluation(
            isValid,
            username,
            isAdmin,
            folders.ToArray(),
            enableLiveTv,
            enableLiveTvManagement);
    }

    private static List<string> ParseNestedRoles(string claimValue, string[] segments)
    {
        try
        {
            var json = JsonConvert.DeserializeObject<IDictionary<string, object>>(claimValue);
            if (json is null)
            {
                return new List<string>();
            }

            for (int i = 1; i < segments.Length - 1; i++)
            {
                if (!json.TryGetValue(segments[i], out var nextToken) || nextToken is not JObject nextObject)
                {
                    return new List<string>();
                }

                json = nextObject.ToObject<IDictionary<string, object>>();
                if (json is null)
                {
                    return new List<string>();
                }
            }

            if (!json.TryGetValue(segments[^1], out var rolesToken) || rolesToken is not JArray rolesArray)
            {
                return new List<string>();
            }

            return rolesArray.ToObject<List<string>>()?.Where(role => role is not null).ToList() ?? new List<string>();
        }
        catch (JsonException)
        {
            // Malformed provider role claims must not turn a login into HTTP 500,
            // and must never grant administrator or folder permissions.
            return new List<string>();
        }
    }
}
