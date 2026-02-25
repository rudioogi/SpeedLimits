using System.ComponentModel.DataAnnotations;

namespace SpeedLimits.Api.Models;

public class ProcessRequest
{
    /// <summary>
    /// Country code to process (e.g. "ZA", "AU"). Required unless All is true.
    /// </summary>
    public string? CountryCode { get; set; }

    /// <summary>
    /// Set to true to process all configured countries.
    /// </summary>
    public bool All { get; set; }
}

public class CountryProcessResult
{
    public required string CountryCode { get; init; }
    public required string CountryName { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
    public long RoadSegmentsExtracted { get; init; }
    public long PlaceNodesExtracted { get; init; }
    public double DatabaseSizeMb { get; init; }
    public double ProcessingTimeMinutes { get; init; }
}

public class ProcessingResult
{
    public int Successful { get; init; }
    public int Failed { get; init; }
    public int Total { get; init; }
    public List<CountryProcessResult> Results { get; init; } = new();
}

public class CountryInfo
{
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string GeofabrikUrl { get; init; }
}
