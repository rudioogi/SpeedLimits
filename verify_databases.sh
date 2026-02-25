#!/bin/bash
# Verify pre-built databases are valid and contain expected data

echo "=================================="
echo "Database Verification Script"
echo "=================================="
echo ""

# Check if databases exist
if [ ! -f "Database/za_speedlimits.db" ]; then
    echo "❌ Error: Database/za_speedlimits.db not found"
    echo "   Please place the South Africa database in the Database/ folder"
    exit 1
fi

if [ ! -f "Database/au_speedlimits.db" ]; then
    echo "⚠️  Warning: Database/au_speedlimits.db not found"
    echo "   Australia database is optional but recommended"
fi

echo "📁 Files found:"
ls -lh Database/*.db 2>/dev/null
echo ""

# Verify South Africa database
echo "=================================="
echo "South Africa Database"
echo "=================================="

sqlite3 Database/za_speedlimits.db "SELECT value FROM metadata WHERE key='country_name';" 2>/dev/null || {
    echo "❌ Error: Cannot read za_speedlimits.db - file may be corrupted"
    exit 1
}

ROAD_COUNT=$(sqlite3 Database/za_speedlimits.db "SELECT COUNT(*) FROM road_segments;")
GRID_CELLS=$(sqlite3 Database/za_speedlimits.db "SELECT COUNT(DISTINCT grid_x || '_' || grid_y) FROM spatial_grid;")
EXPLICIT_COUNT=$(sqlite3 Database/za_speedlimits.db "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 0;")
MIN_LAT=$(sqlite3 Database/za_speedlimits.db "SELECT value FROM metadata WHERE key='min_latitude';")
MAX_LAT=$(sqlite3 Database/za_speedlimits.db "SELECT value FROM metadata WHERE key='max_latitude';")

echo "Road segments: $ROAD_COUNT"
echo "Grid cells: $GRID_CELLS"
echo "Explicit speed limits: $EXPLICIT_COUNT"
echo "Latitude range: $MIN_LAT to $MAX_LAT"

if [ "$ROAD_COUNT" -lt 50000 ]; then
    echo "⚠️  Warning: Road count seems low (expected ~85,000)"
elif [ "$ROAD_COUNT" -gt 150000 ]; then
    echo "⚠️  Warning: Road count seems high (expected ~85,000)"
else
    echo "✅ Road count looks good"
fi

# Test a known location
echo ""
echo "Testing known location: Cape Town N1 (-33.9249, 18.4241)"
SPEED=$(sqlite3 Database/za_speedlimits.db "
SELECT speed_limit_kmh
FROM road_segments
WHERE center_lat BETWEEN -33.9249 - 0.01 AND -33.9249 + 0.01
  AND center_lon BETWEEN 18.4241 - 0.01 AND 18.4241 + 0.01
ORDER BY (center_lat - (-33.9249)) * (center_lat - (-33.9249)) +
         (center_lon - 18.4241) * (center_lon - 18.4241)
LIMIT 1;")

if [ -n "$SPEED" ]; then
    echo "✅ Found speed limit: $SPEED km/h"
else
    echo "⚠️  No road found at test location (database may be incomplete)"
fi

# Verify Australia database if it exists
if [ -f "Database/au_speedlimits.db" ]; then
    echo ""
    echo "=================================="
    echo "Australia Database"
    echo "=================================="

    AU_ROAD_COUNT=$(sqlite3 Database/au_speedlimits.db "SELECT COUNT(*) FROM road_segments;")
    AU_GRID_CELLS=$(sqlite3 Database/au_speedlimits.db "SELECT COUNT(DISTINCT grid_x || '_' || grid_y) FROM spatial_grid;")

    echo "Road segments: $AU_ROAD_COUNT"
    echo "Grid cells: $AU_GRID_CELLS"

    if [ "$AU_ROAD_COUNT" -lt 300000 ]; then
        echo "⚠️  Warning: Road count seems low (expected ~500,000+)"
    else
        echo "✅ Road count looks good"
    fi

    # Test Sydney location
    echo ""
    echo "Testing known location: Sydney M1 (-33.8688, 151.2093)"
    AU_SPEED=$(sqlite3 Database/au_speedlimits.db "
    SELECT speed_limit_kmh
    FROM road_segments
    WHERE center_lat BETWEEN -33.8688 - 0.01 AND -33.8688 + 0.01
      AND center_lon BETWEEN 151.2093 - 0.01 AND 151.2093 + 0.01
    ORDER BY (center_lat - (-33.8688)) * (center_lat - (-33.8688)) +
             (center_lon - 151.2093) * (center_lon - 151.2093)
    LIMIT 1;")

    if [ -n "$AU_SPEED" ]; then
        echo "✅ Found speed limit: $AU_SPEED km/h"
    else
        echo "⚠️  No road found at test location"
    fi
fi

# Check database integrity
echo ""
echo "=================================="
echo "Integrity Checks"
echo "=================================="

ZA_INTEGRITY=$(sqlite3 Database/za_speedlimits.db "PRAGMA integrity_check;")
if [ "$ZA_INTEGRITY" = "ok" ]; then
    echo "✅ South Africa database: OK"
else
    echo "❌ South Africa database: CORRUPTED"
    echo "   $ZA_INTEGRITY"
fi

if [ -f "Database/au_speedlimits.db" ]; then
    AU_INTEGRITY=$(sqlite3 Database/au_speedlimits.db "PRAGMA integrity_check;")
    if [ "$AU_INTEGRITY" = "ok" ]; then
        echo "✅ Australia database: OK"
    else
        echo "❌ Australia database: CORRUPTED"
        echo "   $AU_INTEGRITY"
    fi
fi

echo ""
echo "=================================="
echo "Verification Complete"
echo "=================================="
echo ""
echo "Databases are ready to use!"
echo ""
echo "Quick test commands:"
echo "  C:     gcc -o speedlimit speedlimit_lookup.c -lsqlite3 -lm"
echo "         ./speedlimit Database/za_speedlimits.db -33.9249 18.4241"
echo ""
echo "  SQL:   sqlite3 Database/za_speedlimits.db"
echo "         SELECT * FROM metadata;"
