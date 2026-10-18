using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace HobioAdmin.DbTool;

/// <summary>Applies the scripts in /db to a Tushpa database. Used by the CLI and by the integration tests.</summary>
public static partial class SqlScripts
{
    public const string ViewsScript = "01_analytics_views.sql";
    public const string ReaderScript = "02_readonly_user.sql";
    public const string DefaultReaderLogin = "hobio_admin_reader";

    private const string MasterMarker = "-- ==== PART 1: master ====";
    private const string DatabaseMarker = "-- ==== PART 2: Tushpa database ====";

    public static string Load(string name)
    {
        using var stream = typeof(SqlScripts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded script {name} not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Creates or updates the [analytics] schema and views.</summary>
    public static async Task ApplyViewsAsync(string connectionString, Action<string>? log = null, CancellationToken ct = default)
    {
        await ExecuteBatchesAsync(connectionString, Load(ViewsScript), log, ct);
    }

    /// <summary>Creates (or resets the password of) the read-only login and grants it SELECT on [analytics] only.</summary>
    public static async Task CreateReaderAsync(string connectionString, string readerPassword, string readerLogin = DefaultReaderLogin,
        Action<string>? log = null, CancellationToken ct = default)
    {
        ValidateIdentifier(readerLogin);
        if (string.IsNullOrWhiteSpace(readerPassword) || readerPassword.Length < 12)
        {
            throw new ArgumentException("The reader password must be at least 12 characters.");
        }

        var builder = new SqlConnectionStringBuilder(connectionString);
        var databaseName = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            throw new ArgumentException("The connection string must include the Tushpa database name (Database=...).");
        }

        ValidateIdentifier(databaseName);

        var script = Substitute(Load(ReaderScript), new Dictionary<string, string>
        {
            ["ReaderLogin"] = readerLogin,
            ["ReaderPassword"] = readerPassword.Replace("'", "''", StringComparison.Ordinal),
            ["DatabaseName"] = databaseName,
        });

        var (masterPart, databasePart) = SplitParts(script);

        var masterBuilder = new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" };
        await ExecuteBatchesAsync(masterBuilder.ConnectionString, masterPart, log, ct);
        await ExecuteBatchesAsync(connectionString, databasePart, log, ct);
    }

    public static async Task ExecuteBatchesAsync(string connectionString, string script, Action<string>? log = null, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var batches = SplitBatches(script);
        foreach (var batch in batches)
        {
            await using var command = new SqlCommand(batch, connection) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync(ct);
        }

        log?.Invoke($"Ran {batches.Count} batch(es) against {connection.Database} on {connection.DataSource}.");
    }

    /// <summary>Splits a script on lines that contain only GO (like sqlcmd).</summary>
    public static List<string> SplitBatches(string script) =>
        GoSeparator().Split(script)
            .Select(b => b.Trim())
            .Where(b => b.Length > 0 && !IsOnlyComments(b))
            .ToList();

    internal static string Substitute(string script, IReadOnlyDictionary<string, string> variables)
    {
        foreach (var (name, value) in variables)
        {
            script = script.Replace($"$({name})", value, StringComparison.Ordinal);
        }

        var missing = SqlCmdVariable().Match(script);
        if (missing.Success)
        {
            throw new InvalidOperationException($"Unresolved script variable {missing.Value}.");
        }

        return script;
    }

    internal static (string Master, string Database) SplitParts(string script)
    {
        var masterIndex = script.IndexOf(MasterMarker, StringComparison.Ordinal);
        var databaseIndex = script.IndexOf(DatabaseMarker, StringComparison.Ordinal);
        if (masterIndex < 0 || databaseIndex < masterIndex)
        {
            throw new InvalidOperationException("Reader script markers not found.");
        }

        return (script[masterIndex..databaseIndex], script[databaseIndex..]);
    }

    private static bool IsOnlyComments(string batch)
    {
        var withoutBlock = BlockComment().Replace(batch, string.Empty);
        return withoutBlock.Split('\n').All(line => line.Trim().Length == 0 || line.TrimStart().StartsWith("--", StringComparison.Ordinal));
    }

    private static void ValidateIdentifier(string value)
    {
        if (!SafeIdentifier().IsMatch(value))
        {
            throw new ArgumentException($"'{value}' is not a safe SQL identifier (letters, digits, _ and - only).");
        }
    }

    [GeneratedRegex(@"^\s*GO\s*;?\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();

    [GeneratedRegex(@"\$\([A-Za-z_][A-Za-z0-9_]*\)")]
    private static partial Regex SqlCmdVariable();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"^[A-Za-z0-9_\-]{1,128}$")]
    private static partial Regex SafeIdentifier();
}
