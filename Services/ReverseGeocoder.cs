using Microsoft.Data.Sqlite;
using OsmDataAcquisition.Models;

namespace OsmDataAcquisition.Services;

/// <summary>
/// Reverse geocodes GPS coordinates to street/suburb/city using the speed limit database
/// </summary>
public class ReverseGeocoder : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteCommand _streetCmd;
    private readonly SqliteCommand _suburbCmd;
    private readonly SqliteCommand _cityCmd;
    private readonly bool _hasPlacesTable;

    // Search radii in degrees (approximate):
    // ~550m at equator  = 0.005 degrees
    // ~5.5km at equator = 0.05 degrees
    // ~33km at equator  = 0.3 degrees
    private const double StreetRadiusDeg = 0.005;
    private const double SuburbRadiusDeg = 0.05;
    private const double CityRadiusDeg = 0.3;

    public ReverseGeocoder(string databasePath)
    {
        _connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        _connection.Open();

        // Check if places table exists (backward compatibility)
        _hasPlacesTable = TableExists("places");

        // Prepared query: nearest named road within ~550m
        _streetCmd = _connection.CreateCommand();
        _streetCmd.CommandText = @"
            SELECT name, highway_type, center_lat, center_lon
            FROM road_segments
            WHERE name IS NOT NULL
              AND center_lat BETWEEN @lat - @radius AND @lat + @radius
              AND center_lon BETWEEN @lon - @radius AND @lon + @radius
            ORDER BY
                (center_lat - @lat) * (center_lat - @lat) +
                (center_lon - @lon) * (center_lon - @lon)
            LIMIT 1";
        _streetCmd.Parameters.Add("@lat", SqliteType.Real);
        _streetCmd.Parameters.Add("@lon", SqliteType.Real);
        _streetCmd.Parameters.Add("@radius", SqliteType.Real);

        // Prepared query: nearest suburb/neighbourhood/village/hamlet within ~5.5km
        _suburbCmd = _connection.CreateCommand();
        _suburbCmd.CommandText = @"
            SELECT name, place_type, latitude, longitude
            FROM places
            WHERE place_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')
              AND latitude BETWEEN @lat - @radius AND @lat + @radius
              AND longitude BETWEEN @lon - @radius AND @lon + @radius
            ORDER BY
                (latitude - @lat) * (latitude - @lat) +
                (longitude - @lon) * (longitude - @lon)
            LIMIT 1";
        _suburbCmd.Parameters.Add("@lat", SqliteType.Real);
        _suburbCmd.Parameters.Add("@lon", SqliteType.Real);
        _suburbCmd.Parameters.Add("@radius", SqliteType.Real);

        // Prepared query: nearest city/town within ~33km
        _cityCmd = _connection.CreateCommand();
        _cityCmd.CommandText = @"
            SELECT name, place_type, latitude, longitude
            FROM places
            WHERE place_type IN ('city', 'town')
              AND latitude BETWEEN @lat - @radius AND @lat + @radius
              AND longitude BETWEEN @lon - @radius AND @lon + @radius
            ORDER BY
                (latitude - @lat) * (latitude - @lat) +
                (longitude - @lon) * (longitude - @lon)
            LIMIT 1";
        _cityCmd.Parameters.Add("@lat", SqliteType.Real);
        _cityCmd.Parameters.Add("@lon", SqliteType.Real);
        _cityCmd.Parameters.Add("@radius", SqliteType.Real);
    }

    public bool HasPlaceData => _hasPlacesTable;

    /// <summary>
    /// Reverse geocode coordinates to street, suburb, and city
    /// </summary>
    public ReverseGeocodeResult Lookup(double latitude, double longitude)
    {
        var result = new ReverseGeocodeResult();
        var queryPoint = new GeoPoint(latitude, longitude);

        // Street lookup (from road_segments)
        _streetCmd.Parameters["@lat"].Value = latitude;
        _streetCmd.Parameters["@lon"].Value = longitude;
        _streetCmd.Parameters["@radius"].Value = StreetRadiusDeg;

        using (var reader = _streetCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                result.Street = reader.GetString(0);
                result.HighwayType = reader.GetString(1);
                var streetPoint = new GeoPoint(reader.GetDouble(2), reader.GetDouble(3));
                result.StreetDistanceM = queryPoint.DistanceTo(streetPoint);
            }
        }

        // Suburb and city lookups require places table
        if (!_hasPlacesTable)
            return result;

        // Suburb lookup
        _suburbCmd.Parameters["@lat"].Value = latitude;
        _suburbCmd.Parameters["@lon"].Value = longitude;
        _suburbCmd.Parameters["@radius"].Value = SuburbRadiusDeg;

        using (var reader = _suburbCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                result.Suburb = reader.GetString(0);
                result.SuburbType = reader.GetString(1);
                var suburbPoint = new GeoPoint(reader.GetDouble(2), reader.GetDouble(3));
                result.SuburbDistanceM = queryPoint.DistanceTo(suburbPoint);
            }
        }

        // City lookup
        _cityCmd.Parameters["@lat"].Value = latitude;
        _cityCmd.Parameters["@lon"].Value = longitude;
        _cityCmd.Parameters["@radius"].Value = CityRadiusDeg;

        using (var reader = _cityCmd.ExecuteReader())
        {
            if (reader.Read())
            {
                result.City = reader.GetString(0);
                result.CityType = reader.GetString(1);
                var cityPoint = new GeoPoint(reader.GetDouble(2), reader.GetDouble(3));
                result.CityDistanceM = queryPoint.DistanceTo(cityPoint);
            }
        }

        return result;
    }

    private bool TableExists(string tableName)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name";
        cmd.Parameters.AddWithValue("@name", tableName);
        var result = cmd.ExecuteScalar();
        return Convert.ToInt64(result) > 0;
    }

    public void Dispose()
    {
        _streetCmd?.Dispose();
        _suburbCmd?.Dispose();
        _cityCmd?.Dispose();
        _connection?.Dispose();
    }
}

/// <summary>
/// Result of a reverse geocode lookup
/// </summary>
public class ReverseGeocodeResult
{
    public string? Street { get; set; }
    public string? HighwayType { get; set; }
    public double StreetDistanceM { get; set; }

    public string? Suburb { get; set; }
    public string? SuburbType { get; set; }
    public double SuburbDistanceM { get; set; }

    public string? City { get; set; }
    public string? CityType { get; set; }
    public double CityDistanceM { get; set; }

    public string FormatStreet() => Street ?? "(not found)";
    public string FormatSuburb() => Suburb ?? "(not found)";
    public string FormatCity() => City ?? "(not found)";
}
