# OSM Speed Limit & Reverse Geocoding System

A .NET 8.0 application that builds offline, GPS-based lookup databases from OpenStreetMap data. Given any GPS coordinate, it can return the **speed limit**, **street name**, **suburb**, and **city/town** — entirely offline, with sub-5ms query times. Designed for IoT devices, dashcams, and fleet tracking.

## How It Works — The Big Picture

### The Data Source: OpenStreetMap

All data comes from [OpenStreetMap](https://www.openstreetmap.org/) (OSM), a community-maintained map of the entire world. OSM stores geographic data as three types of elements:

- **Nodes** — individual points on the map with a latitude/longitude. Every intersection, traffic light, and place label is a node. Critically, OSM tags nodes with metadata like `place=city` + `name=Cape Town`, which is how we know where cities and suburbs are located.
- **Ways** — ordered sequences of nodes that form lines or shapes. Every road is a way, tagged with metadata like `highway=residential`, `maxspeed=60`, and `name=Main Road`.
- **Relations** — groups of ways/nodes that form complex structures (boundaries, routes). Not used by this system.

[Geofabrik](https://download.geofabrik.de/) provides regularly updated country-level extracts of OSM data in `.osm.pbf` format (a compressed binary format). The South Africa extract is ~245 MB; Australia is ~1.5 GB.

### What Gets Extracted

The application reads each PBF file in a **two-pass scan**:

**Pass 1 — Collect all nodes:**
Every node in the file is read and its coordinates are stored in a dictionary (node ID to lat/lon). During this same pass, the system also identifies **place nodes** — nodes tagged with `place=city`, `place=town`, `place=suburb`, `place=village`, `place=hamlet`, or `place=neighbourhood` — and collects them separately. These are the named geographic areas that enable reverse geocoding.

**Pass 2 — Process roads:**
Every way tagged as a routable road (`highway=motorway`, `highway=residential`, etc.) is processed. The system looks up the coordinates of each node in the way to reconstruct the road's geometry, extracts the speed limit (from the `maxspeed` tag, or infers it from the road type), and calculates the road's bounding box and center point.

### What Gets Stored

Everything is packed into a single SQLite database per country:

| Table | What it stores | Used for |
|-------|---------------|----------|
| `road_segments` | Every road's name, type, speed limit, and center coordinates | Speed limit lookup + street name lookup |
| `spatial_grid` | A grid index mapping geographic cells to road IDs | Fast spatial queries (which roads are near point X?) |
| `places` | Every city, town, suburb, village, hamlet, and neighbourhood node | Reverse geocoding (suburb + city lookup) |
| `metadata` | Country info, bounds, creation date, statistics | Validation and diagnostics |

### How Lookups Work

All lookups follow the same fundamental principle: **bounding-box query + distance ordering**.

#### Speed Limit Lookup
Given GPS coordinates `(lat, lon)`:
1. Calculate which grid cell the point falls in
2. Query `spatial_grid` for road segment IDs in that cell (and adjacent cells)
3. Filter `road_segments` where the point falls within the road's bounding box
4. Order by squared distance from the road's center point to the query point
5. Return the speed limit of the nearest road

This takes **<1ms** because the grid index narrows millions of roads down to a handful of candidates.

#### Reverse Geocoding (Street / Suburb / City)
Given GPS coordinates `(lat, lon)`, three independent queries run:

1. **Street** — Find the nearest **named road** in `road_segments` within ~550m. This uses the existing road data; no extra table needed.
2. **Suburb** — Find the nearest **suburb/neighbourhood/village/hamlet node** in `places` within ~5.5km. OSM place nodes are point markers at the center of named areas.
3. **City** — Find the nearest **city/town node** in `places` within ~33km.

Each query uses a bounding-box filter on latitude/longitude (which the index accelerates), then orders by distance. Total time: **~1-5ms** for all three.

#### Why "Nearest Point" Works
OSM place nodes are positioned at the recognized center of each area. A suburb node for "Gardens" sits roughly in the middle of the Gardens suburb. By finding the nearest suburb node to your GPS position, you get the suburb you're most likely in. This is simpler than polygon-based containment testing and works well for most use cases, though edge cases near suburb boundaries may return an adjacent area.

## Features

- **Offline operation** — no internet needed after database is built
- **Speed limit lookup** — explicit from OSM tags or inferred from road type
- **Reverse geocoding** — coordinates to street name, suburb, and city
- **Sub-millisecond queries** — spatial grid indexing for IoT-grade performance
- **Two-country support** — South Africa and Australia (easily extensible)
- **IoT-ready API** — standalone `speedlimit_lookup.cs` with prepared statements

## Requirements

- **.NET 8.0 SDK** or later
- **16GB RAM** recommended (for processing Australia)
- **5GB free disk space** (for downloads and databases)
- **Internet connection** (only for downloading OSM data)

## Quick Start

```bash
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

## Menu Options

```
1. Download and Process OSM Data    — Downloads PBF + builds database
2. Validate Pre-Built Databases     — Checks database integrity and stats
3. Test Location Lookup             — Speed limit lookup by coordinates
4. View Database Statistics         — Road/place counts, distributions
5. Test Known Locations             — Predefined test coordinates
6. Reverse Geocode                  — Coordinates to street/suburb/city
7. Exit
```

### Coordinate Input Format

Options 3 and 6 accept coordinates as a single comma-separated value, which you can paste directly from Google Maps:

```
Enter coordinates as lat,lon (e.g. -33.9249,18.4241): -29.621687,30.400331
```

### Example Reverse Geocode Output

```
Results:
─────────────────────────────────────────────────────────────
  Street:  Main Road [primary] (120m away)
  Suburb:  Gardens [suburb] (450m away)
  City:    Cape Town [city] (2,300m away)
─────────────────────────────────────────────────────────────
```

## Configuration

Edit `appsettings.json`:

```json
{
  "DataAcquisition": {
    "Countries": [
      {
        "Code": "ZA",
        "Name": "South Africa",
        "GeofabrikUrl": "https://download.geofabrik.de/africa/south-africa-latest.osm.pbf",
        "DefaultSpeedLimits": {
          "motorway": 120, "trunk": 120, "primary": 100,
          "secondary": 80, "tertiary": 80, "residential": 60
        }
      }
    ],
    "DownloadDirectory": "data/downloads",
    "DatabaseDirectory": "data"
  }
}
```

### Adding a New Country

Add an entry to the `Countries` array with the Geofabrik URL and default speed limits for that country's road types. Run option 1 to download and build.

## Database Schema

### `road_segments`

| Column | Type | Description |
|--------|------|-------------|
| id | INTEGER | Primary key |
| osm_way_id | INTEGER | OSM way identifier |
| name | TEXT | Road name (null if unnamed) |
| highway_type | TEXT | Road classification (motorway, residential, etc.) |
| speed_limit_kmh | INTEGER | Speed limit in km/h |
| is_inferred | INTEGER | 0 = from OSM `maxspeed` tag, 1 = inferred from road type |
| center_lat, center_lon | REAL | Center point of the road segment |
| min_lat, max_lat, min_lon, max_lon | REAL | Bounding box |

### `places`

| Column | Type | Description |
|--------|------|-------------|
| id | INTEGER | Primary key |
| osm_node_id | INTEGER | OSM node identifier |
| name | TEXT | Place name (e.g. "Gardens", "Cape Town") |
| place_type | TEXT | One of: city, town, suburb, village, hamlet, neighbourhood |
| latitude, longitude | REAL | Coordinates of the place node |

### `spatial_grid`

| Column | Type | Description |
|--------|------|-------------|
| grid_x, grid_y | INTEGER | Grid cell coordinates |
| road_segment_id | INTEGER | Foreign key to road_segments |

### `metadata`

Key-value pairs: `country_code`, `country_name`, `created_date`, `grid_size`, bounds, `osm_source`, `place_count`.

## IoT Integration

The standalone `speedlimit_lookup.cs` is designed to be dropped into any .NET IoT project:

```csharp
using var lookup = new SpeedLimitLookup("za_speedlimits.db");

// Speed limit lookup (<1ms)
int speed = lookup.GetSpeedLimit(-33.9249, 18.4241);

// Detailed road info
RoadInfo? road = lookup.GetRoadInfo(-33.9249, 18.4241);

// Reverse geocode — street, suburb, city (~1-5ms)
LocationInfo? location = lookup.GetLocationInfo(-33.9249, 18.4241);
if (location != null)
    Console.WriteLine(location); // "Main Road, Gardens, Cape Town"
```

The `GetLocationInfo()` method is backward compatible — it returns `null` on databases that don't have the `places` table.

## Speed Limit Inference

When roads don't have an explicit `maxspeed` tag in OSM, the speed limit is inferred from the road type using country-specific defaults:

| Road Type | South Africa | Australia |
|-----------|-------------|-----------|
| motorway / trunk | 120 km/h | 110 km/h |
| primary | 100 km/h | 100 km/h |
| secondary / tertiary | 80 km/h | 80 km/h |
| residential | 60 km/h | 50 km/h |
| living_street / service | 40 km/h | 40 km/h |

Special values: `"none"` = national limit, `"walk"` = 5 km/h, mph values are auto-converted.

## File Structure

```
SpeedLimits/
  Program.cs                      — Console app with menu system
  appsettings.json                — Country configs and DB settings
  speedlimit_lookup.cs            — Standalone IoT lookup API
  Models/
    PlaceNode.cs                  — Place node model (city, suburb, etc.)
    RoadSegment.cs                — Road segment model
    GeoPoint.cs                   — Lat/lon coordinate with distance calc
  Services/
    OsmDataDownloader.cs          — PBF file downloader with retry
    OsmRoadExtractor.cs           — Two-pass PBF extraction (roads + places)
    DatabaseBuilder.cs            — SQLite database builder
    ReverseGeocoder.cs            — Reverse geocoding service
  Utilities/
    ValidationHelper.cs           — Database validation and reporting
    ConsoleProgressReporter.cs    — Console progress display
  Database/                       — Pre-built databases (not in git)
  data/downloads/                 — Downloaded PBF files (not in git)
```

## Limitations

- **Suburb accuracy** — Suburbs are identified by nearest OSM place node, not polygon boundaries. Results near suburb edges may return an adjacent area.
- **Inferred speed limits** — ~70-93% of speed limits are inferred from road type (depending on country). These are reasonable defaults but may not reflect actual posted limits.
- **Databases must be rebuilt** after code updates that change the schema. Old databases without the `places` table will still work for speed lookups but reverse geocoding will return "(not found)".

## Data Attribution

OSM data is copyright [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), licensed under ODbL. Country extracts provided by [Geofabrik](https://download.geofabrik.de/).

## NuGet Packages

- **OsmSharp 6.2.0** — OSM PBF parsing
- **Microsoft.Data.Sqlite 8.0.0** — SQLite database
- **Microsoft.Extensions.Configuration 8.0.0** — Configuration management
