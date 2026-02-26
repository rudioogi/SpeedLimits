namespace SpeedLimits.Api.Configuration;

/// <summary>
/// Strongly-typed options for the DatabaseSettings configuration section.
/// </summary>
public class DatabaseSettings
{
    public const string SectionName = "DatabaseSettings";

    /// <summary>
    /// Absolute path to the folder containing the SQLite database files.
    /// </summary>
    public string DatabaseDirectory { get; set; } = string.Empty;
}
