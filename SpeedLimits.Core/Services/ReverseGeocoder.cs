using Microsoft.Data.Sqlite;
using SpeedLimits.Core.Models;

namespace SpeedLimits.Core.Services;

/// <summary>
/// Reverse geocodes GPS coordinates to street/suburb/city using the speed limit database.
/// Prefers polygon containment (place_boundaries table) for suburb/city lookups.
/// Falls back to nearest place node if no containing polygon is found.
/// </summary>
public class ReverseGeocoder : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _nearestRoadCmd;
    private readonly SqliteCommand _suburbCmd;
    private readonly SqliteCommand _cityCmd;
    private readonly SqliteCommand? _addrStreetCmd;
    private readonly SqliteCommand? _suburbBoundaryCmd;
    private readonly SqliteCommand? _cityBoundaryCmd;
    private readonly SqliteCommand? _municipalityBoundaryCmd;
    private readonly SqliteCommand? _regionBoundaryCmd;
    private readonly bool _hasPlacesTable;
    private readonly bool _hasBoundariesTable;
    private readonly bool _hasAddressNodesTable;

    // Search radii in degrees (approximate):
    // ~550m at equator  = 0.005 degrees
    // ~5.5km at equator = 0.05 degrees
    // ~33km at equator  = 0.3 degrees
    private const double StreetRadiusDeg = 0.005;
    private const double SuburbRadiusDeg = 0.05;
    private const double CityRadiusDeg = 0.3;

    public ReverseGeocoder(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        _connection.Open();

        // Backward-compatible table checks
        _hasPlacesTable = TableExists("places");
        _hasBoundariesTable = TableExists("place_boundaries");
        _hasAddressNodesTable = TableExists("address_nodes");

        // ── Nearest road: road way name + highway type ───────────────────────
        _nearestRoadCmd = _connection.CreateCommand();
        _nearestRoadCmd.CommandText = @"
            SELECT name, highway_type, center_lat, center_lon
            FROM road_segments
            WHERE name IS NOT NULL
              AND center_lat BETWEEN @lat - @radius AND @lat + @radius
              AND center_lon BETWEEN @lon - @radius AND @lon + @radius
            ORDER BY
                (center_lat - @lat) * (center_lat - @lat) +
                (center_lon - @lon) * (center_lon - @lon)
            LIMIT 1";
        _nearestRoadCmd.Parameters.Add("@lat", SqliteType.Real);
        _nearestRoadCmd.Parameters.Add("@lon", SqliteType.Real);
        _nearestRoadCmd.Parameters.Add("@radius", SqliteType.Real);

        // ── Suburb: nearest place node (fallback) ────────────────────────────
        _suburbCmd = _connection.CreateCommand();
        _suburbCmd.CommandText = @"
            SELECT name, place_type, latitude, longitude
            FROM places
            WHERE place_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')
              AND latitude BETWEEN @lat - @radius AND @lat + @radius
              AND longitude BETWEEN @lon - @radius AND @lon + @radius
            ORDER BY
                (latitude - @lat) * (latitude - @lat) +
                (longitude - @lon) * (longitude - @lon)
            LIMIT 1";
        _suburbCmd.Parameters.Add("@lat", SqliteType.Real);
        _suburbCmd.Parameters.Add("@lon", SqliteType.Real);
        _suburbCmd.Parameters.Add("@radius", SqliteType.Real);

        // ── City: nearest place node (fallback) ──────────────────────────────
        _cityCmd = _connection.CreateCommand();
        _cityCmd.CommandText = @"
            SELECT name, place_type, latitude, longitude
            FROM places
            WHERE place_type IN ('city', 'town')
              AND latitude BETWEEN @lat - @radius AND @lat + @radius
              AND longitude BETWEEN @lon - @radius AND @lon + @radius
            ORDER BY
                (latitude - @lat) * (latitude - @lat) +
                (longitude - @lon) * (longitude - @lon)
            LIMIT 1";
        _cityCmd.Parameters.Add("@lat", SqliteType.Real);
        _cityCmd.Parameters.Add("@lon", SqliteType.Real);
        _cityCmd.Parameters.Add("@radius", SqliteType.Real);

        // ── addr:street postal name (only if table exists) ──────────────────
        if (_hasAddressNodesTable)
        {
            _addrStreetCmd = _connection.CreateCommand();
            _addrStreetCmd.CommandText = @"
                SELECT street
                FROM address_nodes
                WHERE latitude  BETWEEN @lat - @radius AND @lat + @radius
                  AND longitude BETWEEN @lon - @radius AND @lon + @radius
                ORDER BY
                    (latitude  - @lat) * (latitude  - @lat) +
                    (longitude - @lon) * (longitude - @lon)
                LIMIT 1";
            _addrStreetCmd.Parameters.Add("@lat",    SqliteType.Real);
            _addrStreetCmd.Parameters.Add("@lon",    SqliteType.Real);
            _addrStreetCmd.Parameters.Add("@radius", SqliteType.Real);
        }

        // ── Polygon-based commands (only if table exists) ────────────────────
        if (_hasBoundariesTable)
        {
            // Suburb polygon: candidates whose bbox contains the point, smallest area first
            _suburbBoundaryCmd = _connection.CreateCommand();
            _suburbBoundaryCmd.CommandText = @"
                SELECT name, boundary_type, polygon_blob
                FROM place_boundaries
                WHERE boundary_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')
                  AND min_lat <= @lat AND max_lat >= @lat
                  AND min_lon <= @lon AND max_lon >= @lon
                ORDER BY (max_lat - min_lat) * (max_lon - min_lon) ASC";
            _suburbBoundaryCmd.Parameters.Add("@lat", SqliteType.Real);
            _suburbBoundaryCmd.Parameters.Add("@lon", SqliteType.Real);

            // City polygon: candidates whose bbox contains the point, smallest area first
            _cityBoundaryCmd = _connection.CreateCommand();
            _cityBoundaryCmd.CommandText = @"
                SELECT name, boundary_type, polygon_blob
                FROM place_boundaries
                WHERE boundary_type IN ('city', 'town')
                  AND min_lat <= @lat AND max_lat >= @lat
                  AND min_lon <= @lon AND max_lon >= @lon
                ORDER BY (max_lat - min_lat) * (max_lon - min_lon) ASC";
            _cityBoundaryCmd.Parameters.Add("@lat", SqliteType.Real);
            _cityBoundaryCmd.Parameters.Add("@lon", SqliteType.Real);

            // Municipality polygon: admin-only LGA/district boundary (boundary_type='administrative')
            _municipalityBoundaryCmd = _connection.CreateCommand();
            _municipalityBoundaryCmd.CommandText = @"
                SELECT name, boundary_type, polygon_blob
                FROM place_boundaries
                WHERE boundary_type = 'administrative'
                  AND min_lat <= @lat AND max_lat >= @lat
                  AND min_lon <= @lon AND max_lon >= @lon
                ORDER BY (max_lat - min_lat) * (max_lon - min_lon) ASC";
            _municipalityBoundaryCmd.Parameters.Add("@lat", SqliteType.Real);
            _municipalityBoundaryCmd.Parameters.Add("@lon", SqliteType.Real);

            // Region polygon: state/province level (admin_level 4-5)
            _regionBoundaryCmd = _connection.CreateCommand();
            _regionBoundaryCmd.CommandText = @"
                SELECT name, boundary_type, polygon_blob
                FROM place_boundaries
                WHERE boundary_type = 'region'
                  AND min_lat <= @lat AND max_lat >= @lat
                  AND min_lon <= @lon AND max_lon >= @lon
                ORDER BY (max_lat - min_lat) * (max_lon - min_lon) ASC";
            _regionBoundaryCmd.Parameters.Add("@lat", SqliteType.Real);
            _regionBoundaryCmd.Parameters.Add("@lon", SqliteType.Real);
        }
    }

    public bool HasPlaceData => _hasPlacesTable || _hasBoundariesTable;

    /// <summary>
    /// Reverse geocode coordinates to street, suburb, and city.
    /// Uses polygon containment first; falls back to nearest place node.
    /// </summary>
    public ReverseGeocodeResult Lookup(double latitude, double longitude)
    {
        var result = new ReverseGeocodeResult();
        var queryPoint = new GeoPoint(latitude, longitude);

        // ── Nearest road (road way name + highway type) ──────────────────────
        _nearestRoadCmd.Parameters["@lat"].Value = latitude;
        _nearestRoadCmd.Parameters["@lon"].Value = longitude;
        _nearestRoadCmd.Parameters["@radius"].Value = StreetRadiusDeg;

        using (var reader = _nearestRoadCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                result.NearestRoad = reader.GetString(0);
                result.HighwayType = reader.GetString(1);
                var roadPoint = new GeoPoint(reader.GetDouble(2), reader.GetDouble(3));
                result.NearestRoadDistanceM = queryPoint.DistanceTo(roadPoint);
            }
        }

        // ── Postal street name from nearest addr:street node ─────────────────
        if (_hasAddressNodesTable)
        {
            _addrStreetCmd!.Parameters["@lat"].Value = latitude;
            _addrStreetCmd.Parameters["@lon"].Value = longitude;
            _addrStreetCmd.Parameters["@radius"].Value = StreetRadiusDeg;

            using var addrReader = _addrStreetCmd.ExecuteReader();
            if (addrReader.Read())
                result.Street = addrReader.GetString(0);
        }

        // ── Suburb lookup: polygon first, then nearest-point fallback ────────
        if (_hasBoundariesTable)
        {
            var suburb = FindContainingBoundary(_suburbBoundaryCmd!, latitude, longitude);
            if (suburb != null)
            {
                result.Suburb = suburb.Value.Name;
                result.SuburbType = suburb.Value.BoundaryType + " (polygon)";
                result.SuburbDistanceM = 0; // point is inside the boundary
            }
        }
        if (result.Suburb == null && _hasPlacesTable)
        {
            _suburbCmd.Parameters["@lat"].Value = latitude;
            _suburbCmd.Parameters["@lon"].Value = longitude;
            _suburbCmd.Parameters["@radius"].Value = SuburbRadiusDeg;

            using var reader = _suburbCmd.ExecuteReader();
            if (reader.Read())
            {
                result.Suburb = reader.GetString(0);
                result.SuburbType = reader.GetString(1);
                var suburbPoint = new GeoPoint(reader.GetDouble(2), reader.GetDouble(3));
                result.SuburbDistanceM = queryPoint.DistanceTo(suburbPoint);
            }
        }

        // ── City lookup: polygon first, then nearest-point fallback ──────────
        if (_hasBoundariesTable)
        {
            var city = FindContainingBoundary(_cityBoundaryCmd!, latitude, longitude);
            if (city != null)
            {
                result.City = city.Value.Name;
                result.CityType = city.Value.BoundaryType + " (polygon)";
                result.CityDistanceM = 0;
            }
        }
        if (result.City == null && _hasPlacesTable)
        {
            _cityCmd.Parameters["@lat"].Value = latitude;
            _cityCmd.Parameters["@lon"].Value = longitude;
            _cityCmd.Parameters["@radius"].Value = CityRadiusDeg;

            using var reader = _cityCmd.ExecuteReader();
            if (reader.Read())
            {
                result.City = reader.GetString(0);
                result.CityType = reader.GetString(1);
                var cityPoint = new GeoPoint(reader.GetDouble(2), reader.GetDouble(3));
                result.CityDistanceM = queryPoint.DistanceTo(cityPoint);
            }
        }

        // ── Municipality lookup: admin-only boundary (LGA/district name) ───────
        if (_hasBoundariesTable)
        {
            var municipality = FindContainingBoundary(_municipalityBoundaryCmd!, latitude, longitude);
            if (municipality != null)
            {
                result.Municipality = municipality.Value.Name;
                result.MunicipalityType = municipality.Value.BoundaryType + " (polygon)";
                result.MunicipalityDistanceM = 0;
            }
        }

        // ── Region lookup: polygon only (states/provinces have no place nodes) ─
        if (_hasBoundariesTable)
        {
            var region = FindContainingBoundary(_regionBoundaryCmd!, latitude, longitude);
            if (region != null)
            {
                result.Region = region.Value.Name;
                result.RegionType = region.Value.BoundaryType + " (polygon)";
                result.RegionDistanceM = 0;
            }
        }

        return result;
    }

    // ── Proximity road name search ───────────────────────────────────────────

    /// <summary>
    /// Searches within <paramref name="radiusDeg"/> degrees for any road segment
    /// (or addr:street node, if available) whose name fuzzy-matches
    /// <paramref name="expectedName"/>. Returns the closest match found, or null.
    /// <para>
    /// Intended as a secondary check: when the primary nearest-road lookup returns
    /// a different road name, call this to see whether the expected road actually
    /// exists nearby before declaring a mismatch.
    /// </para>
    /// </summary>
    public (string MatchedName, double DistanceM)? FindNearbyRoadByName(
        string expectedName, double latitude, double longitude, double radiusDeg = 0.01)
    {
        var queryPoint = new GeoPoint(latitude, longitude);
        string? bestName = null;
        double bestDist = double.MaxValue;

        // ── road_segments ────────────────────────────────────────────────────
        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT name, center_lat, center_lon
                FROM road_segments
                WHERE name IS NOT NULL
                  AND center_lat BETWEEN @lat - @radius AND @lat + @radius
                  AND center_lon BETWEEN @lon - @radius AND @lon + @radius";
            cmd.Parameters.AddWithValue("@lat", latitude);
            cmd.Parameters.AddWithValue("@lon", longitude);
            cmd.Parameters.AddWithValue("@radius", radiusDeg);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var name = reader.GetString(0);
                if (!FuzzyContains(expectedName, name)) continue;

                var pt = new GeoPoint(reader.GetDouble(1), reader.GetDouble(2));
                var dist = queryPoint.DistanceTo(pt);
                if (dist < bestDist) { bestDist = dist; bestName = name; }
            }
        }

        // ── address_nodes (postal street names) ──────────────────────────────
        if (_hasAddressNodesTable)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT street, latitude, longitude
                FROM address_nodes
                WHERE latitude  BETWEEN @lat - @radius AND @lat + @radius
                  AND longitude BETWEEN @lon - @radius AND @lon + @radius";
            cmd.Parameters.AddWithValue("@lat", latitude);
            cmd.Parameters.AddWithValue("@lon", longitude);
            cmd.Parameters.AddWithValue("@radius", radiusDeg);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var street = reader.GetString(0);
                if (!FuzzyContains(expectedName, street)) continue;

                var pt = new GeoPoint(reader.GetDouble(1), reader.GetDouble(2));
                var dist = queryPoint.DistanceTo(pt);
                if (dist < bestDist) { bestDist = dist; bestName = street; }
            }
        }

        return bestName != null ? (bestName, bestDist) : null;
    }

    private static bool FuzzyContains(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    // ── Polygon helpers ─────────────────────────────────────────────────────

    /// <summary>
    /// Queries candidate boundaries whose bounding box contains the point,
    /// then tests each polygon with ray-casting. Returns the first match
    /// (smallest area due to ORDER BY).
    /// </summary>
    private static (string Name, string BoundaryType)? FindContainingBoundary(
        SqliteCommand cmd, double lat, double lon)
    {
        cmd.Parameters["@lat"].Value = lat;
        cmd.Parameters["@lon"].Value = lon;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var blob = (byte[])reader["polygon_blob"];
            var polygon = DeserializePolygon(blob);

            if (PointInPolygon(lat, lon, polygon))
            {
                return (reader.GetString(0), reader.GetString(1));
            }
        }

        return null;
    }

    /// <summary>
    /// Deserialises the compact binary blob back into a list of GeoPoints.
    /// Format: [int32 count][double lat₁][double lon₁]…
    /// </summary>
    internal static List<GeoPoint> DeserializePolygon(byte[] blob)
    {
        var count = BitConverter.ToInt32(blob, 0);
        var points = new List<GeoPoint>(count);
        var offset = 4;
        for (int i = 0; i < count; i++)
        {
            var lat = BitConverter.ToDouble(blob, offset); offset += 8;
            var lon = BitConverter.ToDouble(blob, offset); offset += 8;
            points.Add(new GeoPoint(lat, lon));
        }
        return points;
    }

    /// <summary>
    /// Ray-casting point-in-polygon test. Treats latitude as Y, longitude as X.
    /// Accurate for city/suburb-scale polygons where earth curvature is negligible.
    /// </summary>
    internal static bool PointInPolygon(double lat, double lon, List<GeoPoint> polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var yi = polygon[i].Latitude;
            var xi = polygon[i].Longitude;
            var yj = polygon[j].Latitude;
            var xj = polygon[j].Longitude;

            if ((yi > lat) != (yj > lat) &&
                lon < (xj - xi) * (lat - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    // ── Utility ─────────────────────────────────────────────────────────────

    private bool TableExists(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = cmd.ExecuteScalar();
        return Convert.ToInt64(result) > 0;
    }

    public void Dispose()
    {
        _nearestRoadCmd?.Dispose();
        _addrStreetCmd?.Dispose();
        _suburbCmd?.Dispose();
        _cityCmd?.Dispose();
        _suburbBoundaryCmd?.Dispose();
        _cityBoundaryCmd?.Dispose();
        _municipalityBoundaryCmd?.Dispose();
        _regionBoundaryCmd?.Dispose();
        _connection?.Dispose();
    }
}

/// <summary>
/// Result of a reverse geocode lookup
/// </summary>
public class ReverseGeocodeResult
{
    /// <summary>Postal street name — from the nearest OSM addr:street node.</summary>
    public string? Street { get; set; }

    /// <summary>Road way name — from the nearest named road_segment.</summary>
    public string? NearestRoad { get; set; }
    public string? HighwayType { get; set; }
    public double NearestRoadDistanceM { get; set; }

    public string? Suburb { get; set; }
    public string? SuburbType { get; set; }
    public double SuburbDistanceM { get; set; }

    /// <summary>Common-usage city — from place=city/town boundaries or nodes.</summary>
    public string? City { get; set; }
    public string? CityType { get; set; }
    public double CityDistanceM { get; set; }

    /// <summary>Administrative area name — from admin-boundary polygons (e.g. LGA name).</summary>
    public string? Municipality { get; set; }
    public string? MunicipalityType { get; set; }
    public double MunicipalityDistanceM { get; set; }

    public string? Region { get; set; }
    public string? RegionType { get; set; }
    public double RegionDistanceM { get; set; }

    public string FormatStreet()       => Street       ?? NearestRoad ?? "(not found)";
    public string FormatSuburb()       => Suburb        ?? "(not found)";
    public string FormatCity()         => City          ?? "(not found)";
    public string FormatMunicipality() => Municipality  ?? "(not found)";
    public string FormatRegion()       => Region        ?? "(not found)";
}
