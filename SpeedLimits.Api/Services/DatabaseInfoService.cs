using Microsoft.Data.Sqlite;
using SpeedLimits.Api.Models;

namespace SpeedLimits.Api.Services;

/// <summary>
/// Queries database metadata, statistics and validation information.
/// </summary>
public class DatabaseInfoService
{
    public ValidationResult GetValidation(string dbPath, string countryCode)
    {
        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            var total = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments");
            var explicit_ = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 0");
            var inferred = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 1");
            var gridCells = GetScalar<long>(connection, "SELECT COUNT(DISTINCT grid_x || '_' || grid_y) FROM spatial_grid");

            long placeCount = 0, cities = 0, suburbs = 0;
            try
            {
                placeCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM places");
                if (placeCount > 0)
                {
                    cities = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('city', 'town')");
                    suburbs = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')");
                }
            }
            catch (SqliteException) { /* places table absent in older databases */ }

            var speedDistribution = QuerySpeedDistribution(connection);
            var highwayDistribution = QueryHighwayDistribution(connection);
            var metadata = QueryMetadata(connection);

            return new ValidationResult
            {
                CountryCode = countryCode,
                IsValid = true,
                TotalRoadSegments = total,
                ExplicitSpeedLimits = explicit_,
                ExplicitPercent = total > 0 ? explicit_ * 100.0 / total : 0,
                InferredSpeedLimits = inferred,
                InferredPercent = total > 0 ? inferred * 100.0 / total : 0,
                GridCellsPopulated = gridCells,
                TotalPlaceNodes = placeCount,
                CitiesAndTowns = cities,
                SuburbsAndVillages = suburbs,
                SpeedLimitDistribution = speedDistribution,
                HighwayTypeDistribution = highwayDistribution,
                Metadata = metadata
            };
        }
        catch (Exception ex)
        {
            return new ValidationResult
            {
                CountryCode = countryCode,
                IsValid = false,
                Error = ex.Message
            };
        }
    }

    public DatabaseStatistics GetStatistics(string dbPath, string countryCode, string countryName)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        var fileInfo = new FileInfo(dbPath);
        var total = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments");
        var explicit_ = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 0");
        var inferred = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 1");
        var gridCells = GetScalar<long>(connection, "SELECT COUNT(DISTINCT grid_x || '_' || grid_y) FROM spatial_grid");

        long placeCount = 0, cities = 0, suburbs = 0;
        try
        {
            placeCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM places");
            if (placeCount > 0)
            {
                cities = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('city', 'town')");
                suburbs = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')");
            }
        }
        catch (SqliteException) { /* places table absent in older databases */ }

        return new DatabaseStatistics
        {
            CountryCode = countryCode,
            CountryName = countryName,
            FilePath = dbPath,
            FileSizeMb = fileInfo.Length / (1024.0 * 1024.0),
            TotalRoadSegments = total,
            ExplicitSpeedLimits = explicit_,
            ExplicitPercent = total > 0 ? explicit_ * 100.0 / total : 0,
            InferredSpeedLimits = inferred,
            InferredPercent = total > 0 ? inferred * 100.0 / total : 0,
            GridCellsPopulated = gridCells,
            TotalPlaceNodes = placeCount,
            CitiesAndTowns = cities,
            SuburbsAndVillages = suburbs,
            Metadata = QueryMetadata(connection)
        };
    }

    private static List<SpeedLimitBucket> QuerySpeedDistribution(SqliteConnection connection)
    {
        var results = new List<SpeedLimitBucket>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT speed_limit_kmh, COUNT(*) as count
            FROM road_segments
            GROUP BY speed_limit_kmh
            ORDER BY speed_limit_kmh";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new SpeedLimitBucket { SpeedLimitKmh = reader.GetInt32(0), Count = reader.GetInt64(1) });
        return results;
    }

    private static List<HighwayTypeBucket> QueryHighwayDistribution(SqliteConnection connection)
    {
        var results = new List<HighwayTypeBucket>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT highway_type, COUNT(*) as count
            FROM road_segments
            GROUP BY highway_type
            ORDER BY count DESC
            LIMIT 10";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            results.Add(new HighwayTypeBucket { HighwayType = reader.GetString(0), Count = reader.GetInt64(1) });
        return results;
    }

    private static Dictionary<string, string> QueryMetadata(SqliteConnection connection)
    {
        var metadata = new Dictionary<string, string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT key, value FROM metadata";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            metadata[reader.GetString(0)] = reader.GetString(1);
        return metadata;
    }

    private static T GetScalar<T>(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value
            ? default(T)!
            : (T)Convert.ChangeType(result, typeof(T));
    }
}
