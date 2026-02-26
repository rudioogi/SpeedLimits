/*
 * Speed Limit Lookup - Optimized C# Implementation for .NET IoT
 *
 * Usage:
 *   var lookup = new SpeedLimitLookup("za_speedlimits.db");
 *   int speedLimit = lookup.GetSpeedLimit(-33.9249, 18.4241);
 *   Console.WriteLine($"Speed limit: {speedLimit} km/h");
 */

using Microsoft.Data.Sqlite;
using System;

namespace SpeedLimits.Core
{
    /// <summary>
    /// Fast speed limit lookup optimized for IoT devices
    /// </summary>
    public class SpeedLimitLookup : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly SqliteCommand _gridCommand;
        private readonly SqliteCommand _bboxCommand;
        private readonly DatabaseBounds _bounds;
        private readonly bool _hasPlacesTable;
        private readonly SqliteCommand? _streetCmd;
        private readonly SqliteCommand? _suburbCmd;
        private readonly SqliteCommand? _cityCmd;

        private struct DatabaseBounds
        {
            public double MinLatitude;
            public double MaxLatitude;
            public double MinLongitude;
            public double MaxLongitude;
            public int GridSize;
        }

        /// <summary>
        /// Initialize speed limit lookup system (call once at startup)
        /// </summary>
        public SpeedLimitLookup(string databasePath)
        {
            // Open database in read-only mode
            _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
            _connection.Open();

            // Load and cache metadata bounds
            _bounds = LoadBounds();

            // Prepare grid-based query (FASTEST)
            _gridCommand = _connection.CreateCommand();
            _gridCommand.CommandText = @"
                SELECT rs.speed_limit_kmh
                FROM spatial_grid sg
                JOIN road_segments rs ON sg.road_segment_id = rs.id
                WHERE sg.grid_x BETWEEN @gridXMin AND @gridXMax
                  AND sg.grid_y BETWEEN @gridYMin AND @gridYMax
                  AND rs.min_lat <= @lat AND rs.max_lat >= @lat
                  AND rs.min_lon <= @lon AND rs.max_lon >= @lon
                ORDER BY
                    (rs.center_lat - @lat) * (rs.center_lat - @lat) +
                    (rs.center_lon - @lon) * (rs.center_lon - @lon)
                LIMIT 1";

            _gridCommand.Parameters.Add("@gridXMin", SqliteType.Integer);
            _gridCommand.Parameters.Add("@gridXMax", SqliteType.Integer);
            _gridCommand.Parameters.Add("@gridYMin", SqliteType.Integer);
            _gridCommand.Parameters.Add("@gridYMax", SqliteType.Integer);
            _gridCommand.Parameters.Add("@lat", SqliteType.Real);
            _gridCommand.Parameters.Add("@lon", SqliteType.Real);

            // Prepare bounding box query (FALLBACK)
            _bboxCommand = _connection.CreateCommand();
            _bboxCommand.CommandText = @"
                SELECT speed_limit_kmh
                FROM road_segments
                WHERE center_lat BETWEEN @lat - 0.01 AND @lat + 0.01
                  AND center_lon BETWEEN @lon - 0.01 AND @lon + 0.01
                ORDER BY
                    (center_lat - @lat) * (center_lat - @lat) +
                    (center_lon - @lon) * (center_lon - @lon)
                LIMIT 1";

            _bboxCommand.Parameters.Add("@lat", SqliteType.Real);
            _bboxCommand.Parameters.Add("@lon", SqliteType.Real);

            // Check for places table (backward compatible)
            _hasPlacesTable = TableExists("places");

            if (_hasPlacesTable)
            {
                _streetCmd = _connection.CreateCommand();
                _streetCmd.CommandText = @"
                    SELECT name, highway_type
                    FROM road_segments
                    WHERE name IS NOT NULL
                      AND center_lat BETWEEN @lat - 0.005 AND @lat + 0.005
                      AND center_lon BETWEEN @lon - 0.005 AND @lon + 0.005
                    ORDER BY
                        (center_lat - @lat) * (center_lat - @lat) +
                        (center_lon - @lon) * (center_lon - @lon)
                    LIMIT 1";
                _streetCmd.Parameters.Add("@lat", SqliteType.Real);
                _streetCmd.Parameters.Add("@lon", SqliteType.Real);

                _suburbCmd = _connection.CreateCommand();
                _suburbCmd.CommandText = @"
                    SELECT name, place_type
                    FROM places
                    WHERE place_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')
                      AND latitude BETWEEN @lat - 0.05 AND @lat + 0.05
                      AND longitude BETWEEN @lon - 0.05 AND @lon + 0.05
                    ORDER BY
                        (latitude - @lat) * (latitude - @lat) +
                        (longitude - @lon) * (longitude - @lon)
                    LIMIT 1";
                _suburbCmd.Parameters.Add("@lat", SqliteType.Real);
                _suburbCmd.Parameters.Add("@lon", SqliteType.Real);

                _cityCmd = _connection.CreateCommand();
                _cityCmd.CommandText = @"
                    SELECT name, place_type
                    FROM places
                    WHERE place_type IN ('city', 'town')
                      AND latitude BETWEEN @lat - 0.3 AND @lat + 0.3
                      AND longitude BETWEEN @lon - 0.3 AND @lon + 0.3
                    ORDER BY
                        (latitude - @lat) * (latitude - @lat) +
                        (longitude - @lon) * (longitude - @lon)
                    LIMIT 1";
                _cityCmd.Parameters.Add("@lat", SqliteType.Real);
                _cityCmd.Parameters.Add("@lon", SqliteType.Real);
            }
        }

        /// <summary>
        /// Load database bounds (called once during initialization)
        /// </summary>
        private DatabaseBounds LoadBounds()
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'min_latitude'),
                    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'max_latitude'),
                    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'min_longitude'),
                    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'max_longitude'),
                    (SELECT CAST(value AS INTEGER) FROM metadata WHERE key = 'grid_size')";

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new DatabaseBounds
                {
                    MinLatitude = reader.GetDouble(0),
                    MaxLatitude = reader.GetDouble(1),
                    MinLongitude = reader.GetDouble(2),
                    MaxLongitude = reader.GetDouble(3),
                    GridSize = reader.GetInt32(4)
                };
            }

            throw new Exception("Failed to load database bounds");
        }

        /// <summary>
        /// Calculate grid coordinates from GPS position
        /// </summary>
        private (int gridX, int gridY) CalculateGridCoords(double latitude, double longitude)
        {
            double normX = (longitude - _bounds.MinLongitude) /
                          (_bounds.MaxLongitude - _bounds.MinLongitude);
            double normY = (latitude - _bounds.MinLatitude) /
                          (_bounds.MaxLatitude - _bounds.MinLatitude);

            int gridX = (int)(normX * _bounds.GridSize);
            int gridY = (int)(normY * _bounds.GridSize);

            // Clamp to valid range
            gridX = Math.Max(0, Math.Min(_bounds.GridSize - 1, gridX));
            gridY = Math.Max(0, Math.Min(_bounds.GridSize - 1, gridY));

            return (gridX, gridY);
        }

        /// <summary>
        /// Get speed limit using grid-based lookup (FASTEST - use this)
        /// Returns speed limit in km/h, or -1 if not found
        /// Typical execution time: <1ms
        /// </summary>
        public int GetSpeedLimitGrid(double latitude, double longitude)
        {
            var (gridX, gridY) = CalculateGridCoords(latitude, longitude);

            _gridCommand.Parameters["@gridXMin"].Value = gridX - 1;
            _gridCommand.Parameters["@gridXMax"].Value = gridX + 1;
            _gridCommand.Parameters["@gridYMin"].Value = gridY - 1;
            _gridCommand.Parameters["@gridYMax"].Value = gridY + 1;
            _gridCommand.Parameters["@lat"].Value = latitude;
            _gridCommand.Parameters["@lon"].Value = longitude;

            var result = _gridCommand.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : -1;
        }

        /// <summary>
        /// Get speed limit using bounding box lookup (FALLBACK)
        /// Returns speed limit in km/h, or -1 if not found
        /// Typical execution time: <5ms
        /// </summary>
        public int GetSpeedLimitBBox(double latitude, double longitude)
        {
            _bboxCommand.Parameters["@lat"].Value = latitude;
            _bboxCommand.Parameters["@lon"].Value = longitude;

            var result = _bboxCommand.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : -1;
        }

        /// <summary>
        /// Get speed limit (tries grid first, falls back to bbox)
        /// Returns speed limit in km/h, or -1 if not found
        /// RECOMMENDED: Use this method for automatic fallback
        /// </summary>
        public int GetSpeedLimit(double latitude, double longitude)
        {
            int speedLimit = GetSpeedLimitGrid(latitude, longitude);

            // Fallback to bounding box if grid lookup fails
            if (speedLimit == -1)
            {
                speedLimit = GetSpeedLimitBBox(latitude, longitude);
            }

            return speedLimit;
        }

        /// <summary>
        /// Get detailed road information (use sparingly - more overhead)
        /// </summary>
        public RoadInfo? GetRoadInfo(double latitude, double longitude)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT speed_limit_kmh, name, highway_type, is_inferred
                FROM road_segments
                WHERE center_lat BETWEEN @lat - 0.01 AND @lat + 0.01
                  AND center_lon BETWEEN @lon - 0.01 AND @lon + 0.01
                ORDER BY
                    (center_lat - @lat) * (center_lat - @lat) +
                    (center_lon - @lon) * (center_lon - @lon)
                LIMIT 1";

            cmd.Parameters.AddWithValue("@lat", latitude);
            cmd.Parameters.AddWithValue("@lon", longitude);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new RoadInfo
                {
                    SpeedLimitKmh = reader.GetInt32(0),
                    Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                    HighwayType = reader.GetString(2),
                    IsInferred = reader.GetBoolean(3)
                };
            }

            return null;
        }

        /// <summary>
        /// Whether the database supports reverse geocoding (has places table)
        /// </summary>
        public bool HasLocationInfo => _hasPlacesTable;

        /// <summary>
        /// Get location info (street, suburb, city) for coordinates
        /// Returns null if places table is not available
        /// </summary>
        public LocationInfo? GetLocationInfo(double latitude, double longitude)
        {
            if (!_hasPlacesTable || _streetCmd == null || _suburbCmd == null || _cityCmd == null)
                return null;

            var info = new LocationInfo();

            _streetCmd.Parameters["@lat"].Value = latitude;
            _streetCmd.Parameters["@lon"].Value = longitude;
            using (var reader = _streetCmd.ExecuteReader())
            {
                if (reader.Read())
                    info.Street = reader.GetString(0);
            }

            _suburbCmd.Parameters["@lat"].Value = latitude;
            _suburbCmd.Parameters["@lon"].Value = longitude;
            using (var reader = _suburbCmd.ExecuteReader())
            {
                if (reader.Read())
                    info.Suburb = reader.GetString(0);
            }

            _cityCmd.Parameters["@lat"].Value = latitude;
            _cityCmd.Parameters["@lon"].Value = longitude;
            using (var reader = _cityCmd.ExecuteReader())
            {
                if (reader.Read())
                    info.City = reader.GetString(0);
            }

            return info;
        }

        private bool TableExists(string tableName)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
            cmd.Parameters.AddWithValue("@name", tableName);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }

        public void Dispose()
        {
            _streetCmd?.Dispose();
            _suburbCmd?.Dispose();
            _cityCmd?.Dispose();
            _gridCommand?.Dispose();
            _bboxCommand?.Dispose();
            _connection?.Dispose();
        }
    }

    public class LocationInfo
    {
        public string? Street { get; set; }
        public string? Suburb { get; set; }
        public string? City { get; set; }

        public override string ToString() =>
            $"{Street ?? "(unknown)"}, {Suburb ?? "(unknown)"}, {City ?? "(unknown)"}";
    }

    public class RoadInfo
    {
        public int SpeedLimitKmh { get; set; }
        public string? Name { get; set; }
        public string HighwayType { get; set; } = string.Empty;
        public bool IsInferred { get; set; }

        public override string ToString() =>
            $"{Name ?? "(unnamed)"} [{HighwayType}] {SpeedLimitKmh} km/h" +
            (IsInferred ? " (inferred)" : " (explicit)");
    }

    /* Example usage - COMMENTED OUT to avoid conflicts with main Program.cs

    class ProgramExample
    {
        static void Main()
        {
            using var lookup = new SpeedLimitLookup("Database/za_speedlimits.db");

            // Example 1: Simple lookup
            var speedLimit = lookup.GetSpeedLimit(-33.9249, 18.4241);
            Console.WriteLine($"Speed limit: {speedLimit} km/h");

            // Example 2: Detailed info
            var roadInfo = lookup.GetRoadInfo(-33.9249, 18.4241);
            if (roadInfo != null)
            {
                Console.WriteLine($"Road: {roadInfo}");
            }

            // Example 3: Batch lookups (reuses prepared statements)
            var locations = new[]
            {
                (-33.9249, 18.4241),  // Cape Town
                (-26.2041, 28.0473),  // Johannesburg
                (-33.9258, 18.4232)   // Residential
            };

            foreach (var (lat, lon) in locations)
            {
                var speed = lookup.GetSpeedLimit(lat, lon);
                Console.WriteLine($"({lat}, {lon}) -> {speed} km/h");
            }
        }
    }
    */
}
