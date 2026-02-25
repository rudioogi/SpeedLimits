using OsmSharp;
using OsmSharp.Streams;
using OsmDataAcquisition.Configuration;
using OsmDataAcquisition.Models;
using OsmDataAcquisition.Utilities;

namespace OsmDataAcquisition.Services;

/// <summary>
/// Extracts road segments with speed limits from OSM PBF files using two-pass algorithm
/// </summary>
public class OsmRoadExtractor
{
    private readonly CountryConfig _countryConfig;
    private static readonly HashSet<string> RoutableHighwayTypes = new()
    {
        "motorway", "trunk", "primary", "secondary", "tertiary",
        "unclassified", "residential", "living_street", "service",
        "motorway_link", "trunk_link", "primary_link",
        "secondary_link", "tertiary_link"
    };

    private static readonly HashSet<string> PlaceTypes = new()
    {
        "city", "town", "suburb", "village", "hamlet", "neighbourhood"
    };

    public List<PlaceNode> PlaceNodes { get; private set; } = new();

    public OsmRoadExtractor(CountryConfig countryConfig)
    {
        _countryConfig = countryConfig;
    }

    /// <summary>
    /// Extracts road segments from PBF file
    /// </summary>
    public IEnumerable<RoadSegment> ExtractRoadSegments(string pbfFilePath)
    {
        ConsoleProgressReporter.Report("Starting two-pass OSM extraction...");

        // Pass 1: Collect all nodes
        var nodes = CollectNodes(pbfFilePath);
        ConsoleProgressReporter.Report($"Pass 1 complete: Collected {nodes.Count:N0} nodes");

        // Pass 2: Process ways and build road segments
        ConsoleProgressReporter.Report("Pass 2: Processing ways...");
        var roadCount = 0;
        foreach (var roadSegment in ProcessWays(pbfFilePath, nodes))
        {
            roadCount++;
            if (roadCount % 10000 == 0)
            {
                ConsoleProgressReporter.Report($"Extracted {roadCount:N0} road segments...");
            }
            yield return roadSegment;
        }

        ConsoleProgressReporter.Report($"Pass 2 complete: Extracted {roadCount:N0} road segments");
    }

    /// <summary>
    /// Pass 1: Collects all node coordinates into a dictionary (stores only lat/lon, not full Node objects)
    /// </summary>
    private Dictionary<long, (double Lat, double Lon)> CollectNodes(string pbfFilePath)
    {
        var progress = new ConsoleProgressReporter("Pass 1: Collecting nodes");
        var nodes = new Dictionary<long, (double Lat, double Lon)>();
        long nodeCount = 0;
        var lastReportTime = DateTime.UtcNow;

        using var fileStream = File.OpenRead(pbfFilePath);
        var source = new PBFOsmStreamSource(fileStream);

        var placeNodes = new List<PlaceNode>();

        foreach (var element in source)
        {
            if (element.Type == OsmGeoType.Node)
            {
                var node = (Node)element;
                if (node.Id.HasValue && node.Latitude.HasValue && node.Longitude.HasValue)
                {
                    nodes[node.Id.Value] = (node.Latitude.Value, node.Longitude.Value);
                    nodeCount++;

                    // Check for place nodes (city, town, suburb, etc.)
                    if (node.Tags != null
                        && node.Tags.TryGetValue("place", out var placeType)
                        && PlaceTypes.Contains(placeType)
                        && node.Tags.TryGetValue("name", out var placeName))
                    {
                        placeNodes.Add(new PlaceNode
                        {
                            OsmNodeId = node.Id.Value,
                            Name = placeName,
                            PlaceType = placeType,
                            Latitude = node.Latitude.Value,
                            Longitude = node.Longitude.Value
                        });
                    }

                    // Report progress every second
                    if ((DateTime.UtcNow - lastReportTime).TotalSeconds > 1)
                    {
                        progress.ReportCount(nodeCount);
                        lastReportTime = DateTime.UtcNow;
                    }
                }
            }
        }

        PlaceNodes = placeNodes;
        progress.Complete($"{nodeCount:N0} nodes collected, {placeNodes.Count:N0} place nodes found");
        return nodes;
    }

    /// <summary>
    /// Pass 2: Processes ways and builds road segments
    /// </summary>
    private IEnumerable<RoadSegment> ProcessWays(string pbfFilePath, Dictionary<long, (double Lat, double Lon)> nodes)
    {
        using var fileStream = File.OpenRead(pbfFilePath);
        var source = new PBFOsmStreamSource(fileStream);

        foreach (var element in source)
        {
            if (element.Type == OsmGeoType.Way)
            {
                var way = (Way)element;

                // Check if this is a routable road
                if (way.Tags == null || !way.Tags.TryGetValue("highway", out var highwayType))
                    continue;

                if (!RoutableHighwayTypes.Contains(highwayType))
                    continue;

                // Extract speed limit
                var (speedLimit, isInferred) = ExtractSpeedLimit(way, highwayType);

                // Build geometry from nodes
                var geometry = BuildGeometry(way, nodes);
                if (geometry.Count < 2)
                    continue; // Skip invalid ways

                // Create road segment
                var roadSegment = new RoadSegment
                {
                    OsmWayId = way.Id ?? 0,
                    Name = way.Tags.TryGetValue("name", out var name) ? name : null,
                    HighwayType = highwayType,
                    SpeedLimitKmh = speedLimit,
                    IsInferred = isInferred,
                    Geometry = geometry
                };

                roadSegment.CalculateBounds();
                yield return roadSegment;
            }
        }
    }

    /// <summary>
    /// Extracts or infers speed limit from way tags
    /// </summary>
    private (int speedLimit, bool isInferred) ExtractSpeedLimit(Way way, string highwayType)
    {
        // Check for explicit maxspeed tag
        if (way.Tags != null && way.Tags.TryGetValue("maxspeed", out var maxspeedStr))
        {
            var speedLimit = ParseSpeedLimit(maxspeedStr);
            if (speedLimit.HasValue)
            {
                return (speedLimit.Value, false); // Explicit
            }
        }

        // Infer from highway type
        var inferredSpeed = _countryConfig.GetDefaultSpeedLimit(highwayType);
        return (inferredSpeed, true);
    }

    /// <summary>
    /// Parses speed limit string (handles various formats)
    /// </summary>
    private int? ParseSpeedLimit(string maxspeedStr)
    {
        if (string.IsNullOrWhiteSpace(maxspeedStr))
            return null;

        maxspeedStr = maxspeedStr.Trim().ToLower();

        // Handle special cases
        if (maxspeedStr == "none" || maxspeedStr == "signals")
        {
            // "none" typically means national speed limit
            return _countryConfig.Code == "ZA" ? 120 : 110;
        }

        if (maxspeedStr == "walk")
            return 5;

        // Remove common suffixes
        maxspeedStr = maxspeedStr
            .Replace("km/h", "")
            .Replace("kmh", "")
            .Replace("kph", "")
            .Replace(" ", "");

        // Handle mph (convert to km/h)
        if (maxspeedStr.EndsWith("mph"))
        {
            maxspeedStr = maxspeedStr.Replace("mph", "");
            if (int.TryParse(maxspeedStr, out var mph))
            {
                return (int)Math.Round(mph * 1.60934);
            }
        }

        // Parse numeric value
        if (int.TryParse(maxspeedStr, out var kmh))
        {
            // Sanity check (5-200 km/h range)
            if (kmh >= 5 && kmh <= 200)
                return kmh;
        }

        return null;
    }

    /// <summary>
    /// Builds geometry list from way nodes
    /// </summary>
    private List<GeoPoint> BuildGeometry(Way way, Dictionary<long, (double Lat, double Lon)> nodes)
    {
        var geometry = new List<GeoPoint>();

        if (way.Nodes == null)
            return geometry;

        foreach (var nodeId in way.Nodes)
        {
            if (nodes.TryGetValue(nodeId, out var coords))
            {
                geometry.Add(new GeoPoint(coords.Lat, coords.Lon));
            }
        }

        return geometry;
    }
}
