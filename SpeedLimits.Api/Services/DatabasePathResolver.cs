using Microsoft.Extensions.Options;
using SpeedLimits.Api.Configuration;

namespace SpeedLimits.Api.Services;

/// <summary>
/// Resolves paths to SQLite database files using the DatabaseSettings options.
/// The DatabaseDirectory must be an absolute path configured in appsettings.json
/// under the "DatabaseSettings" section.
/// </summary>
public class DatabasePathResolver
{
    private readonly string _databaseFolder;

    public DatabasePathResolver(IOptions<DatabaseSettings> options)
    {
        var directory = options.Value.DatabaseDirectory;

        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException(
                "DatabaseSettings:DatabaseDirectory is not configured. " +
                "Set an absolute path in appsettings.json.");

        _databaseFolder = Path.GetFullPath(directory);
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
