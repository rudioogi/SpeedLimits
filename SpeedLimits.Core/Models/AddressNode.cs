namespace SpeedLimits.Core.Models;

/// <summary>
/// An OSM node carrying addr:street — used to resolve the postal-style street name
/// for a coordinate, independent of the road way's name tag.
/// </summary>
public class AddressNode
{
    public long OsmNodeId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Value of the addr:street tag — the mailing-address street name.</summary>
    public required string Street { get; set; }
}
