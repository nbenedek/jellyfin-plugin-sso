using System;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Ensures that provider edits cannot create, erase or transfer identity links.
/// Only the dedicated elevated linking routes may mutate those dictionaries.
/// </summary>
public static class OidcProviderConfigurationSafety
{
    /// <summary>
    /// Retains server-owned identity associations during provider settings updates.
    /// </summary>
    /// <param name="current">Previously stored provider, or null for a new provider.</param>
    /// <param name="replacement">Administrator-supplied provider settings.</param>
    public static void PreserveLinks(OidConfig current, OidConfig replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);

        if (current is null)
        {
            // Even elevated provider creation must not bypass the explicit linking API.
            replacement.CanonicalLinks = new SerializableDictionary<string, Guid>();
            replacement.SubjectLinks = new SerializableDictionary<string, Guid>();
            replacement.SubjectNames = new SerializableDictionary<string, string>();
            return;
        }

        replacement.CanonicalLinks = current.CanonicalLinks;
        replacement.SubjectLinks = current.SubjectLinks;
        replacement.SubjectNames = current.SubjectNames;
    }
}
