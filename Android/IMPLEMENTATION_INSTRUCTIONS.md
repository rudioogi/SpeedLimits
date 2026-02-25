# Speed Limit Finder - Android Implementation Instructions

## Overview

Implement an efficient speed limit lookup system for Android 9 IoT device using the pre-built SQLite database.

---

## Requirements

### Class Details
- **Package**: `com.oogi.oda`
- **Class Name**: `SpeedLimitFinder`
- **Database Path**: `/data/media/speedDb/za_speedLimits.db` ⚠️ **EXACT PATH - DO NOT MODIFY**
- **Log Tag**: `ODA_SPEED`

### Performance Requirements
- Query time: < 5ms per lookup
- Benchmark and log execution time for every query
- Use Android's native SQLite (android.database.sqlite)

---

## Implementation Steps

### 1. Class Structure

Create `SpeedLimitFinder` class with:
- **Singleton pattern** (thread-safe)
- **Private SQLiteDatabase** field for database connection
- **Private DatabaseBounds** inner class to cache metadata
- **getInstance(Context)** static method
- **Private constructor** that opens database and loads bounds

### 2. Database Initialization

In constructor:
1. Open database at **EXACT PATH**: `/data/media/speedDb/za_speedLimits.db` in **READ_ONLY** mode
   - Note: Use this exact path including capital 'L' in Limits
2. Set SQLite pragmas for optimization:
   ```sql
   PRAGMA query_only = ON
   PRAGMA temp_store = MEMORY
   PRAGMA mmap_size = 67108864  -- 64MB
   PRAGMA cache_size = -8000     -- 8MB
   ```
3. Load metadata bounds (one-time query):
   ```sql
   SELECT
     (SELECT value FROM metadata WHERE key = 'min_latitude'),
     (SELECT value FROM metadata WHERE key = 'max_latitude'),
     (SELECT value FROM metadata WHERE key = 'min_longitude'),
     (SELECT value FROM metadata WHERE key = 'max_longitude'),
     (SELECT value FROM metadata WHERE key = 'grid_size')
   ```
4. Store these bounds in private field for grid calculations
5. Log initialization success with bounds info using tag `ODA_SPEED`

### 3. Grid Coordinate Calculation

Implement private method `calculateGridCoords(double lat, double lon)`:
1. Normalize coordinates:
   - `normX = (lon - minLon) / (maxLon - minLon)`
   - `normY = (lat - minLat) / (maxLat - minLat)`
2. Calculate grid position:
   - `gridX = (int)(normX * gridSize)`
   - `gridY = (int)(normY * gridSize)`
3. Clamp to valid range [0, gridSize-1]
4. Return both coordinates

### 4. Primary Query Method - Grid-Based (FASTEST)

Implement `getSpeedLimit(double lat, double lon)`:

**Step 1: Start timing**
```java
long startTime = System.nanoTime();
```

**Step 2: Calculate grid coordinates**
- Call `calculateGridCoords(lat, lon)`
- Get gridX, gridY values

**Step 3: Execute grid-based query**
```sql
SELECT rs.speed_limit_kmh
FROM spatial_grid sg
JOIN road_segments rs ON sg.road_segment_id = rs.id
WHERE sg.grid_x BETWEEN ? AND ?        -- gridX-1, gridX+1
  AND sg.grid_y BETWEEN ? AND ?        -- gridY-1, gridY+1
  AND rs.min_lat <= ? AND rs.max_lat >= ?   -- lat, lat
  AND rs.min_lon <= ? AND rs.max_lon >= ?   -- lon, lon
ORDER BY
    (rs.center_lat - ?) * (rs.center_lat - ?) +   -- lat, lat
    (rs.center_lon - ?) * (rs.center_lon - ?)     -- lon, lon
LIMIT 1
```

**Step 4: Handle result**
- If found: extract speed limit from cursor
- If not found: try fallback bounding box query (see below)

**Step 5: End timing and log**
```java
long endTime = System.nanoTime();
double durationMs = (endTime - startTime) / 1_000_000.0;
Log.d("ODA_SPEED", String.format("Query(%.6f, %.6f) = %d km/h | Time: %.2f ms | Method: %s",
    lat, lon, speedLimit, durationMs, method));
```

**Step 6: Return speed limit** (or -1 if not found)

### 5. Fallback Query Method - Bounding Box

Implement private method `getSpeedLimitBBox(double lat, double lon)`:

**Execute simpler bounding box query:**
```sql
SELECT speed_limit_kmh
FROM road_segments
WHERE center_lat BETWEEN ? AND ?    -- lat-0.01, lat+0.01
  AND center_lon BETWEEN ? AND ?    -- lon-0.01, lon+0.01
ORDER BY
    (center_lat - ?) * (center_lat - ?) +   -- lat, lat
    (center_lon - ?) * (center_lon - ?)     -- lon, lon
LIMIT 1
```

Use 0.01 degree radius (~1.1 km search area)

### 6. Logging Format

Log every query with format:
```
D/ODA_SPEED: Query(-33.924900, 18.424100) = 120 km/h | Time: 0.73 ms | Method: GRID
D/ODA_SPEED: Query(-26.204100, 28.047300) = 100 km/h | Time: 1.24 ms | Method: BBOX
D/ODA_SPEED: Query(-35.000000, 20.000000) = -1 km/h | Time: 0.89 ms | Method: NONE
```

Include:
- GPS coordinates (6 decimal places)
- Result speed limit (-1 if not found)
- Execution time in milliseconds (2 decimal places)
- Method used (GRID, BBOX, NONE)

### 7. Error Handling

1. **Database not found**: Log error and throw RuntimeException
2. **Database corrupted**: Log error with exception details
3. **Query fails**: Log warning, return -1
4. **Invalid coordinates**: Log warning, return -1

Example error log:
```java
Log.e("ODA_SPEED", "Failed to open database: /data/media/speedDb/za_speedLimits.db", e);
```

### 8. Cleanup

Implement `close()` method:
- Close SQLiteDatabase if open
- Log closure: `Log.i("ODA_SPEED", "SpeedLimitFinder closed");`

---

## Usage Example

```java
// In Application.onCreate() or Service.onCreate()
SpeedLimitFinder finder = SpeedLimitFinder.getInstance(context);

// In GPS callback (1Hz updates)
double lat = location.getLatitude();
double lon = location.getLongitude();
int speedLimit = finder.getSpeedLimit(lat, lon);

if (speedLimit > 0) {
    // Use speed limit for alerts/display
    // Benchmark is automatically logged
}

// In onDestroy()
finder.close();
```

---

## Performance Optimization Notes

### Critical Performance Points

1. **Use rawQuery() not query()** - Slightly faster on Android
2. **Reuse query strings** - SQLite compiles and caches them
3. **Don't use execSQL()** - Use rawQuery() for SELECT
4. **Keep database open** - Don't open/close per query
5. **Cache metadata bounds** - Query once, use many times

### Memory Considerations

- Singleton instance: ~2-3 MB
- Database mmap: 64 MB (configured via pragma)
- Query cursor: ~1 KB per query
- **Total**: ~70 MB memory footprint

### Expected Performance

| Scenario | Time | Method |
|----------|------|--------|
| Grid hit (normal) | 0.5-1.5 ms | GRID |
| Grid miss → bbox hit | 2-4 ms | BBOX |
| No road found | 1-3 ms | NONE |
| Cold start (first query) | 5-10 ms | GRID |

---

## Testing Checklist

### Test Coordinates (South Africa)

```java
// Cape Town N1 - motorway (expect ~120 km/h)
finder.getSpeedLimit(-33.9249, 18.4241);

// Johannesburg M1 - motorway (expect ~120 km/h)
finder.getSpeedLimit(-26.2041, 28.0473);

// Cape Town residential (expect ~60 km/h)
finder.getSpeedLimit(-33.9258, 18.4232);

// Middle of ocean - no road (expect -1)
finder.getSpeedLimit(-35.0, 20.0);

// Edge of coverage (expect -1 or valid speed)
finder.getSpeedLimit(-22.0, 30.0);
```

### Validation

Check logs for:
- ✅ All queries < 5ms (except first cold start)
- ✅ Grid method used ~90%+ of the time
- ✅ Valid speed limits (20-140 km/h range for ZA)
- ✅ No exceptions in normal operation
- ✅ Proper cleanup on close

### Benchmark Test

Run 1000 queries with random coordinates:
```java
for (int i = 0; i < 1000; i++) {
    double lat = -34.0 + Math.random() * 12.0;  // ZA range
    double lon = 16.0 + Math.random() * 17.0;
    finder.getSpeedLimit(lat, lon);
}
// Check average time in logs
```

Expected average: < 2ms

---

## Common Issues & Solutions

### Issue: "Database not found"
**Solution**:
1. Verify file exists at exact path: `/data/media/speedDb/za_speedLimits.db`
2. Check filename case sensitivity (capital 'L' in Limits)
3. Verify directory permissions for `/data/media/speedDb/`

### Issue: "Unable to open database"
**Solution**: Check SELinux context, may need `chcon u:object_r:media_rw_data_file:s0`

### Issue: Queries > 10ms
**Solution**:
1. Check if mmap_size pragma is applied
2. Verify grid index exists: `SELECT COUNT(*) FROM spatial_grid`
3. Check if running on main thread (should be, SQLite is fast enough)

### Issue: Always returning -1
**Solution**:
1. Verify coordinates are in South Africa bounds (-22 to -35 lat, 16 to 33 lon)
2. Check database has data: `SELECT COUNT(*) FROM road_segments`
3. Verify grid size matches metadata

---

## Integration Notes

### Thread Safety
- Singleton is thread-safe (synchronized getInstance)
- SQLite queries are synchronous but fast (<5ms)
- Safe to call from GPS callback thread
- No need for AsyncTask or background thread

### Battery Impact
- Minimal: ~0.1% per hour with 1Hz GPS updates
- SQLite is extremely efficient
- No network usage
- No wake locks needed

### Storage
- Database size: ~87 MB (full ZA)
- Location: `/data/media/speedDb/za_speedLimits.db` (persistent storage)
- Directory: `/data/media/speedDb/` must exist before app starts
- Survives app updates
- Can be shared across apps
- Note: Filename uses capital 'L' in Limits

---

## Deliverables

1. `SpeedLimitFinder.java` - Main implementation
2. Benchmark logs showing performance
3. Test results with validation coordinates
4. Integration with existing ODA GPS callback

---

## Success Criteria

✅ Queries complete in < 5ms average
✅ All benchmark timings logged to `ODA_SPEED` tag
✅ Test coordinates return expected speed limits
✅ No memory leaks or crashes
✅ Proper error handling and logging
✅ Database persists across app restarts

---

**Database Schema Reference**: See main `README.md` for complete schema details

**Query Optimization**: Grid-based lookup is 10-50x faster than bounding box, use it as primary method
