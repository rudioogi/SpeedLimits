using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SpeedLimits.Core;
using SpeedLimits.Core.Services;
using SpeedLimits.Api.Models;
using SpeedLimits.Api.Services;

namespace SpeedLimits.Api.Controllers;

/// <summary>
/// Reverse geocode GPS coordinates to street / suburb / city — mirrors menu option 6.
/// Also supports batch lookups with per-item and total timing.
/// </summary>
[ApiController]
[Route("api/reversegeocode")]
public class ReverseGeocodeController : ControllerBase
{
    private readonly DatabasePathResolver _pathResolver;

    public ReverseGeocodeController(DatabasePathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    /// <summary>
    /// Reverse geocodes a single coordinate pair (menu option 6).
    /// The response includes ElapsedMs showing how long the lookup took.
    /// </summary>
    /// <param name="country">Country code, e.g. ZA or AU.</param>
    /// <param name="lat">Latitude (decimal degrees).</param>
    /// <param name="lon">Longitude (decimal degrees).</param>
    [HttpGet]
    [ProducesResponseType(typeof(ReverseGeocodeResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public IActionResult Lookup(
        [FromQuery] string country,
        [FromQuery] double lat,
        [FromQuery] double lon)
    {
        if (!ValidateCoordinates(lat, lon, out var coordError))
            return BadRequest(new { error = coordError });

        var dbPath = _pathResolver.GetCountryDbPath(country);
        if (dbPath == null)
            return NotFound(new { error = $"Database for country '{country}' not found." });

        using var speedLookup = new SpeedLimitLookup(dbPath);
        var sw = Stopwatch.StartNew();
        var response = PerformLookup(dbPath, country.ToUpper(), lat, lon, sw, speedLookup);
        return Ok(response);
    }

    /// <summary>
    /// Batch reverse geocode: accepts a JSON body with multiple coordinate pairs.
    /// Returns per-item results each with their own ElapsedMs, plus aggregate
    /// RequestCount and TotalTimeMs for the whole batch.
    /// </summary>
    [HttpPost("batch")]
    [ProducesResponseType(typeof(BatchReverseGeocodeResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public IActionResult Batch([FromBody] BatchReverseGeocodeRequest request)
    {
        if (request.Coordinates == null || request.Coordinates.Count == 0)
            return BadRequest(new { error = "Coordinates list must contain at least one item." });

        var dbPath = _pathResolver.GetCountryDbPath(request.CountryCode);
        if (dbPath == null)
            return NotFound(new { error = $"Database for country '{request.CountryCode}' not found." });

        using var speedLookup = new SpeedLimitLookup(dbPath);
        var batchTimer = Stopwatch.StartNew();
        var results = new List<ReverseGeocodeResponse>(request.Coordinates.Count);

        foreach (var coord in request.Coordinates)
        {
            if (!ValidateCoordinates(coord.Latitude, coord.Longitude, out var coordError))
            {
                // Include a failed entry rather than aborting the whole batch
                results.Add(new ReverseGeocodeResponse
                {
                    QueryLatitude = coord.Latitude,
                    QueryLongitude = coord.Longitude,
                    CountryCode = request.CountryCode.ToUpper(),
                    HasPlaceData = false,
                    Street = $"[invalid coordinates: {coordError}]",
                    ElapsedMs = 0
                });
                continue;
            }

            var itemTimer = Stopwatch.StartNew();
            var entry = PerformLookup(dbPath, request.CountryCode.ToUpper(), coord.Latitude, coord.Longitude, itemTimer, speedLookup);
            results.Add(entry);
        }

        batchTimer.Stop();

        return Ok(new BatchReverseGeocodeResult
        {
            CountryCode = request.CountryCode.ToUpper(),
            RequestCount = request.Coordinates.Count,
            TotalTimeMs = batchTimer.Elapsed.TotalMilliseconds,
            Results = results
        });
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ReverseGeocodeResponse PerformLookup(
        string dbPath, string countryCode, double lat, double lon,
        Stopwatch sw, SpeedLimitLookup speedLookup)
    {
        using var geocoder = new ReverseGeocoder(dbPath);
        var result = geocoder.Lookup(lat, lon);
        var roadInfo = speedLookup.GetRoadInfo(lat, lon);
        sw.Stop();

        return new ReverseGeocodeResponse
        {
            QueryLatitude = lat,
            QueryLongitude = lon,
            CountryCode = countryCode,
            HasPlaceData = geocoder.HasPlaceData,
            Street = result.Street,
            HighwayType = result.HighwayType,
            StreetDistanceMeters = result.Street != null ? result.StreetDistanceM : null,
            Suburb = result.Suburb,
            SuburbType = result.SuburbType,
            SuburbDistanceMeters = result.Suburb != null ? result.SuburbDistanceM : null,
            City = result.City,
            CityType = result.CityType,
            CityDistanceMeters = result.City != null ? result.CityDistanceM : null,
            SpeedLimitKmh = roadInfo?.SpeedLimitKmh,
            IsSpeedLimitInferred = roadInfo?.IsInferred,
            ElapsedMs = sw.Elapsed.TotalMilliseconds
        };
    }

    private static bool ValidateCoordinates(double lat, double lon, out string error)
    {
        if (lat < -90 || lat > 90) { error = $"Latitude {lat} out of range (-90 to 90)."; return false; }
        if (lon < -180 || lon > 180) { error = $"Longitude {lon} out of range (-180 to 180)."; return false; }
        error = string.Empty;
        return true;
    }
}
