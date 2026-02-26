# Plan: Implement Full Polygon Method for Reverse Geocoding

## Context
Currently the reverse geocoder determines suburb/city by finding the **nearest place node** (a single lat/lon point). This is inaccurate — a query point can be inside suburb A but closer to the center-point of suburb B. The "full polygon method" extracts actual administrative boundary polygons from OSM data, stores them in the database, and uses point-in-polygon ray casting to determine exactly which suburb/city contains a coordinate. Falls back to nearest-point when no containing polygon exists.

## Files to Create
| File | Purpose |
|------|---------|
| `Models/PlaceBoundary.cs` | Model holding an extracted boundary polygon |

## Files to Modify
| File | Change |
|------|--------|
| `Services/OsmRoadExtractor.cs` | Add Pass 2.5 (relations) + Pass 3 (boundary ways) + polygon assembly |
| `Services/DatabaseBuilder.cs` | Add `place_boundaries` table (blob-serialised polygons) + insert logic |
| `Services/ReverseGeocoder.cs` | Add polygon-first lookup with point-in-polygon; fall back to nearest point |
| `Program.cs` | Pass `extractor.PlaceBoundaries` to builder |
| `SpeedLimits.Api/Controllers/DataAcquisitionController.cs` | Same — pass boundaries to builder |

## Implementation Details

### 1. `Models/PlaceBoundary.cs` (NEW)
```
PlaceBoundary { OsmRelationId, Name, BoundaryType, AdminLevel, List<GeoPoint> Polygon, MinLat/MaxLat/MinLon/MaxLon }
```

### 2. `Services/OsmRoadExtractor.cs`
**Add `PlaceBoundaries` property** (same pattern as `PlaceNodes`).

**Modify `ExtractRoadSegments`** — after the road-yield loop completes, run boundary extraction:
```
Pass 1 (existing): Collect all node coordinates + place nodes
Pass 2 (modified): Process ways for roads AND collect boundary relations (relations come after ways in PBF)
Pass 3 (new): Re-stream PBF, collect node-lists ONLY for boundary member ways (using a HashSet<long> of needed way IDs)
Assembly: Connect ways end-to-end into closed polygon rings, resolve node IDs → GeoPoint coordinates
```

**Boundary relation filter:**
- `type=boundary` AND `boundary=administrative` AND `admin_level` >= 6
- OR relation has `place=city/town/suburb/neighbourhood/village/hamlet`
- Must have a `name` tag

**Boundary type determination:**
- If relation has `place=*` tag -> use it directly
- Else infer from admin_level: 6-7 -> "city", 8-10 -> "suburb"

**Ring assembly algorithm:**
1. Collect outer-role member ways for each relation
2. Start with first way's node list
3. Repeatedly find an unused way whose first or last node matches the ring's last node; append (reversing if needed)
4. If ring closes (first node == last node), accept; otherwise discard

### 3. `Services/DatabaseBuilder.cs`
**New table:**
```sql
CREATE TABLE place_boundaries (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    osm_relation_id INTEGER NOT NULL,
    name TEXT NOT NULL,
    boundary_type TEXT NOT NULL,
    admin_level INTEGER NOT NULL,
    min_lat REAL, max_lat REAL, min_lon REAL, max_lon REAL,
    polygon_blob BLOB NOT NULL
);
CREATE INDEX idx_place_boundaries_bbox
    ON place_boundaries(boundary_type, min_lat, max_lat, min_lon, max_lon);
```

**Blob format:** `[int32 vertex_count][double lat1][double lon1]...` — 4 + N*16 bytes.

**`BuildDatabase` signature** gains `IReadOnlyList<PlaceBoundary>? placeBoundaries = null`.

### 4. `Services/ReverseGeocoder.cs`
**Constructor:** check `TableExists("place_boundaries")` -> `_hasBoundariesTable`.

**Lookup flow (suburb example):**
1. If `_hasBoundariesTable`: query candidates whose bounding box contains the point, ordered by area ASC (smallest first)
2. For each candidate: deserialise blob -> `List<GeoPoint>`, run ray-casting point-in-polygon
3. First hit wins -> set `result.Suburb`, `SuburbType`, `SuburbDistanceM = 0`
4. If no polygon hit AND `_hasPlacesTable`: fall back to existing nearest-point query
5. Same logic for city

**Point-in-polygon (ray casting):**
```
for each edge (i, j): if ray from point crosses edge, toggle inside flag
```

### 5. `Program.cs` + `DataAcquisitionController.cs`
Single-line change each — pass `extractor.PlaceBoundaries` as 4th arg to `BuildDatabase`.

## Verification
1. `dotnet build` — both projects compile
2. Rebuild a database: run console app -> menu 1 -> process ZA (or a small test region)
3. Console app -> menu 6 (reverse geocode) -> should now show suburb/city via polygon containment
4. API `GET /api/reversegeocode?country=za&lat=-33.9249&lon=18.4241` -> suburb + city populated
5. API `POST /api/reversegeocode/batch` -> same for batch
6. Old databases without `place_boundaries` table should still work (falls back to nearest-point)
