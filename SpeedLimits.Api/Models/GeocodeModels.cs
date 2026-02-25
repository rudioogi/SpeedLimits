namespace SpeedLimits.Api.Models;

public class ReverseGeocodeResponse
{
    public double QueryLatitude { get; init; }
    public double QueryLongitude { get; init; }
    public required string CountryCode { get; init; }
    public bool HasPlaceData { get; init; }
    public string? Street { get; init; }
    public string? HighwayType { get; init; }
    public double? StreetDistanceMeters { get; init; }
    public string? Suburb { get; init; }
    public string? SuburbType { get; init; }
    public double? SuburbDistanceMeters { get; init; }
    public string? City { get; init; }
    public string? CityType { get; init; }
    public double? CityDistanceMeters { get; init; }
    /// <summary>Time taken to process this single lookup, in milliseconds.</summary>
    public double ElapsedMs { get; init; }
}

// ── Batch reverse geocode ────────────────────────────────────────────────────

public class CoordinatePair
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class BatchReverseGeocodeRequest
{
    /// <summary>Country code for all coordinates in the batch (e.g. "ZA" or "AU").</summary>
    public required string CountryCode { get; set; }

    /// <summary>One or more coordinate pairs to look up.</summary>
    public required List<CoordinatePair> Coordinates { get; set; }
}

public class BatchReverseGeocodeResult
{
    public required string CountryCode { get; init; }
    /// <summary>Total number of coordinate pairs submitted.</summary>
    public int RequestCount { get; init; }
    /// <summary>Total wall-clock time for the entire batch, in milliseconds.</summary>
    public double TotalTimeMs { get; init; }
    /// <summary>Per-coordinate results, in the same order as the request.</summary>
    public List<ReverseGeocodeResponse> Results { get; init; } = new();
}
