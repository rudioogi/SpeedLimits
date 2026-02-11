using Microsoft.Data.Sqlite;
using OsmDataAcquisition.Configuration;
using OsmDataAcquisition.Models;
using OsmDataAcquisition.Utilities;

namespace OsmDataAcquisition.Services;

/// <summary>
/// Builds optimized SQLite database with spatial grid indexing
/// </summary>
public class DatabaseBuilder
{
    private readonly DatabaseConfig _config;
    private readonly CountryConfig _countryConfig;

    public DatabaseBuilder(DatabaseConfig config, CountryConfig countryConfig)
    {
        _config = config;
        _countryConfig = countryConfig;
    }

    /// <summary>
    /// Builds complete database from road segments
    /// </summary>
    public void BuildDatabase(string databasePath, IEnumerable<RoadSegment> roadSegments)
    {
        ConsoleProgressReporter.Report("Building SQLite database...");

        // Delete existing database
        if (File.Exists(databasePath))
        {
            File.Delete(databasePath);
        }

        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();

        // Configure SQLite for performance
        ConfigureDatabase(connection);

        // Create schema
        CreateSchema(connection);

        // Insert data in single transaction
        using var transaction = connection.BeginTransaction();
        try
        {
            var worldBounds = InsertRoadData(connection, roadSegments);

            // Insert metadata
            InsertMetadata(connection, worldBounds);

            transaction.Commit();
            ConsoleProgressReporter.Report("Transaction committed successfully");
        }
        catch
        {
            transaction.Rollback();
            throw;
        }

        // Optimize database
        OptimizeDatabase(connection);

        ConsoleProgressReporter.Report($"Database created: {databasePath}");
    }

    /// <summary>
    /// Configures SQLite for optimal performance
    /// </summary>
    private void ConfigureDatabase(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL");
        ExecuteNonQuery(connection, $"PRAGMA cache_size = -{_config.CacheSizeKB}");
        ExecuteNonQuery(connection, $"PRAGMA mmap_size = {_config.MmapSizeMB * 1024 * 1024}");
        ExecuteNonQuery(connection, $"PRAGMA page_size = {_config.PageSize}");
        ExecuteNonQuery(connection, "PRAGMA synchronous = NORMAL");
        ExecuteNonQuery(connection, "PRAGMA temp_store = MEMORY");
        ExecuteNonQuery(connection, "PRAGMA locking_mode = EXCLUSIVE");
    }

    /// <summary>
    /// Creates database schema
    /// </summary>
    private void CreateSchema(SqliteConnection connection)
    {
        ConsoleProgressReporter.Report("Creating database schema...");

        // Metadata table
        ExecuteNonQuery(connection, @"
            CREATE TABLE metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )");

        // Road segments table
        ExecuteNonQuery(connection, @"
            CREATE TABLE road_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                osm_way_id INTEGER NOT NULL,
                name TEXT,
                highway_type TEXT NOT NULL,
                speed_limit_kmh INTEGER NOT NULL,
                is_inferred INTEGER NOT NULL,
                min_lat REAL NOT NULL,
                max_lat REAL NOT NULL,
                min_lon REAL NOT NULL,
                max_lon REAL NOT NULL,
                center_lat REAL NOT NULL,
                center_lon REAL NOT NULL
            )");

        // Road geometry table (detailed coordinates)
        ExecuteNonQuery(connection, @"
            CREATE TABLE road_geometry (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                road_segment_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                FOREIGN KEY (road_segment_id) REFERENCES road_segments(id)
            )");

        // Spatial grid table
        ExecuteNonQuery(connection, @"
            CREATE TABLE spatial_grid (
                grid_x INTEGER NOT NULL,
                grid_y INTEGER NOT NULL,
                road_segment_id INTEGER NOT NULL,
                PRIMARY KEY (grid_x, grid_y, road_segment_id),
                FOREIGN KEY (road_segment_id) REFERENCES road_segments(id)
            )");

        // Create indexes
        ExecuteNonQuery(connection, "CREATE INDEX idx_road_segments_osm_id ON road_segments(osm_way_id)");
        ExecuteNonQuery(connection, "CREATE INDEX idx_road_segments_bounds ON road_segments(min_lat, max_lat, min_lon, max_lon)");
        ExecuteNonQuery(connection, "CREATE INDEX idx_road_segments_center ON road_segments(center_lat, center_lon)");
        ExecuteNonQuery(connection, "CREATE INDEX idx_road_segments_type ON road_segments(highway_type)");
        ExecuteNonQuery(connection, "CREATE INDEX idx_road_geometry_segment ON road_geometry(road_segment_id, sequence)");
        ExecuteNonQuery(connection, "CREATE INDEX idx_spatial_grid_cells ON spatial_grid(grid_x, grid_y)");
        ExecuteNonQuery(connection, "CREATE INDEX idx_spatial_grid_segment ON spatial_grid(road_segment_id)");
    }

    /// <summary>
    /// Inserts road data and returns world bounds
    /// </summary>
    private Bounds InsertRoadData(SqliteConnection connection, IEnumerable<RoadSegment> roadSegments)
    {
        var worldBounds = new Bounds();
        var progress = new ConsoleProgressReporter("Inserting road data");
        long roadCount = 0;
        var lastReportTime = DateTime.UtcNow;

        // Prepare insert statements
        using var roadCmd = connection.CreateCommand();
        roadCmd.CommandText = @"
            INSERT INTO road_segments (osm_way_id, name, highway_type, speed_limit_kmh, is_inferred,
                                      min_lat, max_lat, min_lon, max_lon, center_lat, center_lon)
            VALUES (@osm_way_id, @name, @highway_type, @speed_limit_kmh, @is_inferred,
                    @min_lat, @max_lat, @min_lon, @max_lon, @center_lat, @center_lon);
            SELECT last_insert_rowid()";

        var roadParams = new
        {
            osm_way_id = roadCmd.Parameters.Add("@osm_way_id", SqliteType.Integer),
            name = roadCmd.Parameters.Add("@name", SqliteType.Text),
            highway_type = roadCmd.Parameters.Add("@highway_type", SqliteType.Text),
            speed_limit_kmh = roadCmd.Parameters.Add("@speed_limit_kmh", SqliteType.Integer),
            is_inferred = roadCmd.Parameters.Add("@is_inferred", SqliteType.Integer),
            min_lat = roadCmd.Parameters.Add("@min_lat", SqliteType.Real),
            max_lat = roadCmd.Parameters.Add("@max_lat", SqliteType.Real),
            min_lon = roadCmd.Parameters.Add("@min_lon", SqliteType.Real),
            max_lon = roadCmd.Parameters.Add("@max_lon", SqliteType.Real),
            center_lat = roadCmd.Parameters.Add("@center_lat", SqliteType.Real),
            center_lon = roadCmd.Parameters.Add("@center_lon", SqliteType.Real)
        };

        using var geomCmd = connection.CreateCommand();
        geomCmd.CommandText = @"
            INSERT INTO road_geometry (road_segment_id, sequence, latitude, longitude)
            VALUES (@road_segment_id, @sequence, @latitude, @longitude)";

        var geomParams = new
        {
            road_segment_id = geomCmd.Parameters.Add("@road_segment_id", SqliteType.Integer),
            sequence = geomCmd.Parameters.Add("@sequence", SqliteType.Integer),
            latitude = geomCmd.Parameters.Add("@latitude", SqliteType.Real),
            longitude = geomCmd.Parameters.Add("@longitude", SqliteType.Real)
        };

        using var gridCmd = connection.CreateCommand();
        gridCmd.CommandText = @"
            INSERT OR IGNORE INTO spatial_grid (grid_x, grid_y, road_segment_id)
            VALUES (@grid_x, @grid_y, @road_segment_id)";

        var gridParams = new
        {
            grid_x = gridCmd.Parameters.Add("@grid_x", SqliteType.Integer),
            grid_y = gridCmd.Parameters.Add("@grid_y", SqliteType.Integer),
            road_segment_id = gridCmd.Parameters.Add("@road_segment_id", SqliteType.Integer)
        };

        foreach (var segment in roadSegments)
        {
            // Insert road segment
            roadParams.osm_way_id.Value = segment.OsmWayId;
            roadParams.name.Value = segment.Name ?? (object)DBNull.Value;
            roadParams.highway_type.Value = segment.HighwayType;
            roadParams.speed_limit_kmh.Value = segment.SpeedLimitKmh;
            roadParams.is_inferred.Value = segment.IsInferred ? 1 : 0;
            roadParams.min_lat.Value = segment.Bounds.MinLatitude;
            roadParams.max_lat.Value = segment.Bounds.MaxLatitude;
            roadParams.min_lon.Value = segment.Bounds.MinLongitude;
            roadParams.max_lon.Value = segment.Bounds.MaxLongitude;
            roadParams.center_lat.Value = segment.Bounds.Center.Latitude;
            roadParams.center_lon.Value = segment.Bounds.Center.Longitude;

            var roadSegmentId = Convert.ToInt64(roadCmd.ExecuteScalar());

            // Insert geometry points
            for (int i = 0; i < segment.Geometry.Count; i++)
            {
                var point = segment.Geometry[i];
                geomParams.road_segment_id.Value = roadSegmentId;
                geomParams.sequence.Value = i;
                geomParams.latitude.Value = point.Latitude;
                geomParams.longitude.Value = point.Longitude;
                geomCmd.ExecuteNonQuery();
            }

            // Calculate and insert spatial grid cells
            var gridCells = CalculateGridCells(segment.Bounds, worldBounds);
            foreach (var (gridX, gridY) in gridCells)
            {
                gridParams.grid_x.Value = gridX;
                gridParams.grid_y.Value = gridY;
                gridParams.road_segment_id.Value = roadSegmentId;
                gridCmd.ExecuteNonQuery();
            }

            // Expand world bounds
            worldBounds.Expand(segment.Bounds.Center);

            roadCount++;
            if ((DateTime.UtcNow - lastReportTime).TotalSeconds > 1)
            {
                progress.ReportCount(roadCount);
                lastReportTime = DateTime.UtcNow;
            }
        }

        progress.Complete($"{roadCount:N0} roads processed");
        return worldBounds;
    }

    /// <summary>
    /// Calculates which grid cells a road segment intersects
    /// </summary>
    private HashSet<(int gridX, int gridY)> CalculateGridCells(SegmentBounds bounds, Bounds worldBounds)
    {
        var cells = new HashSet<(int, int)>();

        // First pass: just use center point for initial bounds
        if (!worldBounds.IsValid())
        {
            worldBounds.Expand(bounds.Center);
        }

        // Calculate grid coordinates for bounding box corners
        var minGridX = LatLonToGridX(bounds.MinLongitude, worldBounds);
        var maxGridX = LatLonToGridX(bounds.MaxLongitude, worldBounds);
        var minGridY = LatLonToGridY(bounds.MinLatitude, worldBounds);
        var maxGridY = LatLonToGridY(bounds.MaxLatitude, worldBounds);

        // Add all cells in the bounding box
        for (int x = minGridX; x <= maxGridX; x++)
        {
            for (int y = minGridY; y <= maxGridY; y++)
            {
                cells.Add((x, y));
            }
        }

        return cells;
    }

    private int LatLonToGridX(double longitude, Bounds worldBounds)
    {
        if (!worldBounds.IsValid() || worldBounds.MaxLongitude == worldBounds.MinLongitude)
            return 0;

        var normalized = (longitude - worldBounds.MinLongitude) / (worldBounds.MaxLongitude - worldBounds.MinLongitude);
        var gridX = (int)(normalized * _config.GridSize);
        return Math.Max(0, Math.Min(_config.GridSize - 1, gridX));
    }

    private int LatLonToGridY(double latitude, Bounds worldBounds)
    {
        if (!worldBounds.IsValid() || worldBounds.MaxLatitude == worldBounds.MinLatitude)
            return 0;

        var normalized = (latitude - worldBounds.MinLatitude) / (worldBounds.MaxLatitude - worldBounds.MinLatitude);
        var gridY = (int)(normalized * _config.GridSize);
        return Math.Max(0, Math.Min(_config.GridSize - 1, gridY));
    }

    /// <summary>
    /// Inserts metadata
    /// </summary>
    private void InsertMetadata(SqliteConnection connection, Bounds worldBounds)
    {
        var metadata = new Dictionary<string, string>
        {
            ["country_code"] = _countryConfig.Code,
            ["country_name"] = _countryConfig.Name,
            ["created_date"] = DateTime.UtcNow.ToString("o"),
            ["grid_size"] = _config.GridSize.ToString(),
            ["min_latitude"] = worldBounds.MinLatitude.ToString("F6"),
            ["max_latitude"] = worldBounds.MaxLatitude.ToString("F6"),
            ["min_longitude"] = worldBounds.MinLongitude.ToString("F6"),
            ["max_longitude"] = worldBounds.MaxLongitude.ToString("F6"),
            ["osm_source"] = _countryConfig.GeofabrikUrl
        };

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO metadata (key, value) VALUES (@key, @value)";
        var keyParam = cmd.Parameters.Add("@key", SqliteType.Text);
        var valueParam = cmd.Parameters.Add("@value", SqliteType.Text);

        foreach (var kvp in metadata)
        {
            keyParam.Value = kvp.Key;
            valueParam.Value = kvp.Value;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Optimizes database with ANALYZE and VACUUM
    /// </summary>
    private void OptimizeDatabase(SqliteConnection connection)
    {
        ConsoleProgressReporter.Report("Optimizing database (this may take a few minutes)...");

        ExecuteNonQuery(connection, "ANALYZE");
        ConsoleProgressReporter.Report("Analysis complete");

        ExecuteNonQuery(connection, "VACUUM");
        ConsoleProgressReporter.Report("Vacuum complete");
    }

    private void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
