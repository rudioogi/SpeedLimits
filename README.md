# OSM Speed Limit Data Acquisition

A .NET 8.0 console application that downloads OpenStreetMap data for South Africa and Australia, extracts road segments with speed limits (explicit or inferred), and stores them in optimized SQLite databases for IoT device usage.

## NuGet Packages

- **OsmSharp 6.2.0** - OSM data parsing
- **Microsoft.Data.Sqlite 8.0.0** - SQLite database
- **Microsoft.Extensions.Configuration 8.0.0** - Configuration management

## Features

- **Automated Download**: Downloads OSM PBF files from Geofabrik with retry logic
- **Two-Pass Parsing**: Memory-efficient extraction of road segments from large OSM datasets
- **Speed Limit Inference**: Extracts explicit speed limits or infers from highway types
- **Spatial Grid Indexing**: 1000×1000 grid for fast location-based queries
- **Optimized SQLite**: Performance-tuned databases with WAL mode and spatial indexes
- **Validation**: Built-in validation and statistics reporting

## Requirements

- **.NET 8.0 SDK** or later
- **16GB RAM** (recommended for processing Australia dataset)
- **5GB free disk space** (for downloads and databases)
- **Internet connection** (for downloading OSM data)

## Installation

1. Clone or download this repository
2. Restore NuGet packages:
   ```bash
   dotnet restore
   ```

3. Build the project:
   ```bash
   dotnet build -c Release
   ```

## Configuration

Edit `appsettings.json` to customize:

- **Countries**: Add or remove countries to process
- **GeofabrikUrl**: OSM data source URLs
- **DefaultSpeedLimits**: Country-specific speed limits by highway type
- **GridSize**: Spatial grid resolution (default: 1000×1000)
- **Database settings**: Cache size, memory mapping, etc.

## Usage

### Run the Application

```bash
dotnet run -c Release
```

Or run the compiled executable:

```bash
cd bin/Release/net8.0
./OsmDataAcquisition.exe
```

### Expected Output

```
=== OSM Speed Limit Data Acquisition ===

============================================================
Processing South Africa (ZA)
============================================================

Step 1: Downloading OSM data
Source: https://download.geofabrik.de/africa/south-africa-latest.osm.pbf

Downloading: 100% (245.3 MB / 245.3 MB)
Downloaded file size: 245.3 MB

Step 2: Extracting road segments

Starting two-pass OSM extraction...
Pass 1: Collecting nodes: 12,345,678
Pass 1 complete: Collected 12,345,678 nodes
Pass 2: Processing ways...
Extracted 85,423 road segments...
Pass 2 complete: Extracted 85,423 road segments

Step 3: Building SQLite database

Building SQLite database...
Creating database schema...
Inserting road data: 85,423
Transaction committed successfully
Optimizing database (this may take a few minutes)...
Analysis complete
Vacuum complete
Database size: 87.5 MB

Step 4: Validating database

=== Database Validation ===
Total road segments: 85,423
  Explicit speed limits: 12,567 (14.7%)
  Inferred speed limits: 72,856 (85.3%)
Grid cells populated: 4,521
Total geometry points: 1,234,567

Speed limit distribution:
   40 km/h:    2,134 roads
   60 km/h:   45,678 roads
   80 km/h:   18,234 roads
  100 km/h:   12,456 roads
  120 km/h:    6,921 roads

Highway type distribution:
  residential         :   45,678 roads
  tertiary            :   18,234 roads
  secondary           :   12,456 roads
  primary             :    6,921 roads
  trunk               :    1,234 roads

=== Validation Complete ===

✓ South Africa processing complete!

Total processing time: 8.5 minutes
```

## Output Files

The application creates the following files:

- `data/downloads/za-latest.osm.pbf` - Downloaded OSM data (South Africa)
- `data/downloads/au-latest.osm.pbf` - Downloaded OSM data (Australia)
- `data/za_speedlimits.db` - South Africa speed limit database
- `data/au_speedlimits.db` - Australia speed limit database

## Database Schema

### Tables

#### `metadata`
Stores country information and processing metadata.

| Column | Type | Description |
|--------|------|-------------|
| key | TEXT | Metadata key |
| value | TEXT | Metadata value |

#### `road_segments`
Stores road segment information with bounding boxes.

| Column | Type | Description |
|--------|------|-------------|
| id | INTEGER | Primary key |
| osm_way_id | INTEGER | OSM way ID |
| name | TEXT | Road name (nullable) |
| highway_type | TEXT | Highway classification |
| speed_limit_kmh | INTEGER | Speed limit in km/h |
| is_inferred | INTEGER | 1 if inferred, 0 if explicit |
| min_lat | REAL | Minimum latitude |
| max_lat | REAL | Maximum latitude |
| min_lon | REAL | Minimum longitude |
| max_lon | REAL | Maximum longitude |
| center_lat | REAL | Center latitude |
| center_lon | REAL | Center longitude |

#### `road_geometry`
Stores detailed road coordinates.

| Column | Type | Description |
|--------|------|-------------|
| id | INTEGER | Primary key |
| road_segment_id | INTEGER | Foreign key to road_segments |
| sequence | INTEGER | Point order in geometry |
| latitude | REAL | Point latitude |
| longitude | REAL | Point longitude |

#### `spatial_grid`
Spatial grid index for fast location queries.

| Column | Type | Description |
|--------|------|-------------|
| grid_x | INTEGER | Grid X coordinate (0-999) |
| grid_y | INTEGER | Grid Y coordinate (0-999) |
| road_segment_id | INTEGER | Foreign key to road_segments |

## Querying the Database

### Example: Find Roads Near a Location

```sql
-- Find roads near Cape Town (lat: -33.9249, lon: 18.4241)
SELECT rs.name, rs.highway_type, rs.speed_limit_kmh, rs.is_inferred
FROM road_segments rs
WHERE rs.center_lat BETWEEN -33.9349 AND -33.9149
  AND rs.center_lon BETWEEN 18.4141 AND 18.4341
ORDER BY (rs.center_lat - (-33.9249)) * (rs.center_lat - (-33.9249)) +
         (rs.center_lon - 18.4241) * (rs.center_lon - 18.4241)
LIMIT 10;
```

### Example: Using Spatial Grid

```sql
-- Step 1: Calculate grid cell for location
-- For a location at (lat, lon), calculate:
--   grid_x = floor((lon - min_lon) / (max_lon - min_lon) * 1000)
--   grid_y = floor((lat - min_lat) / (max_lat - min_lat) * 1000)

-- Step 2: Query using grid
SELECT DISTINCT rs.*
FROM spatial_grid sg
JOIN road_segments rs ON sg.road_segment_id = rs.id
WHERE sg.grid_x BETWEEN 450 AND 452
  AND sg.grid_y BETWEEN 320 AND 322;
```

## Speed Limit Inference Logic

When explicit `maxspeed` tags are not present, speed limits are inferred based on highway type:

### South Africa (ZA)
- **motorway / trunk**: 120 km/h
- **primary**: 100 km/h
- **secondary / tertiary**: 80 km/h
- **residential / unclassified**: 60 km/h
- **living_street / service**: 40 km/h

### Australia (AU)
- **motorway / trunk**: 110 km/h
- **primary**: 100 km/h
- **secondary / tertiary**: 80 km/h
- **unclassified**: 60 km/h
- **residential**: 50 km/h
- **living_street / service**: 40 km/h

### Special Cases
- **"none"**: Treated as national speed limit (120 ZA / 110 AU)
- **"walk"**: 5 km/h
- **mph values**: Automatically converted to km/h

## Performance Characteristics

### South Africa
- **Download**: ~245 MB
- **Processing time**: 8-12 minutes
- **Database size**: 50-100 MB
- **Road segments**: ~85,000
- **Memory usage**: 2-3 GB RAM

### Australia
- **Download**: ~1.5-2 GB
- **Processing time**: 45-75 minutes
- **Database size**: 300-500 MB
- **Road segments**: ~500,000+
- **Memory usage**: 3-4 GB RAM

## Troubleshooting

### Out of Memory Errors

**Symptom**: Application crashes with `OutOfMemoryException` during Pass 1.

**Solution**:
- Ensure you're running the 64-bit version (`PlatformTarget: x64` in .csproj)
- Close other memory-intensive applications
- Increase system virtual memory (page file)
- Minimum 16GB RAM recommended for Australia

### Download Failures

**Symptom**: HTTP errors or incomplete downloads.

**Solution**:
- Check internet connection
- Verify Geofabrik URL is accessible: https://download.geofabrik.de/
- Increase `RetryAttempts` in `appsettings.json`
- Manually download PBF file to `data/downloads/` directory

### Slow Database Creation

**Symptom**: Database creation takes very long.

**Solution**:
- This is normal for large datasets (Australia can take 30-45 minutes)
- Ensure SSD storage for better performance
- Do not interrupt the process during transaction commit

### No Roads Extracted

**Symptom**: Validation shows 0 road segments.

**Solution**:
- Verify PBF file is not corrupted (check file size)
- Ensure OsmSharp package is correctly installed
- Check for errors in console output during extraction

## Architecture

### Two-Pass Algorithm

The application uses a memory-efficient two-pass approach:

1. **Pass 1**: Reads entire PBF file and collects all nodes into a dictionary (node ID → coordinates)
2. **Pass 2**: Reads PBF file again, processes ways, and builds road segments using node dictionary

This approach trades speed for memory efficiency, avoiding the need to stream and filter nodes dynamically.

### Spatial Grid Indexing

The 1000×1000 grid provides efficient location-based queries:

- **Cell Size**: ~1.4km × 1.5km (varies by latitude)
- **Query Strategy**: Calculate grid cell from (lat, lon) → lookup road segments → filter by distance
- **Trade-off**: Small overhead in database size for significant query performance gains

## License

This project is provided as-is for data acquisition and processing purposes.

OSM data is © OpenStreetMap contributors, licensed under ODbL: https://www.openstreetmap.org/copyright

## Support

For issues, questions, or contributions, please refer to the project documentation or contact the maintainer.

## Acknowledgments

- **OpenStreetMap**: For comprehensive global mapping data
- **Geofabrik**: For providing regularly updated OSM extracts
- **OsmSharp**: For excellent OSM data parsing library
