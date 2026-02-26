namespace SpeedLimits.Core.Models;

/// <summary>
/// Represents a geographic coordinate point
/// </summary>
public readonly struct GeoPoint
{
    public double Latitude { get; }
    public double Longitude { get; }

    public GeoPoint(double latitude, double longitude)
    {
        Latitude = latitude;
        Longitude = longitude;
    }

    /// <summary>
    /// Calculates distance to another point using Haversine formula (in meters)
    /// </summary>
    public double DistanceTo(GeoPoint other)
    {
        const double R = 6371000; // Earth radius in meters
        var lat1 = ToRadians(Latitude);
        var lat2 = ToRadians(other.Latitude);
        var dLat = ToRadians(other.Latitude - Latitude);
        var dLon = ToRadians(other.Longitude - Longitude);

        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return R * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;

    public override string ToString() => $"({Latitude:F6}, {Longitude:F6})";
}
