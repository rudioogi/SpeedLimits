namespace OsmDataAcquisition.Models;

/// <summary>
/// Represents an administrative boundary polygon extracted from OSM relation data.
/// Used for accurate point-in-polygon reverse geocoding (suburb/city containment).
/// </summary>
public class PlaceBoundary
{
    public long OsmRelationId { get; set; }
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Boundary type: "city", "town", "suburb", "neighbourhood", "village", "hamlet".
    /// Derived from the relation's place= tag or inferred from admin_level.
    /// </summary>
    public string BoundaryType { get; set; } = string.Empty;

    public int AdminLevel { get; set; }

    /// <summary>
    /// Closed ring of coordinates forming the outer boundary polygon.
    /// First and last point should be identical.
    /// </summary>
    public List<GeoPoint> Polygon { get; set; } = new();

    public double MinLat { get; set; }
    public double MaxLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLon { get; set; }

    /// <summary>
    /// Calculates bounding box from the polygon vertices.
    /// </summary>
    public void CalculateBounds()
    {
        if (Polygon.Count == 0) return;
        MinLat = double.MaxValue;
        MaxLat = double.MinValue;
        MinLon = double.MaxValue;
        MaxLon = double.MinValue;
        foreach (var p in Polygon)
        {
            MinLat = Math.Min(MinLat, p.Latitude);
            MaxLat = Math.Max(MaxLat, p.Latitude);
            MinLon = Math.Min(MinLon, p.Longitude);
            MaxLon = Math.Max(MaxLon, p.Longitude);
        }
    }

    /// <summary>
    /// Tests whether a point lies inside this polygon using ray-casting.
    /// </summary>
    public bool Contains(double lat, double lon)
    {
        // Quick bounding-box rejection
        if (lat < MinLat || lat > MaxLat || lon < MinLon || lon > MaxLon)
            return false;

        var inside = false;
        for (int i = 0, j = Polygon.Count - 1; i < Polygon.Count; j = i++)
        {
            var yi = Polygon[i].Latitude;
            var xi = Polygon[i].Longitude;
            var yj = Polygon[j].Latitude;
            var xj = Polygon[j].Longitude;

            if ((yi > lat) != (yj > lat) &&
                lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    public override string ToString() =>
        $"Relation {OsmRelationId}: {Name} [{BoundaryType}] admin_level={AdminLevel} ({Polygon.Count} vertices)";
}
