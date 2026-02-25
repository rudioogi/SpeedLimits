namespace SpeedLimits.Api.Services;

/// <summary>
/// Resolves the path to the Database folder containing .db files.
/// Searches relative to the working directory (dotnet run) and the executable
/// so the API works whether launched from the project directory or the bin folder.
/// </summary>
public class DatabasePathResolver
{
    private readonly string _databaseFolder;

    public DatabasePathResolver(IConfiguration configuration)
    {
        var configured = configuration["Api:DatabaseDirectory"] ?? "../Database";
        _databaseFolder = Resolve(configured);
    }

    private static string Resolve(string path)
    {
        // Absolute path provided and exists
        if (Path.IsPathRooted(path) && Directory.Exists(path))
            return path;

        // Relative to current working directory (e.g. dotnet run from SpeedLimits.Api/)
        var cwdBased = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), path));
        if (Directory.Exists(cwdBased))
            return cwdBased;

        // Relative to the executable directory (published/standalone)
        var exeBased = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
        if (Directory.Exists(exeBased))
            return exeBased;

        // Walk up from the executable (handles bin/Debug/net8.0/ nesting)
        // Level 3 = project root of API, Level 4 = solution root (console project root)
        for (var levels = 1; levels <= 5; levels++)
        {
            var parts = Enumerable.Repeat("..", levels).Append("Database").ToArray();
            var candidate = Path.GetFullPath(Path.Combine(new[] { AppContext.BaseDirectory }.Concat(parts).ToArray()));
            if (Directory.Exists(candidate))
                return candidate;
        }

        return cwdBased; // fallback — may not exist yet
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
