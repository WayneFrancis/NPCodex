using Npgsql;

namespace NPCodex.Data;

/// <summary>
/// Local development connection settings shared by NPCodex applications.
/// Reads the existing Docker Compose .env file without changing it or exporting
/// its secrets into the process environment.
/// </summary>
public static class NPCodexConnectionSettings
{
    private const string PasswordVariable = "NPCODEX_DB_PASSWORD";

    /// <summary>
    /// Resolves the PostgreSQL password from an environment variable first,
    /// otherwise from a .env file in the repository or an ancestor directory.
    /// </summary>
    public static string CreateLocalConnectionString()
    {
        var password = GetPassword();

        return new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = 5432,
            Database = "npcodex",
            Username = "npcodex",
            Password = password,
            Timeout = 5
        }.ConnectionString;
    }

    private static string GetPassword()
    {
        var overridePassword = Environment.GetEnvironmentVariable(PasswordVariable);
        if (!string.IsNullOrEmpty(overridePassword))
            return overridePassword;

        // A desktop app may launch from the solution root, a bin folder, or a
        // different working directory. Search both locations and their parents.
        var searched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var startingDirectory in new[] {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        })
        {
            for (var dir = new DirectoryInfo(startingDirectory);
                 dir is not null;
                 dir = dir.Parent)
            {
                if (!searched.Add(dir.FullName))
                    continue;

                var envFile = Path.Combine(dir.FullName, ".env");
                if (!File.Exists(envFile))
                    continue;

                var value = ReadEnvValue(envFile, PasswordVariable);
                if (!string.IsNullOrEmpty(value))
                    return value;
            }
        }

        throw new InvalidOperationException(
            "PostgreSQL password not found. Add NPCODEX_DB_PASSWORD='your-password' " +
            "to the NPCodex root .env file, or set the NPCODEX_DB_PASSWORD environment variable.");
    }

    /// <summary>
    /// Reads only the required key, not a full Compose .env interpreter.
    /// In particular, single-quoted values preserve literal dollar signs.
    /// </summary>
    private static string? ReadEnvValue(string envFile, string key)
    {
        foreach (var line in File.ReadLines(envFile))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
                trimmed = trimmed["export ".Length..].TrimStart();

            var equals = trimmed.IndexOf('=');
            if (equals < 0 || trimmed[..equals].Trim() != key)
                continue;

            var rawValue = trimmed[(equals + 1)..].Trim();
            if (rawValue.Length == 0)
                return null;

            if (rawValue[0] is '\'' or '"')
            {
                var quote = rawValue[0];
                var closingIndex = rawValue.IndexOf(quote, 1);
                // Compose allows escaped single quotes such as 'it\\'s'.
                while (closingIndex > 0 && rawValue[closingIndex - 1] == '\\')
                    closingIndex = rawValue.IndexOf(quote, closingIndex + 1);

                if (closingIndex < 1)
                    throw new InvalidOperationException(
                        $"Invalid quoted value for {key} in {envFile}.");

                var remainder = rawValue[(closingIndex + 1)..].Trim();
                if (remainder.Length > 0 && !remainder.StartsWith('#'))
                    throw new InvalidOperationException(
                        $"Unexpected text after {key} in {envFile}.");

                var quoted = rawValue.Substring(1, closingIndex - 1);
                return quote == '\''
                    ? quoted.Replace("\\'", "'", StringComparison.Ordinal)
                    : quoted;
            }

            // Unquoted Compose values support comments beginning with " #".
            var commentStart = rawValue.IndexOf(" #", StringComparison.Ordinal);
            return (commentStart >= 0 ? rawValue[..commentStart] : rawValue).TrimEnd();
        }

        return null;
    }
}
