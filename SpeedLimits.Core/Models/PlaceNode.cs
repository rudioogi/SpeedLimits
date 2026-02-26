namespace SpeedLimits.Core.Models;

/// <summary>
/// Represents a named place node extracted from OSM data (city, town, suburb, etc.)
/// </summary>
public class PlaceNode
{
    public long OsmNodeId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string PlaceType { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }

    public override string ToString() =>
        $"Node {OsmNodeId}: {Name} [{PlaceType}] ({Latitude:F6}, {Longitude:F6})";
}
