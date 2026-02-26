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

    /// <summary>
    /// Validates reverse geocode results against expected trip location data.
    /// Accepts an Elasticsearch-style hits array; compares startLocationAddress
    /// (road, place, region) against our geocoder output.
    /// isMatch = road matched AND city matched.
    /// </summary>
    [HttpPost("validate")]
    [ProducesResponseType(typeof(TripValidationResponse), 200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public IActionResult Validate([FromBody] TripValidationRequest request)
    {
        if (request.Hits == null || request.Hits.Count == 0)
            return BadRequest(new { error = "Hits list must contain at least one item." });

        var dbPath = _pathResolver.GetCountryDbPath(request.CountryCode);
        if (dbPath == null)
            return NotFound(new { error = $"Database for country '{request.CountryCode}' not found." });

        using var speedLookup = new SpeedLimitLookup(dbPath);
        var batchTimer = Stopwatch.StartNew();
        var results = new List<TripValidationResultItem>(request.Hits.Count);
        int matchCount = 0;

        foreach (var hit in request.Hits)
        {
            var src = hit.Source;
            var addr = src.StartLocationAddress;
            var tripId = !string.IsNullOrEmpty(hit.Id) ? hit.Id : src.Id;

            if (!ValidateCoordinates(addr.Latitude, addr.Longitude, out var coordError))
            {
                results.Add(new TripValidationResultItem
                {
                    TripId = tripId,
                    TripStartTimestamp = src.TripStartTimestamp,
                    TripEndTimestamp = src.TripEndTimestamp,
                    StartLocationAddress = new TripValidationAddress
                    {
                        Latitude = addr.Latitude,
                        Longitude = addr.Longitude,
                        Road = addr.Address,
                        Place = addr.Place,
                        Region = addr.Region
                    },
                    ActualRoad = $"[invalid coordinates: {coordError}]",
                    IsMatch = false
                });
                continue;
            }

            var itemTimer = Stopwatch.StartNew();
            var geo = PerformLookup(dbPath, request.CountryCode.ToUpper(), addr.Latitude, addr.Longitude, itemTimer, speedLookup);

            // Road: prefer postal Street (addr:street), fall back to NearestRoad
            var resolvedStreet = geo.Street ?? geo.NearestRoad;

            // Expected address (with house number) should contain the actual street name
            bool roadMatched = !string.IsNullOrEmpty(addr.Address)
                && !string.IsNullOrEmpty(resolvedStreet)
                && addr.Address.Contains(resolvedStreet, StringComparison.OrdinalIgnoreCase);

            // City: either value contains the other (handles "Greater Sydney" vs "Sydney" etc.)
            bool cityMatched = !string.IsNullOrEmpty(addr.Place)
                && !string.IsNullOrEmpty(geo.City)
                && (geo.City.Contains(addr.Place, StringComparison.OrdinalIgnoreCase)
                    || addr.Place.Contains(geo.City, StringComparison.OrdinalIgnoreCase));

            bool isMatch = roadMatched && cityMatched;
            if (isMatch) matchCount++;

            results.Add(new TripValidationResultItem
            {
                TripId = tripId,
                TripStartTimestamp = src.TripStartTimestamp,
                TripEndTimestamp = src.TripEndTimestamp,
                StartLocationAddress = new TripValidationAddress
                {
                    Latitude = addr.Latitude,
                    Longitude = addr.Longitude,
                    Road = addr.Address,
                    Place = addr.Place,
                    Region = addr.Region
                },
                ActualRoad = resolvedStreet,
                ActualNearestRoad = geo.NearestRoad,
                ActualCity = geo.City,
                ActualMunicipality = geo.Municipality,
                ActualRegion = geo.Region,
                IsMatch = isMatch
            });
        }

        batchTimer.Stop();

        return Ok(new TripValidationResponse
        {
            CountryCode = request.CountryCode.ToUpper(),
            RequestCount = request.Hits.Count,
            MatchCount = matchCount,
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
            NearestRoad = result.NearestRoad,
            HighwayType = result.HighwayType,
            NearestRoadDistanceMeters = result.NearestRoad != null ? result.NearestRoadDistanceM : null,
            Suburb = result.Suburb,
            SuburbType = result.SuburbType,
            SuburbDistanceMeters = result.Suburb != null ? result.SuburbDistanceM : null,
            City = result.City,
            CityType = result.CityType,
            CityDistanceMeters = result.City != null ? result.CityDistanceM : null,
            Municipality = result.Municipality,
            MunicipalityType = result.MunicipalityType,
            MunicipalityDistanceMeters = result.Municipality != null ? result.MunicipalityDistanceM : null,
            Region = result.Region,
            RegionType = result.RegionType,
            RegionDistanceMeters = result.Region != null ? result.RegionDistanceM : null,
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
