namespace OsmDataAcquisition.Models;

/// <summary>
/// Represents a road segment extracted from OSM data
/// </summary>
public class RoadSegment
{
    public long OsmWayId { get; set; }
    public string? Name { get; set; }
    public string HighwayType { get; set; } = string.Empty;
    public int SpeedLimitKmh { get; set; }
    public bool IsInferred { get; set; }
    public List<GeoPoint> Geometry { get; set; } = new();
    public SegmentBounds Bounds { get; set; } = new();

    /// <summary>
    /// Calculates bounds and center from geometry
    /// </summary>
    public void CalculateBounds()
    {
        if (Geometry.Count == 0)
            return;

        Bounds = new SegmentBounds();
        foreach (var point in Geometry)
        {
            Bounds.Expand(point);
        }
        Bounds.CalculateCenter();
    }

    public override string ToString() =>
        $"Way {OsmWayId}: {Name ?? "(unnamed)"} [{HighwayType}] {SpeedLimitKmh} km/h" +
        (IsInferred ? " (inferred)" : "");
}
