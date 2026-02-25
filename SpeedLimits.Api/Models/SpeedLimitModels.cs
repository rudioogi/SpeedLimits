namespace SpeedLimits.Api.Models;

public class NearbyRoad
{
    public string? Name { get; init; }
    public required string HighwayType { get; init; }
    public int SpeedLimitKmh { get; init; }
    public bool IsInferred { get; init; }
    public double DistanceMeters { get; init; }
    public double CenterLatitude { get; init; }
    public double CenterLongitude { get; init; }
}

public class SpeedLimitLookupResult
{
    public double QueryLatitude { get; init; }
    public double QueryLongitude { get; init; }
    public required string CountryCode { get; init; }
    public List<NearbyRoad> NearbyRoads { get; init; } = new();
    public int? NearestSpeedLimitKmh => NearbyRoads.FirstOrDefault()?.SpeedLimitKmh;
}

public class KnownLocationResult
{
    public required string LocationName { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public required string CountryCode { get; init; }
    public List<NearbyRoad> NearbyRoads { get; init; } = new();
}
