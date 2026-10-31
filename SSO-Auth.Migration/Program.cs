using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Plugin.SSO_Auth.Migration;

internal static class Program
{
    private const string DefaultConfig = "/var/lib/jellyfin/plugins/configurations/SSO-Auth.xml";
    private const string DefaultBackup = "/root/sso-migration-backups";

    [DllImport("libc")]
    private static extern uint geteuid();

    private static int Main(string[] args)
    {
        try
        {
            var flags = ReadArguments(args);
            if (flags.ContainsKey("help"))
            {
                PrintHelp();
                return 0;
            }

            var configPath = Path.GetFullPath(Value(flags, "config", DefaultConfig));
            var exportPath = Path.GetFullPath(Value(flags, "users"));
            var provider = Value(flags, "provider");
            var issuer = Value(flags, "issuer");
            var matchField = Value(flags, "match-field", "username");
            flags.TryGetValue("only-user", out var onlyUser);
            var apply = flags.ContainsKey("apply");

            RequireFile(configPath);
            RequireFile(exportPath);
            var configBytes = File.ReadAllBytes(configPath);
            var directoryBytes = File.ReadAllBytes(exportPath);
            var config = MigrationEngine.Deserialize(configBytes);

            // The recovered configuration must be readable and round-trip
            // without losing any known .NET plugin settings before we plan.
            MigrationEngine.VerifyRoundTrip(config);
            var plan = MigrationEngine.Plan(config, configBytes, directoryBytes, provider, issuer, matchField, onlyUser);

            Console.WriteLine("Keycloak-only legacy SSO migration (Keycloak administrator user export)");
            var ready = plan.Rows.Count(row => row.Status == "ready");
            var already = plan.Rows.Count(row => row.Status == "already");
            var unmatched = plan.Rows.Count(row => row.Status == "unmatched");
            var blocked = plan.Rows.Count(row => row.Status == "blocked");
            Console.WriteLine($"ready={ready} already={already} unmatched={unmatched} blocked={blocked}");
            foreach (var row in plan.Rows)
            {
                Console.WriteLine($"  {row.Status,-9} {row.Username}: {row.Reason}");
            }

            Console.WriteLine("APPROVAL_HASH=" + plan.ApprovalHash);
            if (flags.TryGetValue("report", out var reportPath) && !string.IsNullOrWhiteSpace(reportPath))
            {
                WriteReport(Path.GetFullPath(reportPath), new
                {
                    provider,
                    issuer,
                    matchField,
                    only_user = onlyUser,
                    approval_hash = plan.ApprovalHash,
                    rows = plan.Rows
                });
                Console.WriteLine("Report saved.");
            }

            if (!apply)
            {
                Console.WriteLine("DRY RUN ONLY. Jellyfin settings are unchanged.");
                return 0;
            }

            if (geteuid() != 0)
            {
                throw new InvalidOperationException("--apply requires root.");
            }

            if (Value(flags, "approve", "") != plan.ApprovalHash)
            {
                throw new InvalidOperationException(
                    "Approval hash missing or changed. Review a fresh dry run.");
            }

            if ((unmatched > 0 || blocked > 0) && !flags.ContainsKey("allow-partial"))
            {
                throw new InvalidOperationException(
                    "Some users are unmatched/blocked. Use --allow-partial only after review.");
            }

            // Never change XML in memory while the running plugin can overwrite it.
            if (ProcessStatus("systemctl", "is-active", "--quiet", "jellyfin") == 0)
            {
                throw new InvalidOperationException("Stop jellyfin.service before --apply.");
            }

            if (ready == 0)
            {
                Console.WriteLine("No migrations needed. Configuration unchanged.");
                return 0;
            }

            var count = MigrationEngine.ApplyInMemory(config, plan, provider);
            if (count != ready)
            {
                throw new InvalidDataException("Migration count disagrees with approved plan.");
            }

            MigrationEngine.VerifyRoundTrip(config);
            var outputBytes = MigrationEngine.Serialize(config);
            var verify = MigrationEngine.Deserialize(outputBytes);

            // Verify every migrated identity has its original GUID after serialization.
            var cfg = verify.OidConfigs[provider];
            foreach (var row in plan.Rows.Where(row => row.Status == "ready"))
            {
                if (row.IdentityKey is null ||
                    !cfg.SubjectLinks.TryGetValue(row.IdentityKey, out var actual) ||
                    actual != row.JellyfinUserId ||
                    cfg.CanonicalLinks.ContainsKey(row.Username))
                {
                    throw new InvalidDataException("Native XML round-trip migration verification failed.");
                }
            }

            // IMPORTANT: the backup precedes the atomic replacement.
            var backup = SaveWithBackup(configPath, configBytes, outputBytes,
                Path.GetFullPath(Value(flags, "backup-dir", DefaultBackup)));
            Console.WriteLine($"APPLIED={count} BACKUP={backup}");
            Console.WriteLine("Jellyfin remains stopped; start it and verify login on the original account.");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            InvalidDataException or InvalidOperationException or ArgumentException or
            JsonException or System.Xml.XmlException)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static string SaveWithBackup(string configPath, byte[] original, byte[] updated, string backupDir)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The SSO migration writer only supports Linux.");
        }

        RequireFile(configPath);
        if (!File.ReadAllBytes(configPath).AsSpan().SequenceEqual(original))
        {
            throw new IOException("Configuration changed after the plan was created.");
        }

        if (Directory.Exists(backupDir) && new DirectoryInfo(backupDir).LinkTarget is not null)
        {
            throw new IOException("Backup directory cannot be a symlink.");
        }

        Directory.CreateDirectory(backupDir);
        File.SetUnixFileMode(backupDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var backupPath = Path.Combine(backupDir,
            "SSO-Auth.xml." + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + ".bak");
        using (var stream = new FileStream(backupPath, FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
        {
            File.SetUnixFileMode(backupPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            stream.Write(original);
            stream.Flush(flushToDisk: true);
        }

        var parent = Path.GetDirectoryName(configPath)!;
        var tmp = Path.Combine(parent, ".sso-native-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            {
                stream.Write(updated);
                stream.Flush(flushToDisk: true);
            }

            // Preserve the original file ownership and permissions exactly.
            RunRequired("chown", "--reference=" + configPath, tmp);
            RunRequired("chmod", "--reference=" + configPath, tmp);

            if (!File.ReadAllBytes(configPath).AsSpan().SequenceEqual(original))
            {
                throw new IOException("Configuration changed during migration; backup retained.");
            }

            File.Move(tmp, configPath, overwrite: true);
            return backupPath;
        }
        finally
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }
        }
    }

    private static void WriteReport(string path, object report)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("The SSO migration reporter only supports Linux.");
        }

        if (File.Exists(path) && new FileInfo(path).LinkTarget is not null)
        {
            throw new IOException("Report target may not be a symlink.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.SerializeToUtf8Bytes(report,
            new JsonSerializerOptions { WriteIndented = true });
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        stream.Write(json);
    }

    private static void RequireFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.LinkTarget is not null)
        {
            throw new IOException("Missing file or symlink not allowed: " + path);
        }
    }

    private static void RunRequired(string program, params string[] args)
    {
        var code = ProcessStatus(program, args);
        if (code != 0)
        {
            throw new IOException(program + " failed with status " + code);
        }
    }

    private static int ProcessStatus(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new IOException("Cannot launch " + program);
        process.WaitForExit();
        return process.ExitCode;
    }

    private static Dictionary<string, string?> ReadArguments(string[] args)
    {
        var accepted = new HashSet<string>(StringComparer.Ordinal)
        {
            "users", "config", "provider", "issuer", "match-field", "only-user", "report",
            "apply", "approve", "allow-partial", "backup-dir", "help"
        };
        var switches = new HashSet<string>(StringComparer.Ordinal)
        {
            "apply", "allow-partial", "help"
        };
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) ||
                !accepted.Contains(token[2..]))
            {
                throw new ArgumentException("Unknown option: " + token);
            }

            var key = token[2..];
            if (!result.TryAdd(key, null))
            {
                throw new ArgumentException("Duplicate option: " + token);
            }

            if (!switches.Contains(key))
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("Missing value for " + token);
                }

                result[key] = args[index];
            }
        }

        return result;
    }

    private static string Value(IReadOnlyDictionary<string, string?> args, string key, string? fallback = null)
        => args.TryGetValue(key, out var result) && !string.IsNullOrWhiteSpace(result)
            ? result
            : fallback ?? throw new ArgumentException("Missing required --" + key);

    private static void PrintHelp()
        => Console.WriteLine(
            "Jellyfin SSO: Keycloak-only legacy account migration\n" +
            "Input: minimal Keycloak Admin API user export (JSON array / realm users array).\n" +
            "Other OIDC providers and their export formats are NOT supported by this CLI.\n" +
            "--users KEYCLOAK_USERS.json --provider JELLYFIN_PROVIDER_NAME --issuer KEYCLOAK_REALM_HTTPS_URL\n" +
            "[--config SSO-Auth.xml] [--match-field username|email] [--only-user LEGACY_USERNAME] [--report FILE]\n" +
            "[--apply --approve HASH --allow-partial] [--backup-dir DIRECTORY]\n" +
            "--provider selects an existing Jellyfin SSO provider; it does not verify the IdP brand.\n" +
            "Dry-run is default. Use root and stop Jellyfin before approved apply.");
}
