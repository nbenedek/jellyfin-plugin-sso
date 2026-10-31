using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.SSO_Auth.Config;
using Jellyfin.Plugin.SSO_Auth.Security;

namespace Jellyfin.Plugin.SSO_Auth.Migration;

/// <summary>A directory entry from an authenticated Keycloak administrator export.</summary>
public sealed record DirectoryUser(string? Id, string? Username, string? Email, bool Enabled);

/// <summary>A proposed, never automatically authorized, legacy association.</summary>
public sealed record MigrationRow(
    string Username,
    Guid JellyfinUserId,
    string Status,
    string Reason,
    string? OidcSubject,
    string? IdentityKey);

/// <summary>A deterministic review plan bound to the configuration and directory bytes.</summary>
public sealed record MigrationPlan(IReadOnlyList<MigrationRow> Rows, string ApprovalHash);

/// <summary>Safe, model-native configuration loading and migration planning.</summary>
public static class MigrationEngine
{
    /// <summary>Strictly loads a saved Jellyfin SSO configuration using its real .NET types.</summary>
    public static PluginConfiguration Deserialize(byte[] xml)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var events = new XmlDeserializationEvents
        {
            OnUnknownElement = (_, e) => throw new InvalidDataException(
                "Unknown SSO configuration element: " + e.Element.Name),
            OnUnknownAttribute = (_, e) => throw new InvalidDataException(
                "Unknown SSO configuration attribute: " + e.Attr.Name),
            OnUnknownNode = (_, e) => throw new InvalidDataException(
                "Unknown SSO configuration node: " + e.Name),
        };
        using var stream = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 20_000_000,
            IgnoreWhitespace = true
        });
        var result = serializer.Deserialize(reader, events) as PluginConfiguration;
        return result ?? throw new InvalidDataException("XML was not a Jellyfin SSO plugin configuration.");
    }

    /// <summary>Serializes with XmlSerializer, never using an external XML writer.</summary>
    public static byte[] Serialize(PluginConfiguration config)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var output = new MemoryStream();
        using (var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            CloseOutput = false
        }))
        {
            serializer.Serialize(writer, config);
        }

        return output.ToArray();
    }

    /// <summary>Fails closed if any recognized .NET setting changes during XML serialization.</summary>
    public static void VerifyRoundTrip(PluginConfiguration config)
    {
        var restored = Deserialize(Serialize(config));
        var originalJson = JsonNode.Parse(JsonSerializer.Serialize(config));
        var restoredJson = JsonNode.Parse(JsonSerializer.Serialize(restored));
        if (!JsonNode.DeepEquals(originalJson, restoredJson))
        {
            throw new InvalidDataException("SSO configuration changed in its native XML serialization round trip.");
        }
    }

    /// <summary>Parses a minimal Keycloak user-directory export, never credentials or a full realm export.</summary>
    public static IReadOnlyList<DirectoryUser> ParseUsers(byte[] directoryJson)
    {
        using var doc = JsonDocument.Parse(directoryJson);
        var root = doc.RootElement;
        var items = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("users", out var users)
                ? users
                : throw new InvalidDataException("Expected a JSON array or realm object with a users array.");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
        {
            throw new InvalidDataException("Keycloak user export must be a nonempty JSON array.");
        }

        var result = new List<DirectoryUser>();
        foreach (var user in items.EnumerateArray())
        {
            if (user.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Invalid user export entry.");
            }

            static string? GetString(JsonElement element, string key)
                => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
            bool enabled = user.TryGetProperty("enabled", out var enabledElement) &&
                enabledElement.ValueKind == JsonValueKind.True;
            result.Add(new DirectoryUser(
                GetString(user, "id"),
                GetString(user, "username"),
                GetString(user, "email"),
                enabled));
        }

        return result;
    }

    /// <summary>Computes a non-mutating plan, rejecting collisions and ambiguous matches.</summary>
    public static MigrationPlan Plan(
        PluginConfiguration config,
        byte[] configBytes,
        byte[] exportBytes,
        string provider,
        string issuer,
        string matchField,
        string? onlyUsername = null)
    {
        if (!OidcIdentityKey.IsValidIssuer(issuer) || string.IsNullOrWhiteSpace(provider))
        {
            throw new InvalidDataException("Invalid provider or HTTPS issuer.");
        }

        if (matchField is not ("username" or "email"))
        {
            throw new InvalidDataException("Match field must be username or email.");
        }

        if (!config.OidConfigs.TryGetValue(provider, out var oidc) || !oidc.Enabled ||
            !string.Equals(oidc.OidEndpoint?.Trim(), issuer, StringComparison.Ordinal))
        {
            throw new InvalidDataException("OIDC provider missing, disabled, or issuer does not match exactly.");
        }

        var directory = ParseUsers(exportBytes);
        var byName = directory
            .Where(user => !string.IsNullOrWhiteSpace(matchField == "email" ? user.Email : user.Username))
            .GroupBy(user => matchField == "email" ? user.Email! : user.Username!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);

        var subjects = directory.Where(user => !string.IsNullOrWhiteSpace(user.Id))
            .GroupBy(user => user.Id!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var legacyUserCounts = oidc.CanonicalLinks.Values
            .GroupBy(userId => userId)
            .ToDictionary(g => g.Key, g => g.Count());

        var modernOwners = oidc.SubjectLinks
            .GroupBy(link => link.Value)
            .ToDictionary(g => g.Key, g => g.Select(link => link.Key).ToHashSet(StringComparer.Ordinal));

        var rows = new List<MigrationRow>();
        foreach (var legacy in oidc.CanonicalLinks.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (onlyUsername is not null &&
                !string.Equals(legacy.Key, onlyUsername, StringComparison.Ordinal))
            {
                continue;
            }

            var name = legacy.Key;
            var jellyfinId = legacy.Value;
            var status = "blocked";
            var reason = "unspecified_conflict";
            string? subject = null;
            string? key = null;
            if (jellyfinId == Guid.Empty)
            {
                reason = "invalid_jellyfin_user";
            }
            else if (legacyUserCounts[jellyfinId] != 1)
            {
                reason = "multiple_legacy_names_share_jellyfin_account";
            }
            else if (!byName.TryGetValue(name, out var matches))
            {
                status = "unmatched";
                reason = "no_exact_directory_match";
            }
            else if (matches.Length != 1)
            {
                reason = "ambiguous_directory_name";
            }
            else
            {
                var candidate = matches[0];
                subject = candidate.Id;
                if (!Guid.TryParse(subject, out var parsed) || parsed == Guid.Empty)
                {
                    reason = "invalid_keycloak_subject_id";
                }
                else if (subjects[subject!] != 1)
                {
                    reason = "duplicate_keycloak_subject";
                }
                else if (!candidate.Enabled)
                {
                    reason = "keycloak_user_disabled";
                }
                else
                {
                    key = OidcIdentityKey.Create(issuer, subject!);
                    if (oidc.SubjectLinks.TryGetValue(key, out var existing))
                    {
                        if (existing != jellyfinId)
                        {
                            reason = "oidc_identity_linked_to_another_account";
                        }
                        else
                        {
                            status = "already";
                            reason = "existing_subject_link_matches";
                        }
                    }
                    else if (modernOwners.TryGetValue(jellyfinId, out var oldKeys) && oldKeys.Count > 0)
                    {
                        reason = "jellyfin_account_has_other_subject_links";
                    }
                    else
                    {
                        status = "ready";
                        reason = "unique_exact_legacy_and_directory_match";
                    }
                }
            }

            rows.Add(new MigrationRow(name, jellyfinId, status, reason, subject, key));
        }

        if (onlyUsername is not null && rows.Count != 1)
        {
            throw new InvalidDataException("The selected legacy username was not found exactly once.");
        }

        var hashInput = JsonSerializer.Serialize(new
        {
            provider,
            issuer,
            matchField,
            onlyUsername,
            configSha256 = Convert.ToHexString(SHA256.HashData(configBytes)),
            exportSha256 = Convert.ToHexString(SHA256.HashData(exportBytes)),
            rows
        });
        var approval = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(hashInput))).ToLowerInvariant();
        return new MigrationPlan(rows, approval);
    }

    /// <summary>Applies only reviewed ready rows, retaining original Jellyfin account IDs.</summary>
    public static int ApplyInMemory(PluginConfiguration config, MigrationPlan plan, string provider)
    {
        var oidc = config.OidConfigs[provider];
        var applied = 0;
        foreach (var row in plan.Rows.Where(row => row.Status == "ready"))
        {
            if (row.IdentityKey is null || row.OidcSubject is null ||
                !oidc.CanonicalLinks.TryGetValue(row.Username, out var currentId) ||
                currentId != row.JellyfinUserId ||
                oidc.SubjectLinks.ContainsKey(row.IdentityKey) ||
                oidc.SubjectLinks.Values.Contains(row.JellyfinUserId))
            {
                throw new InvalidDataException("Unexpected mapping conflict; no changes should be committed.");
            }

            if (!OidcIdentityLinks.TryLink(oidc, row.IdentityKey, row.Username, row.JellyfinUserId))
            {
                throw new InvalidDataException("OIDC identity collision during migration.");
            }

            applied++;
        }

        return applied;
    }
}
