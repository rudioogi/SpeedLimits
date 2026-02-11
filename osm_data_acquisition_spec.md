# OSM Speed Limit Data Acquisition and SQLite Storage - C# Implementation

## Overview
Build a data acquisition and processing pipeline that downloads OpenStreetMap data for South Africa and Australia, extracts road segments with speed limits, and stores them in an optimized SQLite database for use by IoT devices.

## Architecture

### Components
1. **Data Downloader** - Downloads OSM PBF files from Geofabrik
2. **OSM Parser** - Extracts road data from PBF files
3. **Speed Limit Processor** - Infers missing speed limits based on road classification
4. **Database Builder** - Creates and populates SQLite database with spatial index
5. **Validation Tool** - Tests database accuracy with known locations

## Data Sources

### Geofabrik Downloads
```
South Africa:
- URL: https://download.geofabrik.de/africa/south-africa-latest.osm.pbf
- Size: ~200-300 MB
- Update frequency: Daily

Australia:
- URL: https://download.geofabrik.de/australia-oceania/australia-latest.osm.pbf
- Size: ~1.5-2 GB
- Update frequency: Daily

Alternative source (BBBike - for custom extracts):
- URL: https://extract.bbbike.org/
```

## SQLite Database Schema

### Tables

#### 1. metadata
Stores database information and configuration
```sql
CREATE TABLE metadata (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- Insert default metadata
INSERT INTO metadata VALUES ('version', '1.0');
INSERT INTO metadata VALUES ('country', 'ZA'); -- or 'AU'
INSERT INTO metadata VALUES ('created_date', datetime('now'));
INSERT INTO metadata VALUES ('osm_file_date', '2025-02-11');
INSERT INTO metadata VALUES ('total_roads', '0');
INSERT INTO metadata VALUES ('min_lat', '-35.0');
INSERT INTO metadata VALUES ('max_lat', '-22.0');
INSERT INTO metadata VALUES ('min_lon', '16.0');
INSERT INTO metadata VALUES ('max_lon', '33.0');
```

#### 2. road_segments
Stores individual road segments
```sql
CREATE TABLE road_segments (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    osm_way_id INTEGER NOT NULL,
    name TEXT,
    road_type TEXT NOT NULL, -- highway tag value
    speed_limit INTEGER NOT NULL, -- in km/h
    is_inferred BOOLEAN NOT NULL DEFAULT 0,
    min_lat REAL NOT NULL,
    max_lat REAL NOT NULL,
    min_lon REAL NOT NULL,
    max_lon REAL NOT NULL,
    center_lat REAL NOT NULL,
    center_lon REAL NOT NULL
);

CREATE INDEX idx_road_segments_bounds ON road_segments(min_lat, max_lat, min_lon, max_lon);
CREATE INDEX idx_road_segments_center ON road_segments(center_lat, center_lon);
CREATE INDEX idx_road_segments_osm_id ON road_segments(osm_way_id);
```

#### 3. road_geometry
Stores the detailed geometry points for each road segment
```sql
CREATE TABLE road_geometry (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    road_segment_id INTEGER NOT NULL,
    sequence INTEGER NOT NULL, -- Order of points in the line
    latitude REAL NOT NULL,
    longitude REAL NOT NULL,
    FOREIGN KEY (road_segment_id) REFERENCES road_segments(id) ON DELETE CASCADE
);

CREATE INDEX idx_road_geometry_segment ON road_geometry(road_segment_id, sequence);
CREATE INDEX idx_road_geometry_location ON road_geometry(latitude, longitude);
```

#### 4. spatial_grid
Grid-based spatial index for fast lookups
```sql
CREATE TABLE spatial_grid (
    grid_x INTEGER NOT NULL,
    grid_y INTEGER NOT NULL,
    road_segment_id INTEGER NOT NULL,
    PRIMARY KEY (grid_x, grid_y, road_segment_id),
    FOREIGN KEY (road_segment_id) REFERENCES road_segments(id) ON DELETE CASCADE
);

CREATE INDEX idx_spatial_grid_cell ON spatial_grid(grid_x, grid_y);
```

### Database Configuration
```sql
-- Optimize for read performance
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA cache_size = -64000; -- 64MB cache
PRAGMA temp_store = MEMORY;
PRAGMA mmap_size = 268435456; -- 256MB memory-mapped I/O
```

## Data Processing Pipeline

### Step 1: Download OSM Data

```csharp
public class OsmDataDownloader
{
    private readonly HttpClient _httpClient;
    
    public async Task<string> DownloadOsmData(string country, string outputDirectory)
    {
        string url = country.ToUpper() switch
        {
            "ZA" => "https://download.geofabrik.de/africa/south-africa-latest.osm.pbf",
            "AU" => "https://download.geofabrik.de/australia-oceania/australia-latest.osm.pbf",
            _ => throw new ArgumentException($"Unsupported country: {country}")
        };
        
        string filename = Path.Combine(outputDirectory, $"{country.ToLower()}-latest.osm.pbf");
        
        // Download with progress reporting
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        
        var totalBytes = response.Content.Headers.ContentLength ?? 0;
        using var contentStream = await response.Content.ReadAsStreamAsync();
        using var fileStream = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None);
        
        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;
        
        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;
            
            // Report progress
            if (totalBytes > 0)
            {
                var progress = (double)totalRead / totalBytes * 100;
                Console.WriteLine($"Downloaded: {progress:F1}% ({totalRead / 1024 / 1024} MB)");
            }
        }
        
        return filename;
    }
}
```

### Step 2: Parse OSM PBF File

```csharp
public class OsmRoadExtractor
{
    public IEnumerable<RoadSegment> ExtractRoads(string pbfFilePath, string countryCode)
    {
        using var stream = File.OpenRead(pbfFilePath);
        var source = new PBFOsmStreamSource(stream);
        
        // First pass: collect all nodes (for way geometry)
        var nodes = new Dictionary<long, Node>();
        
        foreach (var element in source)
        {
            if (element.Type == OsmGeoType.Node)
            {
                var node = (Node)element;
                if (node.Latitude.HasValue && node.Longitude.HasValue)
                {
                    nodes[node.Id.Value] = node;
                }
            }
        }
        
        // Reset stream for second pass
        stream.Position = 0;
        source = new PBFOsmStreamSource(stream);
        
        // Second pass: extract ways with highway tags
        foreach (var element in source)
        {
            if (element.Type == OsmGeoType.Way)
            {
                var way = (Way)element;
                
                if (way.Tags == null || !way.Tags.ContainsKey("highway"))
                    continue;
                
                var highwayType = way.Tags["highway"];
                
                // Filter out non-routable highways
                if (IsRoutableHighway(highwayType))
                {
                    var roadSegment = BuildRoadSegment(way, nodes, countryCode);
                    if (roadSegment != null)
                        yield return roadSegment;
                }
            }
        }
    }
    
    private bool IsRoutableHighway(string highwayType)
    {
        var routableTypes = new HashSet<string>
        {
            "motorway", "trunk", "primary", "secondary", "tertiary",
            "unclassified", "residential", "motorway_link", "trunk_link",
            "primary_link", "secondary_link", "tertiary_link",
            "living_street", "service"
        };
        
        return routableTypes.Contains(highwayType);
    }
    
    private RoadSegment BuildRoadSegment(Way way, Dictionary<long, Node> nodes, string countryCode)
    {
        if (way.Nodes == null || way.Nodes.Length < 2)
            return null;
        
        var geometry = new List<GeoPoint>();
        
        foreach (var nodeId in way.Nodes)
        {
            if (nodes.TryGetValue(nodeId, out var node))
            {
                geometry.Add(new GeoPoint 
                { 
                    Latitude = node.Latitude.Value, 
                    Longitude = node.Longitude.Value 
                });
            }
        }
        
        if (geometry.Count < 2)
            return null;
        
        var highwayType = way.Tags["highway"];
        var speedLimit = ExtractSpeedLimit(way.Tags, highwayType, countryCode, out bool isInferred);
        
        return new RoadSegment
        {
            OsmWayId = way.Id.Value,
            Name = way.Tags.ContainsKey("name") ? way.Tags["name"] : null,
            RoadType = highwayType,
            SpeedLimit = speedLimit,
            IsInferred = isInferred,
            Geometry = geometry
        };
    }
    
    private int ExtractSpeedLimit(TagsCollectionBase tags, string highwayType, string countryCode, out bool isInferred)
    {
        isInferred = false;
        
        // Try to get explicit maxspeed tag
        if (tags.ContainsKey("maxspeed"))
        {
            var maxspeedValue = tags["maxspeed"];
            
            // Handle "none", "signals", "variable" cases
            if (maxspeedValue == "none")
                return countryCode == "AU" ? 110 : 120;
            
            // Parse numeric value (handle "120", "120 km/h", "120 kph")
            var match = System.Text.RegularExpressions.Regex.Match(maxspeedValue, @"\d+");
            if (match.Success && int.TryParse(match.Value, out int speed))
            {
                // Convert mph to km/h if needed
                if (maxspeedValue.Contains("mph"))
                    speed = (int)(speed * 1.60934);
                
                return speed;
            }
        }
        
        // Infer from highway type
        isInferred = true;
        return InferSpeedLimit(highwayType, countryCode);
    }
    
    private int InferSpeedLimit(string highwayType, string countryCode)
    {
        if (countryCode == "ZA")
        {
            return highwayType switch
            {
                "motorway" => 120,
                "trunk" => 120,
                "primary" => 100,
                "secondary" => 80,
                "tertiary" => 60,
                "residential" => 60,
                "living_street" => 40,
                "unclassified" => 60,
                "service" => 40,
                _ => 60
            };
        }
        else // AU
        {
            return highwayType switch
            {
                "motorway" => 110,
                "trunk" => 110,
                "primary" => 100,
                "secondary" => 80,
                "tertiary" => 60,
                "residential" => 50,
                "living_street" => 40,
                "unclassified" => 50,
                "service" => 40,
                _ => 50
            };
        }
    }
}
```

### Step 3: Build SQLite Database

```csharp
public class DatabaseBuilder
{
    private const int GRID_SIZE = 1000; // 1000x1000 grid
    private readonly string _connectionString;
    
    public void BuildDatabase(string dbPath, IEnumerable<RoadSegment> roads, string countryCode)
    {
        // Delete existing database
        if (File.Exists(dbPath))
            File.Delete(dbPath);
        
        _connectionString = $"Data Source={dbPath};Version=3;";
        
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        
        // Create schema
        CreateSchema(connection);
        
        // Configure for performance
        ConfigureDatabase(connection);
        
        // Calculate bounds
        var bounds = CalculateBounds(roads);
        
        // Begin transaction for bulk insert
        using var transaction = connection.BeginTransaction();
        
        try
        {
            // Insert metadata
            InsertMetadata(connection, countryCode, bounds);
            
            // Insert roads and build spatial index
            int totalRoads = 0;
            
            foreach (var road in roads)
            {
                InsertRoadSegment(connection, road, bounds);
                totalRoads++;
                
                if (totalRoads % 1000 == 0)
                    Console.WriteLine($"Processed {totalRoads} roads...");
            }
            
            // Update total count
            UpdateMetadata(connection, "total_roads", totalRoads.ToString());
            
            transaction.Commit();
            
            Console.WriteLine($"Database created successfully with {totalRoads} road segments");
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        
        // Optimize database
        OptimizeDatabase(connection);
    }
    
    private void CreateSchema(SqliteConnection connection)
    {
        var commands = new[]
        {
            @"CREATE TABLE metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )",
            
            @"CREATE TABLE road_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                osm_way_id INTEGER NOT NULL,
                name TEXT,
                road_type TEXT NOT NULL,
                speed_limit INTEGER NOT NULL,
                is_inferred BOOLEAN NOT NULL DEFAULT 0,
                min_lat REAL NOT NULL,
                max_lat REAL NOT NULL,
                min_lon REAL NOT NULL,
                max_lon REAL NOT NULL,
                center_lat REAL NOT NULL,
                center_lon REAL NOT NULL
            )",
            
            @"CREATE TABLE road_geometry (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                road_segment_id INTEGER NOT NULL,
                sequence INTEGER NOT NULL,
                latitude REAL NOT NULL,
                longitude REAL NOT NULL,
                FOREIGN KEY (road_segment_id) REFERENCES road_segments(id) ON DELETE CASCADE
            )",
            
            @"CREATE TABLE spatial_grid (
                grid_x INTEGER NOT NULL,
                grid_y INTEGER NOT NULL,
                road_segment_id INTEGER NOT NULL,
                PRIMARY KEY (grid_x, grid_y, road_segment_id),
                FOREIGN KEY (road_segment_id) REFERENCES road_segments(id) ON DELETE CASCADE
            )",
            
            "CREATE INDEX idx_road_segments_bounds ON road_segments(min_lat, max_lat, min_lon, max_lon)",
            "CREATE INDEX idx_road_segments_center ON road_segments(center_lat, center_lon)",
            "CREATE INDEX idx_road_segments_osm_id ON road_segments(osm_way_id)",
            "CREATE INDEX idx_road_geometry_segment ON road_geometry(road_segment_id, sequence)",
            "CREATE INDEX idx_road_geometry_location ON road_geometry(latitude, longitude)",
            "CREATE INDEX idx_spatial_grid_cell ON spatial_grid(grid_x, grid_y)"
        };
        
        foreach (var command in commands)
        {
            using var cmd = new SqliteCommand(command, connection);
            cmd.ExecuteNonQuery();
        }
    }
    
    private void ConfigureDatabase(SqliteConnection connection)
    {
        var pragmas = new[]
        {
            "PRAGMA journal_mode = WAL",
            "PRAGMA synchronous = NORMAL",
            "PRAGMA cache_size = -64000",
            "PRAGMA temp_store = MEMORY",
            "PRAGMA mmap_size = 268435456"
        };
        
        foreach (var pragma in pragmas)
        {
            using var cmd = new SqliteCommand(pragma, connection);
            cmd.ExecuteNonQuery();
        }
    }
    
    private Bounds CalculateBounds(IEnumerable<RoadSegment> roads)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        
        foreach (var road in roads)
        {
            foreach (var point in road.Geometry)
            {
                minLat = Math.Min(minLat, point.Latitude);
                maxLat = Math.Max(maxLat, point.Latitude);
                minLon = Math.Min(minLon, point.Longitude);
                maxLon = Math.Max(maxLon, point.Longitude);
            }
        }
        
        return new Bounds { MinLat = minLat, MaxLat = maxLat, MinLon = minLon, MaxLon = maxLon };
    }
    
    private void InsertRoadSegment(SqliteConnection connection, RoadSegment road, Bounds bounds)
    {
        // Calculate bounding box and center
        var segmentBounds = CalculateSegmentBounds(road.Geometry);
        
        // Insert road segment
        var insertRoad = @"INSERT INTO road_segments 
            (osm_way_id, name, road_type, speed_limit, is_inferred, min_lat, max_lat, min_lon, max_lon, center_lat, center_lon)
            VALUES (@osm_way_id, @name, @road_type, @speed_limit, @is_inferred, @min_lat, @max_lat, @min_lon, @max_lon, @center_lat, @center_lon);
            SELECT last_insert_rowid();";
        
        long roadSegmentId;
        using (var cmd = new SqliteCommand(insertRoad, connection))
        {
            cmd.Parameters.AddWithValue("@osm_way_id", road.OsmWayId);
            cmd.Parameters.AddWithValue("@name", (object)road.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@road_type", road.RoadType);
            cmd.Parameters.AddWithValue("@speed_limit", road.SpeedLimit);
            cmd.Parameters.AddWithValue("@is_inferred", road.IsInferred ? 1 : 0);
            cmd.Parameters.AddWithValue("@min_lat", segmentBounds.MinLat);
            cmd.Parameters.AddWithValue("@max_lat", segmentBounds.MaxLat);
            cmd.Parameters.AddWithValue("@min_lon", segmentBounds.MinLon);
            cmd.Parameters.AddWithValue("@max_lon", segmentBounds.MaxLon);
            cmd.Parameters.AddWithValue("@center_lat", segmentBounds.CenterLat);
            cmd.Parameters.AddWithValue("@center_lon", segmentBounds.CenterLon);
            
            roadSegmentId = (long)cmd.ExecuteScalar();
        }
        
        // Insert geometry points
        var insertGeometry = @"INSERT INTO road_geometry 
            (road_segment_id, sequence, latitude, longitude) 
            VALUES (@road_segment_id, @sequence, @latitude, @longitude)";
        
        for (int i = 0; i < road.Geometry.Count; i++)
        {
            using var cmd = new SqliteCommand(insertGeometry, connection);
            cmd.Parameters.AddWithValue("@road_segment_id", roadSegmentId);
            cmd.Parameters.AddWithValue("@sequence", i);
            cmd.Parameters.AddWithValue("@latitude", road.Geometry[i].Latitude);
            cmd.Parameters.AddWithValue("@longitude", road.Geometry[i].Longitude);
            cmd.ExecuteNonQuery();
        }
        
        // Insert into spatial grid
        var gridCells = GetGridCells(segmentBounds, bounds);
        var insertGrid = @"INSERT OR IGNORE INTO spatial_grid 
            (grid_x, grid_y, road_segment_id) 
            VALUES (@grid_x, @grid_y, @road_segment_id)";
        
        foreach (var (x, y) in gridCells)
        {
            using var cmd = new SqliteCommand(insertGrid, connection);
            cmd.Parameters.AddWithValue("@grid_x", x);
            cmd.Parameters.AddWithValue("@grid_y", y);
            cmd.Parameters.AddWithValue("@road_segment_id", roadSegmentId);
            cmd.ExecuteNonQuery();
        }
    }
    
    private SegmentBounds CalculateSegmentBounds(List<GeoPoint> geometry)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLon = double.MaxValue, maxLon = double.MinValue;
        
        foreach (var point in geometry)
        {
            minLat = Math.Min(minLat, point.Latitude);
            maxLat = Math.Max(maxLat, point.Latitude);
            minLon = Math.Min(minLon, point.Longitude);
            maxLon = Math.Max(maxLon, point.Longitude);
        }
        
        return new SegmentBounds
        {
            MinLat = minLat,
            MaxLat = maxLat,
            MinLon = minLon,
            MaxLon = maxLon,
            CenterLat = (minLat + maxLat) / 2,
            CenterLon = (minLon + maxLon) / 2
        };
    }
    
    private List<(int x, int y)> GetGridCells(SegmentBounds segmentBounds, Bounds worldBounds)
    {
        var cells = new List<(int x, int y)>();
        
        var latRange = worldBounds.MaxLat - worldBounds.MinLat;
        var lonRange = worldBounds.MaxLon - worldBounds.MinLon;
        
        int minX = (int)Math.Floor((segmentBounds.MinLon - worldBounds.MinLon) / lonRange * GRID_SIZE);
        int maxX = (int)Math.Ceiling((segmentBounds.MaxLon - worldBounds.MinLon) / lonRange * GRID_SIZE);
        int minY = (int)Math.Floor((segmentBounds.MinLat - worldBounds.MinLat) / latRange * GRID_SIZE);
        int maxY = (int)Math.Ceiling((segmentBounds.MaxLat - worldBounds.MinLat) / latRange * GRID_SIZE);
        
        // Clamp to grid bounds
        minX = Math.Max(0, Math.Min(GRID_SIZE - 1, minX));
        maxX = Math.Max(0, Math.Min(GRID_SIZE - 1, maxX));
        minY = Math.Max(0, Math.Min(GRID_SIZE - 1, minY));
        maxY = Math.Max(0, Math.Min(GRID_SIZE - 1, maxY));
        
        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                cells.Add((x, y));
            }
        }
        
        return cells;
    }
    
    private void InsertMetadata(SqliteConnection connection, string countryCode, Bounds bounds)
    {
        var metadata = new Dictionary<string, string>
        {
            { "version", "1.0" },
            { "country", countryCode },
            { "created_date", DateTime.UtcNow.ToString("O") },
            { "osm_file_date", DateTime.UtcNow.ToString("yyyy-MM-dd") },
            { "total_roads", "0" },
            { "min_lat", bounds.MinLat.ToString("F6") },
            { "max_lat", bounds.MaxLat.ToString("F6") },
            { "min_lon", bounds.MinLon.ToString("F6") },
            { "max_lon", bounds.MaxLon.ToString("F6") },
            { "grid_size", GRID_SIZE.ToString() }
        };
        
        foreach (var (key, value) in metadata)
        {
            using var cmd = new SqliteCommand("INSERT INTO metadata (key, value) VALUES (@key, @value)", connection);
            cmd.Parameters.AddWithValue("@key", key);
            cmd.Parameters.AddWithValue("@value", value);
            cmd.ExecuteNonQuery();
        }
    }
    
    private void UpdateMetadata(SqliteConnection connection, string key, string value)
    {
        using var cmd = new SqliteCommand("UPDATE metadata SET value = @value WHERE key = @key", connection);
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        cmd.ExecuteNonQuery();
    }
    
    private void OptimizeDatabase(SqliteConnection connection)
    {
        Console.WriteLine("Optimizing database...");
        
        using (var cmd = new SqliteCommand("ANALYZE", connection))
            cmd.ExecuteNonQuery();
        
        using (var cmd = new SqliteCommand("VACUUM", connection))
            cmd.ExecuteNonQuery();
        
        Console.WriteLine("Database optimization complete");
    }
}
```

## Data Models

```csharp
public class RoadSegment
{
    public long OsmWayId { get; set; }
    public string Name { get; set; }
    public string RoadType { get; set; }
    public int SpeedLimit { get; set; }
    public bool IsInferred { get; set; }
    public List<GeoPoint> Geometry { get; set; }
}

public struct GeoPoint
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
}

public class Bounds
{
    public double MinLat { get; set; }
    public double MaxLat { get; set; }
    public double MinLon { get; set; }
    public double MaxLon { get; set; }
}

public class SegmentBounds : Bounds
{
    public double CenterLat { get; set; }
    public double CenterLon { get; set; }
}
```

## Main Application Flow

```csharp
public class Program
{
    static async Task Main(string[] args)
    {
        var countries = new[] { "ZA", "AU" };
        
        foreach (var country in countries)
        {
            Console.WriteLine($"Processing {country}...");
            
            // Step 1: Download OSM data
            Console.WriteLine("Downloading OSM data...");
            var downloader = new OsmDataDownloader();
            var pbfFile = await downloader.DownloadOsmData(country, "./data");
            
            // Step 2: Extract roads
            Console.WriteLine("Extracting road data...");
            var extractor = new OsmRoadExtractor();
            var roads = extractor.ExtractRoads(pbfFile, country).ToList();
            Console.WriteLine($"Extracted {roads.Count} road segments");
            
            // Step 3: Build database
            Console.WriteLine("Building SQLite database...");
            var dbPath = $"./data/{country.ToLower()}_speedlimits.db";
            var builder = new DatabaseBuilder();
            builder.BuildDatabase(dbPath, roads, country);
            
            // Step 4: Validate
            Console.WriteLine("Validating database...");
            ValidateDatabase(dbPath, country);
            
            Console.WriteLine($"{country} processing complete!");
            Console.WriteLine();
        }
        
        Console.WriteLine("All processing complete!");
    }
    
    static void ValidateDatabase(string dbPath, string countryCode)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Version=3;");
        connection.Open();
        
        // Check total roads
        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM road_segments", connection))
        {
            var count = (long)cmd.ExecuteScalar();
            Console.WriteLine($"Total road segments: {count}");
        }
        
        // Check roads with explicit speed limits
        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM road_segments WHERE is_inferred = 0", connection))
        {
            var count = (long)cmd.ExecuteScalar();
            Console.WriteLine($"Roads with explicit speed limits: {count}");
        }
        
        // Check spatial grid
        using (var cmd = new SqliteCommand("SELECT COUNT(DISTINCT grid_x || '_' || grid_y) FROM spatial_grid", connection))
        {
            var count = (long)cmd.ExecuteScalar();
            Console.WriteLine($"Grid cells populated: {count}");
        }
        
        // Sample speed limit distribution
        Console.WriteLine("\nSpeed limit distribution:");
        using (var cmd = new SqliteCommand(@"
            SELECT speed_limit, COUNT(*) as count 
            FROM road_segments 
            GROUP BY speed_limit 
            ORDER BY speed_limit", connection))
        {
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                Console.WriteLine($"  {reader.GetInt32(0)} km/h: {reader.GetInt64(1)} roads");
            }
        }
    }
}
```

## NuGet Packages Required

```xml
<PackageReference Include="OsmSharp" Version="7.0.0" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.0" />
```

## Configuration File (appsettings.json)

```json
{
  "DataAcquisition": {
    "OutputDirectory": "./data",
    "Countries": ["ZA", "AU"],
    "GeofabrikBaseUrl": "https://download.geofabrik.de",
    "GridSize": 1000,
    "MinRoadLength": 10,
    "IncludeServiceRoads": true
  },
  "Database": {
    "CacheSize": 64000,
    "MmapSize": 268435456,
    "JournalMode": "WAL"
  }
}
```

## Performance Optimizations

1. **Bulk Inserts**: Use transactions for inserting thousands of roads
2. **Prepared Statements**: Reuse SqliteCommand objects
3. **Grid Size**: Tune GRID_SIZE based on data density (1000x1000 works well)
4. **Memory-Mapped I/O**: Enable PRAGMA mmap_size for faster reads
5. **WAL Mode**: Write-Ahead Logging for better concurrency

## Expected Database Sizes

- **South Africa**: 50-100 MB
- **Australia**: 300-500 MB

## Testing Validation Points

### South Africa Test Points
```
Cape Town N1: -33.9249, 18.4241 → 120 km/h
Johannesburg M1: -26.2041, 28.0473 → 120 km/h
Durban N3: -29.8587, 31.0218 → 120 km/h
Residential: -33.9258, 18.4232 → 60 km/h
```

### Australia Test Points
```
Sydney M1: -33.8688, 151.2093 → 110 km/h
Melbourne M1: -37.8136, 144.9631 → 100 km/h
Brisbane M1: -27.4698, 153.0251 → 110 km/h
Residential: -33.8675, 151.2070 → 50 km/h
```

## Error Handling

Handle these scenarios:
- Download failures (network issues)
- Corrupted PBF files
- Insufficient disk space
- Invalid geometry (< 2 points)
- Database write errors
- Out of memory (large countries)

## Logging

Log the following:
- Download progress
- Roads extracted
- Roads with/without explicit speed limits
- Database size
- Processing time
- Validation results

## Deliverables

1. **OsmDataAcquisition.exe** - Console application
2. **ZA_speedlimits.db** - South Africa database
3. **AU_speedlimits.db** - Australia database
4. **README.md** - Usage instructions
5. **validation_report.txt** - Database statistics

## Usage Example

```bash
# Run data acquisition
dotnet run --project OsmDataAcquisition

# Output:
# Processing ZA...
# Downloading OSM data...
# Downloaded: 100% (245 MB)
# Extracting road data...
# Extracted 85,423 road segments
# Building SQLite database...
# Processed 85,423 roads...
# Database created successfully with 85,423 road segments
# Optimizing database...
# Validating database...
# Total road segments: 85,423
# Roads with explicit speed limits: 12,567
# Grid cells populated: 4,521
# ZA processing complete!
```