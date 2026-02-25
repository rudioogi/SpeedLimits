/*
 * Speed Limit Lookup - Optimized C Implementation for IoT Devices
 *
 * Compile: gcc -o speedlimit speedlimit_lookup.c -lsqlite3 -lm
 * Usage: ./speedlimit za_speedlimits.db -33.9249 18.4241
 */

#include <stdio.h>
#include <stdlib.h>
#include <sqlite3.h>
#include <math.h>

typedef struct {
    double min_lat;
    double max_lat;
    double min_lon;
    double max_lon;
    int grid_size;
} DatabaseBounds;

typedef struct {
    sqlite3 *db;
    DatabaseBounds bounds;
    sqlite3_stmt *grid_stmt;
    sqlite3_stmt *bbox_stmt;
} SpeedLimitContext;

/*
 * Initialize the speed limit lookup system
 * Call once at startup
 */
int speedlimit_init(SpeedLimitContext *ctx, const char *db_path) {
    int rc;

    // Open database in read-only mode
    rc = sqlite3_open_v2(db_path, &ctx->db,
                         SQLITE_OPEN_READONLY, NULL);
    if (rc != SQLITE_OK) {
        fprintf(stderr, "Cannot open database: %s\n", sqlite3_errmsg(ctx->db));
        return rc;
    }

    // Load metadata bounds (cached for grid calculations)
    sqlite3_stmt *meta_stmt;
    const char *meta_sql =
        "SELECT "
        "  (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'min_latitude'),"
        "  (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'max_latitude'),"
        "  (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'min_longitude'),"
        "  (SELECT CAST(value AS REAL) FROM metadata WHERE key = 'max_longitude'),"
        "  (SELECT CAST(value AS INTEGER) FROM metadata WHERE key = 'grid_size')";

    rc = sqlite3_prepare_v2(ctx->db, meta_sql, -1, &meta_stmt, NULL);
    if (rc != SQLITE_OK) return rc;

    if (sqlite3_step(meta_stmt) == SQLITE_ROW) {
        ctx->bounds.min_lat = sqlite3_column_double(meta_stmt, 0);
        ctx->bounds.max_lat = sqlite3_column_double(meta_stmt, 1);
        ctx->bounds.min_lon = sqlite3_column_double(meta_stmt, 2);
        ctx->bounds.max_lon = sqlite3_column_double(meta_stmt, 3);
        ctx->bounds.grid_size = sqlite3_column_int(meta_stmt, 4);
    }
    sqlite3_finalize(meta_stmt);

    // Prepare grid-based query (FASTEST - use this for real-time lookups)
    const char *grid_sql =
        "SELECT rs.speed_limit_kmh "
        "FROM spatial_grid sg "
        "JOIN road_segments rs ON sg.road_segment_id = rs.id "
        "WHERE sg.grid_x BETWEEN ?1 AND ?2 "
        "  AND sg.grid_y BETWEEN ?3 AND ?4 "
        "  AND rs.min_lat <= ?5 AND rs.max_lat >= ?5 "
        "  AND rs.min_lon <= ?6 AND rs.max_lon >= ?6 "
        "ORDER BY "
        "    (rs.center_lat - ?5) * (rs.center_lat - ?5) + "
        "    (rs.center_lon - ?6) * (rs.center_lon - ?6) "
        "LIMIT 1";

    rc = sqlite3_prepare_v2(ctx->db, grid_sql, -1, &ctx->grid_stmt, NULL);
    if (rc != SQLITE_OK) return rc;

    // Prepare bounding box query (FALLBACK - simpler but slower)
    const char *bbox_sql =
        "SELECT speed_limit_kmh "
        "FROM road_segments "
        "WHERE center_lat BETWEEN ?1 - 0.01 AND ?1 + 0.01 "
        "  AND center_lon BETWEEN ?2 - 0.01 AND ?2 + 0.01 "
        "ORDER BY "
        "    (center_lat - ?1) * (center_lat - ?1) + "
        "    (center_lon - ?2) * (center_lon - ?2) "
        "LIMIT 1";

    rc = sqlite3_prepare_v2(ctx->db, bbox_sql, -1, &ctx->bbox_stmt, NULL);
    if (rc != SQLITE_OK) return rc;

    return SQLITE_OK;
}

/*
 * Calculate grid coordinates from GPS position
 */
void calculate_grid_coords(SpeedLimitContext *ctx, double lat, double lon,
                          int *grid_x, int *grid_y) {
    double norm_x = (lon - ctx->bounds.min_lon) /
                    (ctx->bounds.max_lon - ctx->bounds.min_lon);
    double norm_y = (lat - ctx->bounds.min_lat) /
                    (ctx->bounds.max_lat - ctx->bounds.min_lat);

    *grid_x = (int)(norm_x * ctx->bounds.grid_size);
    *grid_y = (int)(norm_y * ctx->bounds.grid_size);

    // Clamp to valid range
    if (*grid_x < 0) *grid_x = 0;
    if (*grid_x >= ctx->bounds.grid_size) *grid_x = ctx->bounds.grid_size - 1;
    if (*grid_y < 0) *grid_y = 0;
    if (*grid_y >= ctx->bounds.grid_size) *grid_y = ctx->bounds.grid_size - 1;
}

/*
 * Lookup speed limit using grid-based query (FASTEST)
 * Returns: speed limit in km/h, or -1 if not found
 */
int speedlimit_lookup_grid(SpeedLimitContext *ctx, double lat, double lon) {
    int grid_x, grid_y;
    calculate_grid_coords(ctx, lat, lon, &grid_x, &grid_y);

    // Reset prepared statement
    sqlite3_reset(ctx->grid_stmt);
    sqlite3_clear_bindings(ctx->grid_stmt);

    // Bind parameters: grid_x-1, grid_x+1, grid_y-1, grid_y+1, lat, lon
    sqlite3_bind_int(ctx->grid_stmt, 1, grid_x - 1);
    sqlite3_bind_int(ctx->grid_stmt, 2, grid_x + 1);
    sqlite3_bind_int(ctx->grid_stmt, 3, grid_y - 1);
    sqlite3_bind_int(ctx->grid_stmt, 4, grid_y + 1);
    sqlite3_bind_double(ctx->grid_stmt, 5, lat);
    sqlite3_bind_double(ctx->grid_stmt, 6, lon);

    int speed_limit = -1;
    if (sqlite3_step(ctx->grid_stmt) == SQLITE_ROW) {
        speed_limit = sqlite3_column_int(ctx->grid_stmt, 0);
    }

    return speed_limit;
}

/*
 * Lookup speed limit using bounding box query (FALLBACK)
 * Returns: speed limit in km/h, or -1 if not found
 */
int speedlimit_lookup_bbox(SpeedLimitContext *ctx, double lat, double lon) {
    sqlite3_reset(ctx->bbox_stmt);
    sqlite3_clear_bindings(ctx->bbox_stmt);

    sqlite3_bind_double(ctx->bbox_stmt, 1, lat);
    sqlite3_bind_double(ctx->bbox_stmt, 2, lon);

    int speed_limit = -1;
    if (sqlite3_step(ctx->bbox_stmt) == SQLITE_ROW) {
        speed_limit = sqlite3_column_int(ctx->bbox_stmt, 0);
    }

    return speed_limit;
}

/*
 * Lookup speed limit (tries grid first, falls back to bbox)
 * Returns: speed limit in km/h, or -1 if not found
 */
int speedlimit_lookup(SpeedLimitContext *ctx, double lat, double lon) {
    int speed_limit = speedlimit_lookup_grid(ctx, lat, lon);

    // Fallback to bounding box if grid lookup fails
    if (speed_limit == -1) {
        speed_limit = speedlimit_lookup_bbox(ctx, lat, lon);
    }

    return speed_limit;
}

/*
 * Cleanup - call at shutdown
 */
void speedlimit_cleanup(SpeedLimitContext *ctx) {
    if (ctx->grid_stmt) sqlite3_finalize(ctx->grid_stmt);
    if (ctx->bbox_stmt) sqlite3_finalize(ctx->bbox_stmt);
    if (ctx->db) sqlite3_close(ctx->db);
}

/*
 * Example usage
 */
int main(int argc, char *argv[]) {
    if (argc != 4) {
        printf("Usage: %s <database.db> <latitude> <longitude>\n", argv[0]);
        printf("Example: %s Database/za_speedlimits.db -33.9249 18.4241\n", argv[0]);
        return 1;
    }

    const char *db_path = argv[1];
    double lat = atof(argv[2]);
    double lon = atof(argv[3]);

    SpeedLimitContext ctx = {0};

    // Initialize (do this once at startup)
    if (speedlimit_init(&ctx, db_path) != SQLITE_OK) {
        fprintf(stderr, "Failed to initialize\n");
        return 1;
    }

    printf("Looking up speed limit for: %.6f, %.6f\n", lat, lon);

    // Lookup speed limit
    int speed_limit = speedlimit_lookup(&ctx, lat, lon);

    if (speed_limit != -1) {
        printf("Speed limit: %d km/h\n", speed_limit);
    } else {
        printf("No road found at this location\n");
    }

    // Cleanup
    speedlimit_cleanup(&ctx);

    return 0;
}
