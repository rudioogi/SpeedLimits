using System.Text.Json.Serialization;

namespace SpeedLimits.Api.Models;

public class ReverseGeocodeResponse
{
    public double QueryLatitude { get; init; }
    public double QueryLongitude { get; init; }
    public required string CountryCode { get; init; }
    public bool HasPlaceData { get; init; }
    /// <summary>Postal street name from nearest addr:street node (mailing-address style).</summary>
    public string? Street { get; init; }
    /// <summary>Road way name from the nearest named road segment.</summary>
    public string? NearestRoad { get; init; }
    public string? HighwayType { get; init; }
    public double? NearestRoadDistanceMeters { get; init; }
    public string? Suburb { get; init; }
    public string? SuburbType { get; init; }
    public double? SuburbDistanceMeters { get; init; }
    /// <summary>Common-usage city name from place=city/town boundaries or nodes.</summary>
    public string? City { get; init; }
    public string? CityType { get; init; }
    public double? CityDistanceMeters { get; init; }
    /// <summary>Administrative area name from LGA/district boundary (e.g. "Shire of Yarra Ranges").</summary>
    public string? Municipality { get; init; }
    public string? MunicipalityType { get; init; }
    public double? MunicipalityDistanceMeters { get; init; }
    public string? Region { get; init; }
    public string? RegionType { get; init; }
    public double? RegionDistanceMeters { get; init; }
    /// <summary>Speed limit in km/h for the nearest road, or null if not found.</summary>
    public int? SpeedLimitKmh { get; init; }
    /// <summary>True if the speed limit was inferred from highway type rather than an explicit maxspeed tag.</summary>
    public bool? IsSpeedLimitInferred { get; init; }
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

// ── Trip validation reverse geocode ─────────────────────────────────────────

/// <summary>Request body — wraps an Elasticsearch-style hits array.</summary>
public class TripValidationRequest
{
    public required string CountryCode { get; set; }
    public required List<TripHit> Hits { get; set; }
}

public class TripHit
{
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    [JsonPropertyName("_index")]
    public string? Index { get; set; }

    [JsonPropertyName("_score")]
    public double? Score { get; set; }

    [JsonPropertyName("_source")]
    public TripSource? Source { get; set; }
}

public class TripSource
{
    public string? TripStartTimestamp { get; set; }
    public string? TripEndTimestamp { get; set; }
    public string? Id { get; set; }
    public TripLocationAddress? StartLocationAddress { get; set; }
    public TripLocationAddress? EndLocationAddress { get; set; }
}

public class TripLocationAddress
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Address { get; set; }
    public string? Place { get; set; }
    public string? Region { get; set; }
}

/// <summary>Per-trip result — expected (from input) vs actual (from geocoder).</summary>
public class TripValidationResultItem
{
    public required string TripId { get; init; }
    public string? TripStartTimestamp { get; init; }
    public string? TripEndTimestamp { get; init; }
    public TripValidationAddress StartLocationAddress { get; init; } = new();
    /// <summary>Postal street name from addr:street (mailing-address style).</summary>
    public string? ActualRoad { get; init; }
    /// <summary>Road way name from the nearest named road segment.</summary>
    public string? ActualNearestRoad { get; init; }
    public string? ActualCity { get; init; }
    public string? ActualMunicipality { get; init; }
    public string? ActualRegion { get; init; }
    public bool RoadMatched { get; init; }
    public bool PlaceMatched { get; init; }
    public bool IsMatch { get; init; }
}

/// <summary>Expected location data carried through to the response.</summary>
public class TripValidationAddress
{
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string? Road { get; init; }
    public string? Place { get; init; }
    public string? Region { get; init; }
}

public class TripValidationResponse
{
    public required string CountryCode { get; init; }
    public int RequestCount { get; init; }
    public int MatchCount { get; init; }
    public double TotalTimeMs { get; init; }
    public List<TripValidationResultItem> Results { get; init; } = new();
}
