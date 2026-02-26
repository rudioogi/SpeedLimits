# OSM Speed Limit & Reverse Geocoding System

A .NET 8.0 application that builds offline, GPS-based lookup databases from OpenStreetMap data. Given any GPS coordinate, it can return the **speed limit**, **street name**, **suburb**, and **city/town** — entirely offline, with sub-5ms query times. Designed for IoT devices, dashcams, and fleet tracking.

## How It Works — The Big Picture

### The Data Source: OpenStreetMap

All data comes from [OpenStreetMap](https://www.openstreetmap.org/) (OSM), a community-maintained map of the entire world. OSM stores geographic data as three types of elements:

- **Nodes** — individual points on the map with a latitude/longitude. Every intersection, traffic light, and place label is a node. Critically, OSM tags nodes with metadata like `place=city` + `name=Cape Town`, which is how we know where cities and suburbs are located.
- **Ways** — ordered sequences of nodes that form lines or shapes. Every road is a way, tagged with metadata like `highway=residential`, `maxspeed=60`, and `name=Main Road`.
- **Relations** — groups of ways/nodes that form complex structures. Administrative boundary relations define the polygon outlines of cities, towns, and suburbs.

[Geofabrik](https://download.geofabrik.de/) provides regularly updated country-level extracts of OSM data in `.osm.pbf` format (a compressed binary format). The South Africa extract is ~245 MB; Australia is ~1.5 GB.

### What Gets Extracted

The application reads each PBF file in a **multi-pass scan**:

**Pass 1 — Collect all nodes:**
Every node in the file is read and its coordinates are stored in a dictionary (node ID to lat/lon). During this same pass, the system also identifies **place nodes** — nodes tagged with `place=city`, `place=town`, `place=suburb`, `place=village`, `place=hamlet`, or `place=neighbourhood` — and collects them separately. These are the named geographic areas used as fallback positions for reverse geocoding.

**Pass 2 — Process roads:**
Every way tagged as a routable road (`highway=motorway`, `highway=residential`, etc.) is processed. The system looks up the coordinates of each node in the way to reconstruct the road's geometry, extracts the speed limit (from the `maxspeed` tag, or infers it from the road type), and calculates the road's bounding box and center point.

**Pass 3 — Extract boundary polygons:**
Administrative boundary relations (tagged `boundary=administrative` or `type=boundary`) are collected. The outer ring of each boundary is assembled from its member ways and stored as a polygon. These polygons power **accurate point-in-polygon reverse geocoding** for suburb and city lookups.

### What Gets Stored

Everything is packed into a single SQLite database per country:

| Table | What it stores | Used for |
|-------|---------------|----------|
| `road_segments` | Every road's name, type, speed limit, and center coordinates | Speed limit lookup + street name lookup |
| `spatial_grid` | A grid index mapping geographic cells to road IDs | Fast spatial queries (which roads are near point X?) |
| `places` | Every city, town, suburb, village, hamlet, and neighbourhood node | Reverse geocoding fallback (nearest-point) |
| `place_boundaries` | Polygon outlines of administrative boundaries | Accurate point-in-polygon reverse geocoding |
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
2. **Suburb** — Query `place_boundaries` for suburb/neighbourhood polygons whose bounding box contains the point, then run a **ray-casting point-in-polygon test** on each candidate (smallest area first). Falls back to nearest place node within ~5.5km if no polygon contains the point.
3. **City** — Same polygon-first approach for city/town boundaries, falling back to nearest place node within ~33km.

Total time: **~1-5ms** for all three.

The response indicates which method was used: `SuburbType` will be e.g. `"suburb (polygon)"` for a polygon hit, or `"suburb"` for a nearest-point fallback.

#### Why Polygon Lookup Is Better
OSM boundary polygons trace the actual administrative borders of cities and suburbs. Point-in-polygon testing determines which area a coordinate is truly inside — no guessing based on proximity. For coordinates near suburb boundaries this is significantly more accurate than the nearest-node approach. The nearest-node fallback is retained for areas that don't have boundary data in OSM.

## Features

- **Offline operation** — no internet needed after database is built
- **Speed limit lookup** — explicit from OSM tags or inferred from road type
- **Reverse geocoding** — coordinates to street name, suburb, and city
- **Polygon accuracy** — point-in-polygon suburb/city lookup from OSM boundary relations
- **Sub-millisecond queries** — spatial grid indexing for IoT-grade performance
- **Two-country support** — South Africa and Australia (easily extensible)
- **IoT-ready standalone** — `speedlimit_lookup.cs` with prepared statements
- **REST API** — ASP.NET Core Web API with Swagger UI

## Requirements

- **.NET 8.0 SDK** or later
- **16GB RAM** recommended (for processing Australia)
- **5GB free disk space** (for downloads and databases)
- **Internet connection** (only for downloading OSM data)

## Quick Start

### Console App

```bash
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

### REST API

```bash
cd SpeedLimits.Api
dotnet run -c Release
```

Swagger UI is served at `http://localhost:5000` (or the configured port). All endpoints are listed and testable there.

## Menu Options (Console App)

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
  Suburb:  Gardens [suburb (polygon)]
  City:    Cape Town [city (polygon)]
─────────────────────────────────────────────────────────────
```

## REST API

The `SpeedLimits.Api` project exposes all console-app functionality as a REST API. Swagger UI is available at the root URL.

### Endpoints

#### Databases

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/databases` | List all configured databases and whether they exist on disk |
| `GET` | `/api/databases/{countryCode}/validate` | Validate database integrity and return stats (menu 2) |
| `GET` | `/api/databases/{countryCode}/statistics` | Database statistics for one country (menu 4) |
| `GET` | `/api/databases/statistics` | Statistics for all available databases |

#### Speed Limits

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/speedlimit?country=ZA&lat=...&lon=...` | Look up nearby roads and speed limits (menu 3) |
| `GET` | `/api/speedlimit/known-locations` | Test fixed reference locations for ZA and AU (menu 5) |

Query parameters for `/api/speedlimit`: `country` (required), `lat` (required), `lon` (required), `limit` (1–20, default 5).

#### Reverse Geocoding

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/reversegeocode?country=ZA&lat=...&lon=...` | Reverse geocode a single coordinate (menu 6) |
| `POST` | `/api/reversegeocode/batch` | Batch reverse geocode with per-item and total timing |

**Single lookup response:**
```json
{
  "queryLatitude": -33.9249,
  "queryLongitude": 18.4241,
  "countryCode": "ZA",
  "hasPlaceData": true,
  "street": "De Waal Drive",
  "highwayType": "primary",
  "streetDistanceMeters": 45.2,
  "suburb": "Gardens",
  "suburbType": "suburb (polygon)",
  "suburbDistanceMeters": 0,
  "city": "Cape Town",
  "cityType": "city (polygon)",
  "cityDistanceMeters": 0,
  "elapsedMs": 1.8
}
```

`distanceMeters` is `0` when the coordinate is inside a polygon; it reflects actual distance for nearest-point fallbacks.

**Batch request body:**
```json
{
  "countryCode": "ZA",
  "coordinates": [
    { "latitude": -33.9249, "longitude": 18.4241 },
    { "latitude": -29.6217, "longitude": 30.4003 }
  ]
}
```

**Batch response:**
```json
{
  "countryCode": "ZA",
  "requestCount": 2,
  "totalTimeMs": 3.4,
  "results": [ ... ]
}
```

#### Data Acquisition

| Method | Route | Description |
|--------|-------|-------------|
| `GET` | `/api/acquisition/countries` | List all configured countries |
| `POST` | `/api/acquisition/process` | Download and process OSM data (menu 1) |

**Process request body:**
```json
{ "countryCode": "ZA" }        // single country
{ "all": true }                 // all configured countries
```

> **Note:** `/api/acquisition/process` is a long-running operation — minutes for South Africa, hours for Australia. It blocks until complete.

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

Add an entry to the `Countries` array with the Geofabrik URL and default speed limits for that country's road types. Run option 1 (or `POST /api/acquisition/process`) to download and build.

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

### `place_boundaries`

| Column | Type | Description |
|--------|------|-------------|
| id | INTEGER | Primary key |
| osm_relation_id | INTEGER | OSM relation identifier |
| name | TEXT | Place name |
| boundary_type | TEXT | One of: city, town, suburb, neighbourhood, village, hamlet |
| admin_level | INTEGER | OSM admin_level value |
| min_lat, max_lat, min_lon, max_lon | REAL | Bounding box (for bbox pre-filter) |
| polygon_blob | BLOB | Compact binary polygon: `[int32 count][double lat₁][double lon₁]…` |

### `spatial_grid`

| Column | Type | Description |
|--------|------|-------------|
| grid_x, grid_y | INTEGER | Grid cell coordinates |
| road_segment_id | INTEGER | Foreign key to road_segments |

### `metadata`

Key-value pairs: `country_code`, `country_name`, `created_date`, `grid_size`, bounds, `osm_source`, `place_count`, `boundary_count`.

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
    PlaceBoundary.cs              — Boundary polygon model (city, suburb, etc.)
    PlaceNode.cs                  — Place node model (city, suburb, etc.)
    RoadSegment.cs                — Road segment model
    GeoPoint.cs                   — Lat/lon coordinate with distance calc
  Services/
    OsmDataDownloader.cs          — PBF file downloader with retry
    OsmRoadExtractor.cs           — Multi-pass PBF extraction (roads + places + boundaries)
    DatabaseBuilder.cs            — SQLite database builder
    ReverseGeocoder.cs            — Reverse geocoding (polygon-first with nearest-point fallback)
  Utilities/
    ValidationHelper.cs           — Database validation and reporting
    ConsoleProgressReporter.cs    — Console progress display
  SpeedLimits.Api/
    Program.cs                    — ASP.NET Core app host + Swagger setup
    appsettings.json              — API configuration
    Controllers/
      DatabaseController.cs       — GET /api/databases (list, validate, statistics)
      SpeedLimitController.cs     — GET /api/speedlimit (lookup, known-locations)
      ReverseGeocodeController.cs — GET /api/reversegeocode, POST /api/reversegeocode/batch
      DataAcquisitionController.cs — GET/POST /api/acquisition (countries, process)
    Models/
      DatabaseModels.cs           — DatabaseEntry, DatabaseStatistics, ValidationResult
      SpeedLimitModels.cs         — SpeedLimitLookupResult, KnownLocationResult
      GeocodeModels.cs            — ReverseGeocodeResponse, BatchReverseGeocodeRequest/Result
      AcquisitionModels.cs        — ProcessRequest, ProcessingResult, CountryInfo
    Services/
      DatabasePathResolver.cs     — Locates database files relative to app directory
      DatabaseInfoService.cs      — Validation and statistics queries
      SpeedLimitService.cs        — Speed limit lookup and known-location tests
  Database/                       — Pre-built databases (not in git)
  data/downloads/                 — Downloaded PBF files (not in git)
```

## Limitations

- **Suburb accuracy** — Polygon-based lookup is accurate when OSM boundary relations exist. For areas without boundary data, the system falls back to nearest place node, which may return an adjacent area near boundaries.
- **Inferred speed limits** — ~70-93% of speed limits are inferred from road type (depending on country). These are reasonable defaults but may not reflect actual posted limits.
- **Databases must be rebuilt** after code updates that change the schema. Old databases without the `places` or `place_boundaries` tables will still work for speed lookups but reverse geocoding will return "(not found)".
- **Acquisition API blocks** — `POST /api/acquisition/process` is synchronous and will hold the HTTP connection open for the full processing duration.

## Data Attribution

OSM data is copyright [OpenStreetMap contributors](https://www.openstreetmap.org/copyright), licensed under ODbL. Country extracts provided by [Geofabrik](https://download.geofabrik.de/).

## NuGet Packages

- **OsmSharp 6.2.0** — OSM PBF parsing
- **Microsoft.Data.Sqlite 8.0.0** — SQLite database
- **Microsoft.Extensions.Configuration 8.0.0** — Configuration management
- **Swashbuckle.AspNetCore 6.5.0** — Swagger/OpenAPI for the REST API
