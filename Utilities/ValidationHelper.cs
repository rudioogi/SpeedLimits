using Microsoft.Data.Sqlite;
using OsmDataAcquisition.Models;
using OsmDataAcquisition.Services;

namespace OsmDataAcquisition.Utilities;

/// <summary>
/// Validates database contents and reports statistics
/// </summary>
public class ValidationHelper
{
    private readonly string _databasePath;

    public ValidationHelper(string databasePath)
    {
        _databasePath = databasePath;
    }

    /// <summary>
    /// Runs validation and prints statistics
    /// </summary>
    public void ValidateAndReport()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        Console.WriteLine("\n=== Database Validation ===");

        // Basic counts
        var totalRoads = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments");
        var explicitCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 0");
        var inferredCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 1");
        var gridCells = GetScalar<long>(connection, "SELECT COUNT(DISTINCT grid_x || '_' || grid_y) FROM spatial_grid");

        Console.WriteLine($"Total road segments: {totalRoads:N0}");
        Console.WriteLine($"  Explicit speed limits: {explicitCount:N0} ({(explicitCount * 100.0 / totalRoads):F1}%)");
        Console.WriteLine($"  Inferred speed limits: {inferredCount:N0} ({(inferredCount * 100.0 / totalRoads):F1}%)");
        Console.WriteLine($"Grid cells populated: {gridCells:N0}");

        // Place counts (backward compatible with old databases)
        try
        {
            var placeCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM places");
            Console.WriteLine($"Total place nodes: {placeCount:N0}");
            if (placeCount > 0)
            {
                var cities = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('city', 'town')");
                var suburbs = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')");
                Console.WriteLine($"  Cities/Towns: {cities:N0}");
                Console.WriteLine($"  Suburbs/Villages: {suburbs:N0}");
            }
        }
        catch (SqliteException) { /* places table doesn't exist in old databases */ }

        // Speed limit distribution
        Console.WriteLine("\nSpeed limit distribution:");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT speed_limit_kmh, COUNT(*) as count
                FROM road_segments
                GROUP BY speed_limit_kmh
                ORDER BY speed_limit_kmh";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var speedLimit = reader.GetInt32(0);
                var count = reader.GetInt64(1);
                Console.WriteLine($"  {speedLimit,3} km/h: {count,8:N0} roads");
            }
        }

        // Highway type distribution
        Console.WriteLine("\nHighway type distribution:");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT highway_type, COUNT(*) as count
                FROM road_segments
                GROUP BY highway_type
                ORDER BY count DESC
                LIMIT 10";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var highwayType = reader.GetString(0);
                var count = reader.GetInt64(1);
                Console.WriteLine($"  {highwayType,-20}: {count,8:N0} roads");
            }
        }

        // Metadata
        Console.WriteLine("\nMetadata:");
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT key, value FROM metadata";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                Console.WriteLine($"  {key}: {value}");
            }
        }

        Console.WriteLine("=== Validation Complete ===\n");
    }

    /// <summary>
    /// Tests a known location query
    /// </summary>
    public void TestLocationQuery(GeoPoint location, string locationName)
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        Console.WriteLine($"\nTesting location: {locationName} {location}");

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT rs.osm_way_id, rs.name, rs.highway_type, rs.speed_limit_kmh, rs.is_inferred,
                   rs.center_lat, rs.center_lon
            FROM road_segments rs
            WHERE rs.center_lat BETWEEN @lat - 0.01 AND @lat + 0.01
              AND rs.center_lon BETWEEN @lon - 0.01 AND @lon + 0.01
            ORDER BY (rs.center_lat - @lat) * (rs.center_lat - @lat) +
                     (rs.center_lon - @lon) * (rs.center_lon - @lon)
            LIMIT 5";

        cmd.Parameters.AddWithValue("@lat", location.Latitude);
        cmd.Parameters.AddWithValue("@lon", location.Longitude);

        using var reader = cmd.ExecuteReader();
        var foundRoads = 0;
        while (reader.Read())
        {
            foundRoads++;
            var wayId = reader.GetInt64(0);
            var name = reader.IsDBNull(1) ? "(unnamed)" : reader.GetString(1);
            var highwayType = reader.GetString(2);
            var speedLimit = reader.GetInt32(3);
            var isInferred = reader.GetBoolean(4);
            var centerLat = reader.GetDouble(5);
            var centerLon = reader.GetDouble(6);

            var centerPoint = new GeoPoint(centerLat, centerLon);
            var distance = location.DistanceTo(centerPoint);

            Console.WriteLine($"  {name} [{highwayType}] {speedLimit} km/h" +
                            $"{(isInferred ? " (inferred)" : "")} - {distance:F0}m away");
        }

        if (foundRoads == 0)
        {
            Console.WriteLine("  No roads found nearby");
        }
    }

    private T GetScalar<T>(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? default(T)! : (T)Convert.ChangeType(result, typeof(T));
    }
}
