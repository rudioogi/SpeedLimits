# Pre-Built Speed Limit Databases

This folder contains pre-built SQLite databases with speed limit data extracted from OpenStreetMap.

## Files

- **`za_speedlimits.db`** - South Africa speed limit database
  - Size: ~50-100 MB
  - Roads: ~85,000 segments
  - Coverage: Complete South Africa

- **`au_speedlimits.db`** - Australia speed limit database
  - Size: ~300-500 MB
  - Roads: ~500,000+ segments
  - Coverage: Complete Australia

## Database Contents

Each database contains:
- Road segments with speed limits (explicit or inferred)
- GPS coordinates (latitude/longitude)
- Road names and types
- Spatial grid index (1000×1000) for fast lookups
- Metadata (bounds, creation date, source)

## Usage

### Quick Lookup Example

```bash
# Using sqlite3 command line (replace coordinates)
sqlite3 Database/za_speedlimits.db "
SELECT speed_limit_kmh
FROM road_segments
WHERE center_lat BETWEEN -33.9249 - 0.01 AND -33.9249 + 0.01
  AND center_lon BETWEEN 18.4241 - 0.01 AND 18.4241 + 0.01
ORDER BY (center_lat - (-33.9249)) * (center_lat - (-33.9249)) +
         (center_lon - 18.4241) * (center_lon - 18.4241)
LIMIT 1;"
```

### Application Usage

See `speedlimit_lookup.c`, `speedlimit_lookup.cs`, or `IOT_USAGE_GUIDE.md` for optimized lookup implementations.

## Data Source

- **Source:** OpenStreetMap (© OpenStreetMap contributors)
- **License:** ODbL (Open Database License)
- **Downloaded from:** Geofabrik (https://download.geofabrik.de/)
- **Processing:** See `OsmDataAcquisition` project

## Updating Databases

To regenerate with fresh OSM data:

```bash
# Run the data acquisition tool
dotnet run

# Copy newly generated databases here
cp data/za_speedlimits.db Database/
cp data/au_speedlimits.db Database/
```

**Note:** OSM data is typically updated weekly. Regenerate databases periodically for the latest road information.

## Database Schema

See main `README.md` for complete schema documentation.

### Quick Schema Overview

```sql
-- Main tables
metadata         -- Country info and bounds
road_segments    -- Road data with speed limits
road_geometry    -- Detailed coordinate points
spatial_grid     -- 1000×1000 spatial index

-- Example query
SELECT * FROM metadata;
SELECT COUNT(*) FROM road_segments;
SELECT COUNT(*) FROM spatial_grid;
```

## Performance

- **Lookup time:** <1ms using spatial grid
- **Memory usage:** ~5MB for connection
- **Query methods:** Grid-based (fastest) or bounding box (fallback)

See `IOT_USAGE_GUIDE.md` for complete performance optimization guide.

## File Permissions (Production Deployment)

```bash
# Set read-only for security
chmod 444 Database/*.db

# Or restrict to specific user
chown root:appuser Database/*.db
chmod 440 Database/*.db
```

## Validation

To verify database integrity:

```bash
sqlite3 Database/za_speedlimits.db "PRAGMA integrity_check;"
sqlite3 Database/za_speedlimits.db "SELECT COUNT(*) FROM road_segments;"
sqlite3 Database/za_speedlimits.db "SELECT * FROM metadata;"
```

Expected output:
- **South Africa:** ~85,000 road segments
- **Australia:** ~500,000+ road segments

## Support

For issues with the databases or to report data errors, refer to the main project documentation.

---

**These databases are ready to use - no build process required!**
