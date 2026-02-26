namespace SpeedLimits.Api.Services;

/// <summary>
/// Resolves the path to the Database folder containing .db files.
/// Reads the configured value from "DatabaseDirectory" in appsettings.json.
/// Supports absolute paths and relative paths (resolved against CWD, exe directory,
/// and up to 5 parent directories so the API works from any launch location).
/// </summary>
public class DatabasePathResolver
{
    private readonly string _databaseFolder;

    public DatabasePathResolver(IConfiguration configuration)
    {
        var configured = configuration["DatabaseDirectory"] ?? "Database";
        _databaseFolder = Resolve(configured);
    }

    private static string Resolve(string path)
    {
        // Absolute path — use directly (create if needed)
        if (Path.IsPathRooted(path))
        {
            Directory.CreateDirectory(path);
            return path;
        }

        // Relative to current working directory (e.g. dotnet run from solution root)
        var cwdBased = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (Directory.Exists(cwdBased))
            return cwdBased;

        // Relative to the executable directory (published/standalone)
        var exeBased = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (Directory.Exists(exeBased))
            return exeBased;

        // Walk up from the executable (handles bin/Debug/net8.0/ nesting).
        // Use the last path component of the configured value so that
        // e.g. "Database" or "data/db" both work correctly.
        var folderName = Path.GetFileName(path.TrimEnd('/', '\\'));
        if (string.IsNullOrEmpty(folderName))
            folderName = path;

        for (var levels = 1; levels <= 5; levels++)
        {
            var parts = Enumerable.Repeat("..", levels).Append(folderName).ToArray();
            var candidate = Path.GetFullPath(
                Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray()));
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Fallback: create at CWD-relative path
        Directory.CreateDirectory(cwdBased);
        return cwdBased;
    }

    /// <summary>Returns the resolved absolute path to the Database folder.</summary>
    public string DatabaseFolder => _databaseFolder;

    /// <summary>Returns the full path for a database file.</summary>
    public string GetPath(string filename) => Path.Combine(_databaseFolder, filename);

    /// <summary>Returns the full path for a country database if it exists, otherwise null.</summary>
    public string? GetCountryDbPath(string countryCode)
    {
        var path = GetPath($"{countryCode.ToLower()}_speedlimits.db");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Returns true if the country database file exists.</summary>
    public bool CountryDbExists(string countryCode) => GetCountryDbPath(countryCode) != null;
}
