using System.Globalization;
using System.Text;
using JeekTools;

namespace VpsLimitMonitor.Settings;

/// <summary>
/// Persists the user-assigned server number and current IP as two-column tab files.
/// Each account has its own file, so the files do not need a provider column.
/// </summary>
public static class ServerNumberStore
{
    private const string IdHeader = "id";
    private const string IpHeader = "ip";
    private const string StorageDirectoryName = "ServerNumbers";
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, int>> Entries =
        new(StringComparer.OrdinalIgnoreCase);
    private static string _directory = "";

    public static void Initialize(string directory, IEnumerable<AccountConfig> accounts)
    {
        lock (Sync)
        {
            _directory = directory;
            Directory.CreateDirectory(GetStorageDirectory());
            Entries.Clear();
            foreach (var account in accounts)
                Entries[account.Name] = Load(account.Name);
        }
    }

    public static int? Get(AccountConfig account, string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return null;

        lock (Sync)
        {
            var entries = GetEntries(account.Name);
            return entries.TryGetValue(ip.Trim(), out var number) ? number : null;
        }
    }

    public static void Set(AccountConfig account, string? ip, int? number)
    {
        if (string.IsNullOrWhiteSpace(ip))
            throw new InvalidOperationException("Cannot assign a number to a server without an IP.");
        if (number is < 1 or > 99)
            throw new ArgumentOutOfRangeException(
                nameof(number),
                "Service number must be between 1 and 99."
            );

        var path = GetFilePath(account.Name);
        lock (Sync)
        {
            using var lease = SharedDataFile.Acquire(path);
            var entries = Load(account.Name);
            if (number is { } value)
                entries[ip.Trim()] = value;
            else
                entries.Remove(ip.Trim());

            Save(path, entries);
            Entries[account.Name] = entries;
        }
    }

    public static string GetFilePath(string accountName)
    {
        if (string.IsNullOrWhiteSpace(_directory))
            throw new InvalidOperationException("Server number storage has not been initialized.");

        return Path.Combine(
            GetStorageDirectory(),
            $"{SanitizeFileName(accountName)}.tab"
        );
    }

    private static string GetStorageDirectory() =>
        Path.Combine(_directory, StorageDirectoryName);

    private static Dictionary<string, int> GetEntries(string accountName)
    {
        if (!Entries.TryGetValue(accountName, out var entries))
        {
            entries = Load(accountName);
            Entries[accountName] = entries;
        }

        return entries;
    }

    private static Dictionary<string, int> Load(string accountName)
    {
        var entries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(_directory))
            return entries;

        var tab = new TabFile();
        if (!tab.Load(GetFilePath(accountName)))
            return entries;

        foreach (var row in tab.Rows)
        {
            if (row.Count < 2
                || string.Equals(row[0], IdHeader, StringComparison.OrdinalIgnoreCase)
                || string.Equals(row[1], IpHeader, StringComparison.OrdinalIgnoreCase))
                continue;

            if (
                !int.TryParse(
                    row[0].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var number
                )
                || number is < 1 or > 99
            )
                continue;

            var ip = row[1].Trim();
            if (!string.IsNullOrWhiteSpace(ip))
                entries[ip] = number;
        }

        return entries;
    }

    private static void Save(string path, Dictionary<string, int> entries)
    {
        var rows = new List<string> { $"{IdHeader}\t{IpHeader}" };
        rows.AddRange(
            entries
                .OrderBy(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair =>
                    $"{pair.Value.ToString("D2", CultureInfo.InvariantCulture)}\t{pair.Key}"
                )
        );

        var content = string.Join("\n", rows) + "\n";
        SharedDataFile.WriteAllBytesAtomic(path, new UTF8Encoding(false).GetBytes(content));
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(name.Length);
        foreach (var character in name.Trim())
            builder.Append(invalid.Contains(character) ? '_' : character);

        return builder.Length == 0 ? "Servers" : builder.ToString();
    }
}
