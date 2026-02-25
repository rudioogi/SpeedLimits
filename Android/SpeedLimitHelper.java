package com.yourapp.speedlimit;

import android.content.Context;
import android.database.Cursor;
import android.database.sqlite.SQLiteDatabase;
import android.database.sqlite.SQLiteOpenHelper;
import android.util.Log;

/**
 * Efficient speed limit lookup for Android IoT devices
 * Optimized for sub-millisecond queries on low-power hardware
 *
 * Usage:
 *   SpeedLimitHelper helper = SpeedLimitHelper.getInstance(context);
 *   int speedLimit = helper.getSpeedLimit(-33.9249, 18.4241);
 */
public class SpeedLimitHelper {
    private static final String TAG = "SpeedLimitHelper";
    private static SpeedLimitHelper instance;

    private final SQLiteDatabase db;
    private final DatabaseBounds bounds;

    // Cached prepared statement strings (compiled by SQLite)
    private static final String QUERY_GRID =
        "SELECT rs.speed_limit_kmh " +
        "FROM spatial_grid sg " +
        "JOIN road_segments rs ON sg.road_segment_id = rs.id " +
        "WHERE sg.grid_x BETWEEN ? AND ? " +
        "  AND sg.grid_y BETWEEN ? AND ? " +
        "  AND rs.min_lat <= ? AND rs.max_lat >= ? " +
        "  AND rs.min_lon <= ? AND rs.max_lon >= ? " +
        "ORDER BY " +
        "    (rs.center_lat - ?) * (rs.center_lat - ?) + " +
        "    (rs.center_lon - ?) * (rs.center_lon - ?) " +
        "LIMIT 1";

    private static final String QUERY_BBOX =
        "SELECT speed_limit_kmh " +
        "FROM road_segments " +
        "WHERE center_lat BETWEEN ? AND ? " +
        "  AND center_lon BETWEEN ? AND ? " +
        "ORDER BY " +
        "    (center_lat - ?) * (center_lat - ?) + " +
        "    (center_lon - ?) * (center_lon - ?) " +
        "LIMIT 1";

    /**
     * Cached database bounds for grid calculations
     */
    private static class DatabaseBounds {
        final double minLat;
        final double maxLat;
        final double minLon;
        final double maxLon;
        final int gridSize;

        DatabaseBounds(double minLat, double maxLat, double minLon, double maxLon, int gridSize) {
            this.minLat = minLat;
            this.maxLat = maxLat;
            this.minLon = minLon;
            this.maxLon = maxLon;
            this.gridSize = gridSize;
        }
    }

    /**
     * Get singleton instance (thread-safe)
     */
    public static synchronized SpeedLimitHelper getInstance(Context context) {
        if (instance == null) {
            instance = new SpeedLimitHelper(context.getApplicationContext());
        }
        return instance;
    }

    /**
     * Private constructor - use getInstance()
     */
    private SpeedLimitHelper(Context context) {
        // Open database in read-only mode
        String dbPath = context.getDatabasePath("za_speedlimits.db").getAbsolutePath();
        db = SQLiteDatabase.openDatabase(dbPath, null,
            SQLiteDatabase.OPEN_READONLY | SQLiteDatabase.NO_LOCALIZED_COLLATORS);

        // Configure for performance
        optimizeDatabase();

        // Load and cache metadata bounds (one-time query)
        bounds = loadBounds();

        Log.i(TAG, "SpeedLimitHelper initialized. Bounds: " +
            String.format("[%.2f,%.2f] x [%.2f,%.2f], Grid: %dx%d",
            bounds.minLat, bounds.maxLat, bounds.minLon, bounds.maxLon,
            bounds.gridSize, bounds.gridSize));
    }

    /**
     * Configure SQLite for optimal read performance
     */
    private void optimizeDatabase() {
        db.rawQuery("PRAGMA query_only = ON", null).close();
        db.rawQuery("PRAGMA temp_store = MEMORY", null).close();
        db.rawQuery("PRAGMA mmap_size = 67108864", null).close(); // 64MB
        db.rawQuery("PRAGMA cache_size = -8000", null).close();   // 8MB
    }

    /**
     * Load database bounds from metadata (called once at startup)
     */
    private DatabaseBounds loadBounds() {
        Cursor cursor = db.rawQuery(
            "SELECT " +
            "  (SELECT value FROM metadata WHERE key = 'min_latitude')," +
            "  (SELECT value FROM metadata WHERE key = 'max_latitude')," +
            "  (SELECT value FROM metadata WHERE key = 'min_longitude')," +
            "  (SELECT value FROM metadata WHERE key = 'max_longitude')," +
            "  (SELECT value FROM metadata WHERE key = 'grid_size')", null);

        try {
            if (cursor.moveToFirst()) {
                return new DatabaseBounds(
                    cursor.getDouble(0),  // minLat
                    cursor.getDouble(1),  // maxLat
                    cursor.getDouble(2),  // minLon
                    cursor.getDouble(3),  // maxLon
                    cursor.getInt(4)      // gridSize
                );
            }
        } finally {
            cursor.close();
        }

        throw new RuntimeException("Failed to load database bounds");
    }

    /**
     * Calculate grid coordinates from GPS position
     */
    private int[] calculateGridCoords(double lat, double lon) {
        double normX = (lon - bounds.minLon) / (bounds.maxLon - bounds.minLon);
        double normY = (lat - bounds.minLat) / (bounds.maxLat - bounds.minLat);

        int gridX = (int)(normX * bounds.gridSize);
        int gridY = (int)(normY * bounds.gridSize);

        // Clamp to valid range
        gridX = Math.max(0, Math.min(bounds.gridSize - 1, gridX));
        gridY = Math.max(0, Math.min(bounds.gridSize - 1, gridY));

        return new int[]{gridX, gridY};
    }

    /**
     * Get speed limit using grid-based lookup (FASTEST - <1ms)
     *
     * @param lat Latitude (-90 to 90)
     * @param lon Longitude (-180 to 180)
     * @return Speed limit in km/h, or -1 if not found
     */
    public int getSpeedLimit(double lat, double lon) {
        // Try grid-based lookup first (fastest)
        int speedLimit = getSpeedLimitGrid(lat, lon);

        // Fallback to bounding box if grid lookup fails
        if (speedLimit == -1) {
            speedLimit = getSpeedLimitBBox(lat, lon);
        }

        return speedLimit;
    }

    /**
     * Grid-based lookup (FASTEST - use this for real-time)
     * Typical execution time: 0.3-1ms on Android
     */
    private int getSpeedLimitGrid(double lat, double lon) {
        int[] gridCoords = calculateGridCoords(lat, lon);
        int gridX = gridCoords[0];
        int gridY = gridCoords[1];

        Cursor cursor = db.rawQuery(QUERY_GRID, new String[]{
            String.valueOf(gridX - 1),  // grid_x min
            String.valueOf(gridX + 1),  // grid_x max
            String.valueOf(gridY - 1),  // grid_y min
            String.valueOf(gridY + 1),  // grid_y max
            String.valueOf(lat),        // lat for bounds check
            String.valueOf(lat),        // lat for bounds check
            String.valueOf(lon),        // lon for bounds check
            String.valueOf(lon),        // lon for bounds check
            String.valueOf(lat),        // lat for distance calc
            String.valueOf(lat),        // lat for distance calc
            String.valueOf(lon),        // lon for distance calc
            String.valueOf(lon)         // lon for distance calc
        });

        try {
            if (cursor.moveToFirst()) {
                return cursor.getInt(0);
            }
        } finally {
            cursor.close();
        }

        return -1;
    }

    /**
     * Bounding box lookup (FALLBACK - 2-5ms)
     * Used when grid lookup fails (edge cases, very sparse areas)
     */
    private int getSpeedLimitBBox(double lat, double lon) {
        final double SEARCH_RADIUS = 0.01; // ~1km

        Cursor cursor = db.rawQuery(QUERY_BBOX, new String[]{
            String.valueOf(lat - SEARCH_RADIUS),  // min_lat
            String.valueOf(lat + SEARCH_RADIUS),  // max_lat
            String.valueOf(lon - SEARCH_RADIUS),  // min_lon
            String.valueOf(lon + SEARCH_RADIUS),  // max_lon
            String.valueOf(lat),                  // lat for distance
            String.valueOf(lat),                  // lat for distance
            String.valueOf(lon),                  // lon for distance
            String.valueOf(lon)                   // lon for distance
        });

        try {
            if (cursor.moveToFirst()) {
                return cursor.getInt(0);
            }
        } finally {
            cursor.close();
        }

        return -1;
    }

    /**
     * Get detailed road information (use sparingly - more overhead)
     *
     * @return RoadInfo object or null if not found
     */
    public RoadInfo getRoadInfo(double lat, double lon) {
        final double SEARCH_RADIUS = 0.01;

        String query =
            "SELECT speed_limit_kmh, name, highway_type, is_inferred " +
            "FROM road_segments " +
            "WHERE center_lat BETWEEN ? AND ? " +
            "  AND center_lon BETWEEN ? AND ? " +
            "ORDER BY " +
            "    (center_lat - ?) * (center_lat - ?) + " +
            "    (center_lon - ?) * (center_lon - ?) " +
            "LIMIT 1";

        Cursor cursor = db.rawQuery(query, new String[]{
            String.valueOf(lat - SEARCH_RADIUS),
            String.valueOf(lat + SEARCH_RADIUS),
            String.valueOf(lon - SEARCH_RADIUS),
            String.valueOf(lon + SEARCH_RADIUS),
            String.valueOf(lat),
            String.valueOf(lat),
            String.valueOf(lon),
            String.valueOf(lon)
        });

        try {
            if (cursor.moveToFirst()) {
                return new RoadInfo(
                    cursor.getInt(0),                                    // speed_limit_kmh
                    cursor.isNull(1) ? null : cursor.getString(1),      // name
                    cursor.getString(2),                                 // highway_type
                    cursor.getInt(3) == 1                                // is_inferred
                );
            }
        } finally {
            cursor.close();
        }

        return null;
    }

    /**
     * Close database connection (call in onDestroy)
     */
    public void close() {
        if (db != null && db.isOpen()) {
            db.close();
        }
    }

    /**
     * Road information container
     */
    public static class RoadInfo {
        public final int speedLimitKmh;
        public final String name;
        public final String highwayType;
        public final boolean isInferred;

        RoadInfo(int speedLimitKmh, String name, String highwayType, boolean isInferred) {
            this.speedLimitKmh = speedLimitKmh;
            this.name = name;
            this.highwayType = highwayType;
            this.isInferred = isInferred;
        }

        @Override
        public String toString() {
            return String.format("%s [%s] %d km/h%s",
                name != null ? name : "(unnamed)",
                highwayType,
                speedLimitKmh,
                isInferred ? " (inferred)" : "");
        }
    }
}
