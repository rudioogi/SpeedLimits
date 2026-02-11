namespace OsmDataAcquisition.Configuration;

/// <summary>
/// Configuration for data acquisition process
/// </summary>
public class DataAcquisitionConfig
{
    public List<CountryConfig> Countries { get; set; } = new();
    public string DownloadDirectory { get; set; } = "data/downloads";
    public string DatabaseDirectory { get; set; } = "data";
    public int RetryAttempts { get; set; } = 3;
    public int RetryDelaySeconds { get; set; } = 5;
}

/// <summary>
/// Configuration for a specific country
/// </summary>
public class CountryConfig
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string GeofabrikUrl { get; set; } = string.Empty;
    public Dictionary<string, int> DefaultSpeedLimits { get; set; } = new();

    /// <summary>
    /// Gets default speed limit for a highway type
    /// </summary>
    public int GetDefaultSpeedLimit(string highwayType)
    {
        if (DefaultSpeedLimits.TryGetValue(highwayType, out var speedLimit))
            return speedLimit;

        // Fallback defaults
        return highwayType switch
        {
            "motorway" or "motorway_link" => 100,
            "trunk" or "trunk_link" => 100,
            "primary" or "primary_link" => 80,
            "secondary" or "secondary_link" => 80,
            "tertiary" or "tertiary_link" => 60,
            _ => 50
        };
    }
}
