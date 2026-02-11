namespace OsmDataAcquisition.Models;

/// <summary>
/// Extends Bounds with a center point for efficient spatial queries
/// </summary>
public class SegmentBounds : Bounds
{
    public GeoPoint Center { get; set; }

    public SegmentBounds() : base()
    {
    }

    public SegmentBounds(double minLat, double maxLat, double minLon, double maxLon)
        : base(minLat, maxLat, minLon, maxLon)
    {
        CalculateCenter();
    }

    /// <summary>
    /// Calculates the center point of the bounding box
    /// </summary>
    public void CalculateCenter()
    {
        if (IsValid())
        {
            Center = new GeoPoint(
                (MinLatitude + MaxLatitude) / 2.0,
                (MinLongitude + MaxLongitude) / 2.0
            );
        }
    }
}
