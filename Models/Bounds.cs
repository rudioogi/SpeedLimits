namespace OsmDataAcquisition.Models;

/// <summary>
/// Represents a geographic bounding box
/// </summary>
public class Bounds
{
    public double MinLatitude { get; set; }
    public double MaxLatitude { get; set; }
    public double MinLongitude { get; set; }
    public double MaxLongitude { get; set; }

    public Bounds()
    {
        MinLatitude = double.MaxValue;
        MaxLatitude = double.MinValue;
        MinLongitude = double.MaxValue;
        MaxLongitude = double.MinValue;
    }

    public Bounds(double minLat, double maxLat, double minLon, double maxLon)
    {
        MinLatitude = minLat;
        MaxLatitude = maxLat;
        MinLongitude = minLon;
        MaxLongitude = maxLon;
    }

    /// <summary>
    /// Expands bounds to include a point
    /// </summary>
    public void Expand(GeoPoint point)
    {
        MinLatitude = Math.Min(MinLatitude, point.Latitude);
        MaxLatitude = Math.Max(MaxLatitude, point.Latitude);
        MinLongitude = Math.Min(MinLongitude, point.Longitude);
        MaxLongitude = Math.Max(MaxLongitude, point.Longitude);
    }

    /// <summary>
    /// Checks if bounds contain a point
    /// </summary>
    public bool Contains(GeoPoint point)
    {
        return point.Latitude >= MinLatitude && point.Latitude <= MaxLatitude &&
               point.Longitude >= MinLongitude && point.Longitude <= MaxLongitude;
    }

    public bool IsValid()
    {
        return MinLatitude != double.MaxValue && MaxLatitude != double.MinValue &&
               MinLongitude != double.MaxValue && MaxLongitude != double.MinValue;
    }

    public override string ToString() =>
        $"[{MinLatitude:F6},{MinLongitude:F6}] to [{MaxLatitude:F6},{MaxLongitude:F6}]";
}
