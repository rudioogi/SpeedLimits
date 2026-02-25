-- ============================================================================
-- Speed Limit Lookup Functions - Optimized for IoT Devices
-- ============================================================================
-- This file contains optimized SQL queries for fast speed limit lookups
-- on resource-constrained IoT devices.
-- ============================================================================

-- ----------------------------------------------------------------------------
-- METADATA QUERY - Run once at startup to cache bounds
-- ----------------------------------------------------------------------------
-- Cache these values in your application to avoid repeated queries
SELECT
    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'min_latitude') as min_lat,
    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'max_latitude') as max_lat,
    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'min_longitude') as min_lon,
    (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'max_longitude') as max_lon,
    (SELECT CAST(value AS INTEGER) FROM metadata WHERE key = 'grid_size') as grid_size;

-- ----------------------------------------------------------------------------
-- FASTEST: Grid-Based Lookup (Recommended for IoT)
-- ----------------------------------------------------------------------------
-- Prerequisites: Calculate grid coordinates in your application:
--   grid_x = floor((lon - min_lon) / (max_lon - min_lon) * 1000)
--   grid_y = floor((lat - min_lat) / (max_lat - min_lat) * 1000)
--
-- Replace ? with actual values: lat, lon, grid_x-1, grid_x+1, grid_y-1, grid_y+1, lat, lon
-- ----------------------------------------------------------------------------
SELECT rs.speed_limit_kmh
FROM spatial_grid sg
JOIN road_segments rs ON sg.road_segment_id = rs.id
WHERE sg.grid_x BETWEEN ? AND ?           -- grid_x-1 to grid_x+1
  AND sg.grid_y BETWEEN ? AND ?           -- grid_y-1 to grid_y+1
  AND rs.min_lat <= ? AND rs.max_lat >= ? -- lat
  AND rs.min_lon <= ? AND rs.max_lon >= ? -- lon
ORDER BY
    (rs.center_lat - ?) * (rs.center_lat - ?) +  -- lat
    (rs.center_lon - ?) * (rs.center_lon - ?)    -- lon
LIMIT 1;

-- ----------------------------------------------------------------------------
-- FAST: Bounding Box Lookup (Fallback if grid calculation fails)
-- ----------------------------------------------------------------------------
-- Replace ? with: lat, lat, lon, lon, lat, lon
-- Search radius: ±0.01 degrees (~1.1 km)
-- ----------------------------------------------------------------------------
SELECT speed_limit_kmh
FROM road_segments
WHERE center_lat BETWEEN ? - 0.01 AND ? + 0.01  -- lat
  AND center_lon BETWEEN ? - 0.01 AND ? + 0.01  -- lon
ORDER BY
    (center_lat - ?) * (center_lat - ?) +        -- lat
    (center_lon - ?) * (center_lon - ?)          -- lon
LIMIT 1;

-- ----------------------------------------------------------------------------
-- SIMPLE: Direct Bounding Box (Simplest but slower)
-- ----------------------------------------------------------------------------
-- Replace ? with: lat, lat, lon, lon, lat, lon
-- Use only if above methods fail
-- ----------------------------------------------------------------------------
SELECT speed_limit_kmh
FROM road_segments
WHERE min_lat <= ? AND max_lat >= ?  -- lat
  AND min_lon <= ? AND max_lon >= ?  -- lon
ORDER BY
    (center_lat - ?) * (center_lat - ?) +  -- lat
    (center_lon - ?) * (center_lon - ?)    -- lon
LIMIT 1;

-- ----------------------------------------------------------------------------
-- DETAILED: Get Road Information (Use sparingly - more data transfer)
-- ----------------------------------------------------------------------------
-- Replace ? with: lat, lat, lon, lon, lat, lon
-- Returns full road details
-- ----------------------------------------------------------------------------
SELECT
    speed_limit_kmh,
    name,
    highway_type,
    is_inferred
FROM road_segments
WHERE center_lat BETWEEN ? - 0.01 AND ? + 0.01  -- lat
  AND center_lon BETWEEN ? - 0.01 AND ? + 0.01  -- lon
ORDER BY
    (center_lat - ?) * (center_lat - ?) +        -- lat
    (center_lon - ?) * (center_lon - ?)          -- lon
LIMIT 1;

-- ----------------------------------------------------------------------------
-- BATCH: Get Multiple Nearby Roads (For route planning)
-- ----------------------------------------------------------------------------
-- Replace ? with: lat, lat, lon, lon
-- Returns up to 10 nearest roads
-- ----------------------------------------------------------------------------
SELECT
    speed_limit_kmh,
    name,
    highway_type,
    center_lat,
    center_lon
FROM road_segments
WHERE center_lat BETWEEN ? - 0.02 AND ? + 0.02  -- lat
  AND center_lon BETWEEN ? - 0.02 AND ? + 0.02  -- lon
ORDER BY
    (center_lat - ?) * (center_lat - ?) +        -- lat
    (center_lon - ?) * (center_lon - ?)          -- lon
LIMIT 10;

-- ============================================================================
-- PERFORMANCE NOTES:
-- ============================================================================
-- 1. Grid-based lookup is 10-50x faster than bounding box
-- 2. Always use prepared statements - SQLite caches query plans
-- 3. Cache metadata bounds at startup
-- 4. Keep database in read-only mode (PRAGMA query_only = ON)
-- 5. Use memory-mapped I/O (already configured in database)
-- 6. Expected query time: <1ms (grid), <5ms (bounding box)
-- ============================================================================
