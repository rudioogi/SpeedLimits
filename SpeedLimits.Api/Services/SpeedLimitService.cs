using Microsoft.Data.Sqlite;
using OsmDataAcquisition.Models;
using SpeedLimits.Api.Models;

namespace SpeedLimits.Api.Services;

/// <summary>
/// Queries speed limit data from a SQLite database.
/// </summary>
public class SpeedLimitService
{
    private readonly DatabasePathResolver _pathResolver;

    public SpeedLimitService(DatabasePathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// Returns up to <paramref name="limit"/> road segments nearest to the given coordinates.
    /// </summary>
    public List<NearbyRoad> LookupNearby(string dbPath, double lat, double lon, int limit = 5)
    {
        var results = new List<NearbyRoad>();
        var queryPoint = new GeoPoint(lat, lon);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            SELECT speed_limit_kmh, name, highway_type, is_inferred, center_lat, center_lon
            FROM road_segments
            WHERE center_lat BETWEEN @lat - 0.02 AND @lat + 0.02
              AND center_lon BETWEEN @lon - 0.02 AND @lon + 0.02
            ORDER BY
                (center_lat - @lat) * (center_lat - @lat) +
                (center_lon - @lon) * (center_lon - @lon)
            LIMIT @limit";

        cmd.Parameters.AddWithValue("@lat", lat);
        cmd.Parameters.AddWithValue("@lon", lon);
        cmd.Parameters.AddWithValue("@limit", limit);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var centerPoint = new GeoPoint(reader.GetDouble(4), reader.GetDouble(5));
            results.Add(new NearbyRoad
            {
                SpeedLimitKmh = reader.GetInt32(0),
                Name = reader.IsDBNull(1) ? null : reader.GetString(1),
                HighwayType = reader.GetString(2),
                IsInferred = reader.GetBoolean(3),
                CenterLatitude = reader.GetDouble(4),
                CenterLongitude = reader.GetDouble(5),
                DistanceMeters = queryPoint.DistanceTo(centerPoint)
            });
        }

        return results;
    }

    /// <summary>
    /// Tests a fixed set of known locations for both ZA and AU and returns results.
    /// </summary>
    public List<KnownLocationResult> TestKnownLocations()
    {
        var results = new List<KnownLocationResult>();

        var knownLocations = new[]
        {
            // South Africa
            (CountryCode: "ZA", Lat: -33.9249, Lon: 18.4241, Name: "Cape Town N1"),
            (CountryCode: "ZA", Lat: -26.2041, Lon: 28.0473, Name: "Johannesburg M1"),
            (CountryCode: "ZA", Lat: -33.9258, Lon: 18.4232, Name: "Cape Town Residential"),
            // Australia
            (CountryCode: "AU", Lat: -33.8688, Lon: 151.2093, Name: "Sydney M1"),
            (CountryCode: "AU", Lat: -37.8136, Lon: 144.9631, Name: "Melbourne M1"),
            (CountryCode: "AU", Lat: -33.8675, Lon: 151.2070, Name: "Sydney Residential")
        };

        foreach (var loc in knownLocations)
        {
            var dbPath = _pathResolver.GetCountryDbPath(loc.CountryCode);
            var roads = dbPath != null
                ? LookupNearby(dbPath, loc.Lat, loc.Lon, limit: 5)
                : new List<NearbyRoad>();

            results.Add(new KnownLocationResult
            {
                LocationName = loc.Name,
                Latitude = loc.Lat,
                Longitude = loc.Lon,
                CountryCode = loc.CountryCode,
                NearbyRoads = roads
            });
        }

        return results;
    }
}
