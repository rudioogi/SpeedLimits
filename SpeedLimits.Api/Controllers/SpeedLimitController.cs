using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using SpeedLimits.Api.Models;
using SpeedLimits.Api.Services;

namespace SpeedLimits.Api.Controllers;

/// <summary>
/// Speed limit lookups — mirrors menu options 3 and 5.
/// </summary>
[ApiController]
[Route("api/speedlimit")]
public class SpeedLimitController : ControllerBase
{
    private readonly DatabasePathResolver _pathResolver;
    private readonly SpeedLimitService _speedLimitService;

    public SpeedLimitController(DatabasePathResolver pathResolver, SpeedLimitService speedLimitService)
    {
        _pathResolver = pathResolver;
        _speedLimitService = speedLimitService;
    }

    /// <summary>
    /// Returns up to 5 nearest road segments with their speed limits (menu option 3).
    /// </summary>
    /// <param name="country">Country code, e.g. ZA or AU.</param>
    /// <param name="lat">Latitude (decimal degrees).</param>
    /// <param name="lon">Longitude (decimal degrees).</param>
    /// <param name="limit">Maximum results to return (1–20, default 5).</param>
    [HttpGet]
    [ProducesResponseType(typeof(SpeedLimitLookupResult), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public IActionResult Lookup(
        [FromQuery] string country,
        [FromQuery] double lat,
        [FromQuery] double lon,
        [FromQuery] int limit = 5)
    {
        if (!ValidateCoordinates(lat, lon, out var coordError))
            return BadRequest(new { error = coordError });

        if (limit < 1 || limit > 20)
            return BadRequest(new { error = "limit must be between 1 and 20." });

        var dbPath = _pathResolver.GetCountryDbPath(country);
        if (dbPath == null)
            return NotFound(new { error = $"Database for country '{country}' not found." });

        var roads = _speedLimitService.LookupNearby(dbPath, lat, lon, limit);

        return Ok(new SpeedLimitLookupResult
        {
            QueryLatitude = lat,
            QueryLongitude = lon,
            CountryCode = country.ToUpper(),
            NearbyRoads = roads
        });
    }

    /// <summary>
    /// Tests a fixed set of known reference locations for ZA and AU (menu option 5).
    /// </summary>
    [HttpGet("known-locations")]
    [ProducesResponseType(typeof(List<KnownLocationResult>), 200)]
    public IActionResult KnownLocations()
    {
        var results = _speedLimitService.TestKnownLocations();
        return Ok(results);
    }

    private static bool ValidateCoordinates(double lat, double lon, out string error)
    {
        if (lat < -90 || lat > 90) { error = $"Latitude {lat} out of range (-90 to 90)."; return false; }
        if (lon < -180 || lon > 180) { error = $"Longitude {lon} out of range (-180 to 180)."; return false; }
        error = string.Empty;
        return true;
    }
}
