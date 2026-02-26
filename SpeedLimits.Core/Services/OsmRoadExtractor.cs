using OsmSharp;
using OsmSharp.Streams;
using SpeedLimits.Core.Configuration;
using SpeedLimits.Core.Models;
using SpeedLimits.Core.Utilities;

namespace SpeedLimits.Core.Services;

/// <summary>
/// Extracts road segments, place nodes and boundary polygons from OSM PBF files.
/// Pass 1: collect all node coordinates + place nodes
/// Pass 2: process ways (roads) + collect boundary relations
/// Pass 3: re-stream PBF to collect boundary way node-lists, then assemble polygons
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

    private static readonly HashSet<string> BoundaryPlaceTypes = new()
    {
        "city", "town", "suburb", "village", "hamlet", "neighbourhood"
    };

    public List<PlaceNode> PlaceNodes { get; private set; } = new();
    public List<PlaceBoundary> PlaceBoundaries { get; private set; } = new();
    public List<AddressNode> AddressNodes { get; private set; } = new();

    // Collected during pass 2, consumed after all roads are yielded
    private List<BoundaryRelationInfo> _boundaryRelations = new();

    public OsmRoadExtractor(CountryConfig countryConfig)
    {
        _countryConfig = countryConfig;
    }

    /// <summary>
    /// Extracts road segments from PBF file.
    /// After all road segments are consumed (.ToList()), PlaceNodes and PlaceBoundaries are populated.
    /// </summary>
    public IEnumerable<RoadSegment> ExtractRoadSegments(string pbfFilePath)
    {
        ConsoleProgressReporter.Report("Starting multi-pass OSM extraction...");

        // Pass 1: Collect all nodes
        var nodes = CollectNodes(pbfFilePath);
        ConsoleProgressReporter.Report($"Pass 1 complete: Collected {nodes.Count:N0} nodes");

        // Pass 2: Process ways (yield roads) + collect boundary relations
        ConsoleProgressReporter.Report("Pass 2: Processing ways and collecting boundary relations...");
        var roadCount = 0;
        foreach (var roadSegment in ProcessWaysAndCollectRelations(pbfFilePath, nodes))
        {
            roadCount++;
            if (roadCount % 10000 == 0)
            {
                ConsoleProgressReporter.Report($"Extracted {roadCount:N0} road segments...");
            }
            yield return roadSegment;
        }

        ConsoleProgressReporter.Report($"Pass 2 complete: Extracted {roadCount:N0} road segments, found {_boundaryRelations.Count:N0} boundary relations");

        // Pass 3 + assembly: build boundary polygons (runs after all roads are consumed)
        BuildBoundaryPolygons(pbfFilePath, nodes);
    }

    // ── Pass 1: node collection ──────────────────────────────────────────────

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
        var addressNodes = new List<AddressNode>();

        foreach (var element in source)
        {
            if (element.Type == OsmGeoType.Node)
            {
                var node = (Node)element;
                if (node.Id.HasValue && node.Latitude.HasValue && node.Longitude.HasValue)
                {
                    nodes[node.Id.Value] = (node.Latitude.Value, node.Longitude.Value);
                    nodeCount++;

                    if (node.Tags != null)
                    {
                        // Place nodes (city, town, suburb, etc.)
                        if (node.Tags.TryGetValue("place", out var placeType)
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

                        // Address nodes — postal street name source
                        if (node.Tags.TryGetValue("addr:street", out var addrStreet)
                            && !string.IsNullOrWhiteSpace(addrStreet))
                        {
                            addressNodes.Add(new AddressNode
                            {
                                OsmNodeId = node.Id.Value,
                                Latitude = node.Latitude.Value,
                                Longitude = node.Longitude.Value,
                                Street = addrStreet
                            });
                        }
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
        AddressNodes = addressNodes;
        progress.Complete($"{nodeCount:N0} nodes collected, {placeNodes.Count:N0} place nodes, {addressNodes.Count:N0} address nodes found");
        return nodes;
    }

    // ── Pass 2: ways (roads) + relations (boundaries) ────────────────────────

    /// <summary>
    /// Pass 2: Processes ways for road segments (yielded) and also collects boundary
    /// relations that appear after ways in the PBF stream.
    /// </summary>
    private IEnumerable<RoadSegment> ProcessWaysAndCollectRelations(
        string pbfFilePath, Dictionary<long, (double Lat, double Lon)> nodes)
    {
        var boundaryRelations = new List<BoundaryRelationInfo>();

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
            else if (element.Type == OsmGeoType.Relation)
            {
                var relation = (Relation)element;
                if (relation.Tags == null || relation.Members == null)
                    continue;

                var info = TryParseBoundaryRelation(relation);
                if (info != null)
                    boundaryRelations.Add(info);
            }
        }

        _boundaryRelations = boundaryRelations;
    }

    /// <summary>
    /// Checks if a relation is a boundary we care about and extracts its metadata + member way IDs.
    /// </summary>
    private static BoundaryRelationInfo? TryParseBoundaryRelation(Relation relation)
    {
        var tags = relation.Tags;
        if (tags == null) return null;

        // Must be a boundary relation
        bool isBoundary = tags.TryGetValue("boundary", out var boundaryValue)
                          && boundaryValue == "administrative";
        bool hasPlaceTag = tags.TryGetValue("place", out var placeValue)
                           && BoundaryPlaceTypes.Contains(placeValue);

        if (!isBoundary && !hasPlaceTag)
            return null;

        // Must have a name
        if (!tags.TryGetValue("name", out var name) || string.IsNullOrWhiteSpace(name))
            return null;

        // Parse admin_level
        int adminLevel = 0;
        if (tags.TryGetValue("admin_level", out var levelStr))
            int.TryParse(levelStr, out adminLevel);

        // Filter: admin_level 4+ (states/provinces) and above; skip country (2) and above-country (0-3).
        // Place-tagged relations bypass the admin_level filter entirely.
        if (!hasPlaceTag && adminLevel < 4)
            return null;

        // Determine boundary type
        string boundaryType;
        if (hasPlaceTag)
        {
            boundaryType = placeValue!;
        }
        else
        {
            // Admin-only boundaries (no place= tag): store as region or administrative.
            // "city" / "suburb" types are reserved for place=-tagged boundaries so that
            // geocoder city lookups return common-usage names, not LGA names.
            boundaryType = adminLevel switch
            {
                <= 5 => "region",
                _ => "administrative"
            };
        }

        // Collect outer member way IDs
        var outerWayIds = new List<long>();
        foreach (var member in relation.Members)
        {
            if (member.Type == OsmGeoType.Way &&
                (member.Role is null or "" or "outer"))
            {
                outerWayIds.Add(member.Id);
            }
        }

        if (outerWayIds.Count == 0)
            return null;

        return new BoundaryRelationInfo
        {
            RelationId = relation.Id ?? 0,
            Name = name,
            BoundaryType = boundaryType,
            AdminLevel = adminLevel,
            OuterWayIds = outerWayIds
        };
    }

    // ── Pass 3 + assembly: boundary polygons ────────────────────────────────

    /// <summary>
    /// Pass 3: Re-streams the PBF to collect node-lists for boundary member ways,
    /// then assembles them into closed polygon rings.
    /// </summary>
    private void BuildBoundaryPolygons(
        string pbfFilePath, Dictionary<long, (double Lat, double Lon)> nodes)
    {
        if (_boundaryRelations.Count == 0)
        {
            PlaceBoundaries = new List<PlaceBoundary>();
            return;
        }

        // Build set of all way IDs we need
        var neededWayIds = new HashSet<long>();
        foreach (var rel in _boundaryRelations)
            foreach (var wayId in rel.OuterWayIds)
                neededWayIds.Add(wayId);

        ConsoleProgressReporter.Report($"Pass 3: Collecting {neededWayIds.Count:N0} boundary ways...");

        // Stream PBF again, only collecting ways whose IDs are in the needed set
        var wayNodeLists = new Dictionary<long, long[]>(neededWayIds.Count);

        using (var fileStream = File.OpenRead(pbfFilePath))
        {
            var source = new PBFOsmStreamSource(fileStream);
            foreach (var element in source)
            {
                if (element.Type == OsmGeoType.Way)
                {
                    var way = (Way)element;
                    if (way.Id.HasValue && neededWayIds.Contains(way.Id.Value) && way.Nodes != null)
                    {
                        wayNodeLists[way.Id.Value] = way.Nodes;
                    }
                }
            }
        }

        ConsoleProgressReporter.Report($"Collected {wayNodeLists.Count:N0} boundary ways. Assembling polygons...");

        // Assemble polygons from relations
        var boundaries = new List<PlaceBoundary>();
        var assembled = 0;
        var failed = 0;

        foreach (var rel in _boundaryRelations)
        {
            // Gather the way node-lists for this relation's outer members
            var memberWays = new List<long[]>();
            foreach (var wayId in rel.OuterWayIds)
            {
                if (wayNodeLists.TryGetValue(wayId, out var nodeList))
                    memberWays.Add(nodeList);
            }

            if (memberWays.Count == 0)
            {
                failed++;
                continue;
            }

            // Assemble into a closed ring of node IDs
            var ring = AssembleRing(memberWays);
            if (ring == null || ring.Count < 4) // minimum 3 unique points + closing point
            {
                failed++;
                continue;
            }

            // Resolve node IDs to coordinates
            var polygon = new List<GeoPoint>(ring.Count);
            var allResolved = true;
            foreach (var nodeId in ring)
            {
                if (nodes.TryGetValue(nodeId, out var coords))
                {
                    polygon.Add(new GeoPoint(coords.Lat, coords.Lon));
                }
                else
                {
                    allResolved = false;
                    break;
                }
            }

            if (!allResolved || polygon.Count < 4)
            {
                failed++;
                continue;
            }

            var boundary = new PlaceBoundary
            {
                OsmRelationId = rel.RelationId,
                Name = rel.Name,
                BoundaryType = rel.BoundaryType,
                AdminLevel = rel.AdminLevel,
                Polygon = polygon
            };
            boundary.CalculateBounds();
            boundaries.Add(boundary);
            assembled++;
        }

        PlaceBoundaries = boundaries;
        ConsoleProgressReporter.Report(
            $"Boundary assembly complete: {assembled:N0} polygons built, {failed:N0} skipped");
    }

    /// <summary>
    /// Connects a list of ordered way node-arrays into a single closed ring.
    /// Ways may need to be reversed to connect end-to-end.
    /// Returns null if a closed ring cannot be formed.
    /// </summary>
    private static List<long>? AssembleRing(List<long[]> ways)
    {
        if (ways.Count == 0)
            return null;

        // Start with the first way
        var ring = new List<long>(ways[0]);
        var used = new bool[ways.Count];
        used[0] = true;
        var usedCount = 1;

        while (usedCount < ways.Count)
        {
            var lastNode = ring[^1];
            var found = false;

            for (int i = 0; i < ways.Count; i++)
            {
                if (used[i]) continue;
                var way = ways[i];
                if (way.Length == 0) continue;

                if (way[0] == lastNode)
                {
                    // Append forward, skipping the duplicate join node
                    for (int k = 1; k < way.Length; k++)
                        ring.Add(way[k]);
                    used[i] = true;
                    usedCount++;
                    found = true;
                    break;
                }
                else if (way[^1] == lastNode)
                {
                    // Append reversed, skipping the duplicate join node
                    for (int k = way.Length - 2; k >= 0; k--)
                        ring.Add(way[k]);
                    used[i] = true;
                    usedCount++;
                    found = true;
                    break;
                }
            }

            if (!found)
                break; // Cannot connect further
        }

        // Check if the ring is closed (first node == last node)
        if (ring.Count >= 4 && ring[0] == ring[^1])
            return ring;

        return null; // Not a valid closed ring
    }

    // ── Shared helpers ──────────────────────────────────────────────────────

    private (int speedLimit, bool isInferred) ExtractSpeedLimit(Way way, string highwayType)
    {
        if (way.Tags != null && way.Tags.TryGetValue("maxspeed", out var maxspeedStr))
        {
            var speedLimit = ParseSpeedLimit(maxspeedStr);
            if (speedLimit.HasValue)
                return (speedLimit.Value, false);
        }

        var inferredSpeed = _countryConfig.GetDefaultSpeedLimit(highwayType);
        return (inferredSpeed, true);
    }

    private int? ParseSpeedLimit(string maxspeedStr)
    {
        if (string.IsNullOrWhiteSpace(maxspeedStr))
            return null;

        maxspeedStr = maxspeedStr.Trim().ToLower();

        if (maxspeedStr == "none" || maxspeedStr == "signals")
            return _countryConfig.Code == "ZA" ? 120 : 110;

        if (maxspeedStr == "walk")
            return 5;

        maxspeedStr = maxspeedStr
            .Replace("km/h", "")
            .Replace("kmh", "")
            .Replace("kph", "")
            .Replace(" ", "");

        if (maxspeedStr.EndsWith("mph"))
        {
            maxspeedStr = maxspeedStr.Replace("mph", "");
            if (int.TryParse(maxspeedStr, out var mph))
                return (int)Math.Round(mph * 1.60934);
        }

        if (int.TryParse(maxspeedStr, out var kmh))
        {
            if (kmh >= 5 && kmh <= 200)
                return kmh;
        }

        return null;
    }

    private List<GeoPoint> BuildGeometry(Way way, Dictionary<long, (double Lat, double Lon)> nodes)
    {
        var geometry = new List<GeoPoint>();

        if (way.Nodes == null)
            return geometry;

        foreach (var nodeId in way.Nodes)
        {
            if (nodes.TryGetValue(nodeId, out var coords))
                geometry.Add(new GeoPoint(coords.Lat, coords.Lon));
        }

        return geometry;
    }

    // ── Private types ───────────────────────────────────────────────────────

    private class BoundaryRelationInfo
    {
        public long RelationId;
        public string Name = string.Empty;
        public string BoundaryType = string.Empty;
        public int AdminLevel;
        public List<long> OuterWayIds = new();
    }
}
