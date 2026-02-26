using System.Diagnostics;
using System.Text.RegularExpressions;
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
        // Share one geocoder instance across all items in the batch for efficiency.
        using var geocoder = new ReverseGeocoder(dbPath);
        var batchTimer = Stopwatch.StartNew();
        var results = new List<TripValidationResultItem>(request.Hits.Count);
        int matchCount = 0;

        foreach (var hit in request.Hits)
        {
            var src = hit.Source;
            var addr = src?.StartLocationAddress;
            var tripId = hit.Id ?? src?.Id ?? "(unknown)";

            // Skip hits where source or coordinates are missing
            if (src == null || addr == null)
            {
                results.Add(new TripValidationResultItem
                {
                    TripId = tripId,
                    TripStartTimestamp = src?.TripStartTimestamp,
                    TripEndTimestamp = src?.TripEndTimestamp,
                    StartLocationAddress = new TripValidationAddress(),
                    ActualRoad = "[missing source or startLocationAddress]",
                    IsMatch = false
                });
                continue;
            }

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
            var geo = PerformLookup(geocoder, request.CountryCode.ToUpper(), addr.Latitude, addr.Longitude, itemTimer, speedLookup);

            // Road: strip house number from expected address then fuzzy-compare
            // against postal street (addr:street) or nearest road name.
            var resolvedStreet = geo.Street ?? geo.NearestRoad;
            var expectedStreet = StripHouseNumber(addr.Address ?? string.Empty);
            bool roadMatched = FuzzyContains(expectedStreet, resolvedStreet);

            // Proximity fallback: if the primary road didn't match, search within
            // ProximitySearchRadiusDeg for any road/address-node whose name does match.
            // This handles cases where the expected road is real but not the nearest one
            // (e.g. a parallel street just one block away).
            bool roadMatchedViaProximity = false;
            string? proximityMatchedRoad = null;
            double? proximityRoadDistanceMeters = null;

            if (!roadMatched && !string.IsNullOrWhiteSpace(expectedStreet))
            {
                var proximity = geocoder.FindNearbyRoadByName(
                    expectedStreet, addr.Latitude, addr.Longitude, ProximitySearchRadiusDeg);
                if (proximity.HasValue)
                {
                    roadMatched = true;
                    roadMatchedViaProximity = true;
                    proximityMatchedRoad = proximity.Value.MatchedName;
                    proximityRoadDistanceMeters = proximity.Value.DistanceM;
                }
            }

            // Place: compare against city, municipality, or suburb — any match counts.
            // This handles cases like expected "Heddon Greta" matching actualMunicipality
            // when actualCity is a different neighbouring locality.
            bool placeMatched = FuzzyContains(addr.Place, geo.City)
                             || FuzzyContains(addr.Place, geo.Municipality)
                             || FuzzyContains(addr.Place, geo.Suburb);

            bool isMatch = roadMatched && placeMatched;
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
                RoadMatched = roadMatched,
                RoadMatchedViaProximity = roadMatchedViaProximity,
                ProximityMatchedRoad = proximityMatchedRoad,
                ProximityRoadDistanceMeters = proximityRoadDistanceMeters,
                PlaceMatched = placeMatched,
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

    /// <summary>Radius used when searching for a named road nearby a mismatch point (~1.1 km).</summary>
    private const double ProximitySearchRadiusDeg = 0.01;

    /// <summary>Creates a fresh geocoder for the lookup (used by single-item and batch endpoints).</summary>
    private static ReverseGeocodeResponse PerformLookup(
        string dbPath, string countryCode, double lat, double lon,
        Stopwatch sw, SpeedLimitLookup speedLookup)
    {
        using var geocoder = new ReverseGeocoder(dbPath);
        return PerformLookup(geocoder, countryCode, lat, lon, sw, speedLookup);
    }

    /// <summary>Reuses a caller-owned geocoder (used by the validate endpoint for batch efficiency).</summary>
    private static ReverseGeocodeResponse PerformLookup(
        ReverseGeocoder geocoder, string countryCode, double lat, double lon,
        Stopwatch sw, SpeedLimitLookup speedLookup)
    {
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

    /// <summary>
    /// Removes a leading house number (and optional range) from a mailing address,
    /// leaving just the street name. E.g. "3 Heddon Street" → "Heddon Street",
    /// "13A-15B Main Rd" → "Main Rd".
    /// </summary>
    private static readonly Regex HouseNumberPrefix =
        new(@"^\d+[a-zA-Z]?(\s*[-–/]\s*\d+[a-zA-Z]?)?\s+", RegexOptions.Compiled);

    private static string StripHouseNumber(string address) =>
        HouseNumberPrefix.Replace(address.Trim(), string.Empty).Trim();

    /// <summary>
    /// Returns true when neither value is null/empty and one contains the other
    /// (case-insensitive). Handles partial name matches in both directions.
    /// </summary>
    private static bool FuzzyContains(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase)
            || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ValidateCoordinates(double lat, double lon, out string error)
    {
        if (lat < -90 || lat > 90) { error = $"Latitude {lat} out of range (-90 to 90)."; return false; }
        if (lon < -180 || lon > 180) { error = $"Longitude {lon} out of range (-180 to 180)."; return false; }
        error = string.Empty;
        return true;
    }
}
