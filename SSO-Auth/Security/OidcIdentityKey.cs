using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.SSO_Auth.Config;

namespace Jellyfin.Plugin.SSO_Auth.Security;

/// <summary>
/// Result of removing an OIDC identity link.
/// </summary>
public enum OidcUnlinkResult
{
    /// <summary>The requested link does not exist.</summary>
    NotFound = 0,

    /// <summary>The link was removed.</summary>
    Removed = 1,

    /// <summary>More than one identity has the same display name.</summary>
    Ambiguous = 2
}

/// <summary>
/// Derives an opaque, stable key from a validated OIDC issuer and subject.
/// </summary>
public static class OidcIdentityKey
{
    /// <summary>
    /// Creates a key from the exact issuer and subject strings.
    /// </summary>
    /// <param name="issuer">Validated OIDC issuer.</param>
    /// <param name="subject">Validated OIDC subject.</param>
    /// <returns>An opaque, URL-safe identity key.</returns>
    public static string Create(string issuer, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var bytes = Encoding.UTF8.GetBytes($"{issuer.Length}:{issuer}{subject.Length}:{subject}");
        return "oidc-v1-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    /// <summary>
    /// Extracts issuer and subject from an ID token that has already been validated by the OIDC client.
    /// This method does not validate signatures or token claims; callers MUST check OidcClient's
    /// ProcessResponseAsync result before calling it.
    /// </summary>
    /// <param name="validatedIdentityToken">Identity token from a successful OIDC response.</param>
    /// <param name="issuer">The exact issuer from the validated token.</param>
    /// <param name="subject">The exact subject from the validated token.</param>
    /// <returns>Whether the token contains a usable issuer and subject.</returns>
    public static bool TryReadValidatedToken(string validatedIdentityToken, out string issuer, out string subject)
    {
        issuer = null;
        subject = null;

        if (string.IsNullOrWhiteSpace(validatedIdentityToken) || validatedIdentityToken.Length > 65536)
        {
            return false;
        }

        var parts = validatedIdentityToken.Split('.');
        if (parts.Length != 3 || parts[1].Length == 0)
        {
            return false;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            var decoded = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(decoded, new JsonDocumentOptions { MaxDepth = 16 });

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("iss", out var issuerElement)
                || issuerElement.ValueKind != JsonValueKind.String
                || !document.RootElement.TryGetProperty("sub", out var subjectElement)
                || subjectElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            issuer = issuerElement.GetString();
            subject = subjectElement.GetString();
            if (string.IsNullOrWhiteSpace(issuer)
                || string.IsNullOrWhiteSpace(subject)
                || !Uri.TryCreate(issuer, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || !string.IsNullOrEmpty(uri.Query)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                issuer = null;
                subject = null;
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Manages issuer-and-subject links without granting any rights from legacy username mappings.
/// Callers are responsible for authorizing the target Jellyfin account and persisting changes.
/// </summary>
public static class OidcIdentityLinks
{
    /// <summary>
    /// Looks up an existing validated subject mapping.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="identityKey">Opaque issuer-and-subject key.</param>
    /// <returns>The mapped user ID, or an empty GUID.</returns>
    public static Guid Find(OidConfig config, string identityKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.SubjectLinks.TryGetValue(identityKey, out var id) ? id : Guid.Empty;
    }

    /// <summary>
    /// Links a validated identity to an already-authorized Jellyfin account.
    /// An identity may not be reassigned to a different account.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="identityKey">Opaque issuer-and-subject key.</param>
    /// <param name="username">Display username only.</param>
    /// <param name="userId">Jellyfin user ID verified by the caller.</param>
    /// <returns>Whether there was no conflicting identity owner.</returns>
    public static bool TryLink(OidConfig config, string identityKey, string username, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("A real Jellyfin user is required.", nameof(userId));
        }

        if (config.SubjectLinks.TryGetValue(identityKey, out var existing) && existing != userId)
        {
            return false;
        }

        config.SubjectLinks[identityKey] = userId;
        config.SubjectNames[identityKey] = username;

        // Migrate only the explicitly verified user's legacy mapping.
        if (config.CanonicalLinks.TryGetValue(username, out var legacyId) && legacyId == userId)
        {
            config.CanonicalLinks.Remove(username);
        }

        return true;
    }

    /// <summary>
    /// Lists linked display names and un-migrated legacy mappings for a Jellyfin user.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="userId">Jellyfin user ID.</param>
    /// <returns>Link names that the existing self-service unlink route accepts.</returns>
    public static string[] ListNames(OidConfig config, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(config);

        var subjectNames = config.SubjectLinks
            .Where(link => link.Value == userId)
            .Select(link => config.SubjectNames.TryGetValue(link.Key, out var name) ? name : link.Key)
            .ToList();
        var duplicates = subjectNames
            .GroupBy(name => name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

        var visible = config.SubjectLinks
            .Where(link => link.Value == userId)
            .Select(link =>
            {
                var name = config.SubjectNames.TryGetValue(link.Key, out var display) ? display : link.Key;
                return duplicates.Contains(name) ? link.Key : name;
            })
            .ToList();

        visible.AddRange(config.CanonicalLinks.Where(link => link.Value == userId).Select(link => link.Key));
        return visible.Distinct(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Unlinks only mappings owned by the specified Jellyfin user.
    /// </summary>
    /// <param name="config">Provider configuration.</param>
    /// <param name="userId">Authorized Jellyfin user ID.</param>
    /// <param name="nameOrKey">Link name or opaque key from the self-service list.</param>
    /// <returns>Whether a mapping was removed, missing, or ambiguous.</returns>
    public static OidcUnlinkResult Unlink(OidConfig config, Guid userId, string nameOrKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(nameOrKey))
        {
            return OidcUnlinkResult.NotFound;
        }

        var matches = config.SubjectLinks
            .Where(link => link.Value == userId
                && (string.Equals(link.Key, nameOrKey, StringComparison.Ordinal)
                    || (config.SubjectNames.TryGetValue(link.Key, out var name)
                        && string.Equals(name, nameOrKey, StringComparison.Ordinal))))
            .Select(link => link.Key)
            .ToArray();

        if (matches.Length > 1)
        {
            return OidcUnlinkResult.Ambiguous;
        }

        if (matches.Length == 1)
        {
            config.SubjectLinks.Remove(matches[0]);
            config.SubjectNames.Remove(matches[0]);
            return OidcUnlinkResult.Removed;
        }

        if (config.CanonicalLinks.TryGetValue(nameOrKey, out var existing) && existing == userId)
        {
            config.CanonicalLinks.Remove(nameOrKey);
            return OidcUnlinkResult.Removed;
        }

        return OidcUnlinkResult.NotFound;
    }
}
