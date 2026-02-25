namespace SpeedLimits.Api.Models;

public class DatabaseEntry
{
    public required string CountryCode { get; init; }
    public required string CountryName { get; init; }
    public bool Exists { get; init; }
    public string? FilePath { get; init; }
    public double? FileSizeMb { get; init; }
}

public class ValidationResult
{
    public required string CountryCode { get; init; }
    public bool IsValid { get; init; }
    public string? Error { get; init; }
    public long TotalRoadSegments { get; init; }
    public long ExplicitSpeedLimits { get; init; }
    public double ExplicitPercent { get; init; }
    public long InferredSpeedLimits { get; init; }
    public double InferredPercent { get; init; }
    public long GridCellsPopulated { get; init; }
    public long TotalPlaceNodes { get; init; }
    public long CitiesAndTowns { get; init; }
    public long SuburbsAndVillages { get; init; }
    public List<SpeedLimitBucket> SpeedLimitDistribution { get; init; } = new();
    public List<HighwayTypeBucket> HighwayTypeDistribution { get; init; } = new();
    public Dictionary<string, string> Metadata { get; init; } = new();
}

public class SpeedLimitBucket
{
    public int SpeedLimitKmh { get; init; }
    public long Count { get; init; }
}

public class HighwayTypeBucket
{
    public required string HighwayType { get; init; }
    public long Count { get; init; }
}

public class DatabaseStatistics
{
    public required string CountryCode { get; init; }
    public required string CountryName { get; init; }
    public required string FilePath { get; init; }
    public double FileSizeMb { get; init; }
    public long TotalRoadSegments { get; init; }
    public long ExplicitSpeedLimits { get; init; }
    public double ExplicitPercent { get; init; }
    public long InferredSpeedLimits { get; init; }
    public double InferredPercent { get; init; }
    public long GridCellsPopulated { get; init; }
    public long TotalPlaceNodes { get; init; }
    public long CitiesAndTowns { get; init; }
    public long SuburbsAndVillages { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}
