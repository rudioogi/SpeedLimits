using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using OsmDataAcquisition.Configuration;
using OsmDataAcquisition.Services;
using OsmDataAcquisition.Utilities;
using SpeedLimits.Api.Models;
using SpeedLimits.Api.Services;

namespace SpeedLimits.Api.Controllers;

/// <summary>
/// Data acquisition pipeline — mirrors menu option 1 (download and process OSM data).
/// </summary>
[ApiController]
[Route("api/acquisition")]
public class DataAcquisitionController : ControllerBase
{
    private readonly DatabasePathResolver _pathResolver;
    private readonly DataAcquisitionConfig _dataConfig;
    private readonly DatabaseConfig _dbConfig;

    public DataAcquisitionController(
        DatabasePathResolver pathResolver,
        IOptions<DataAcquisitionConfig> dataConfig,
        IOptions<DatabaseConfig> dbConfig)
    {
        _pathResolver = pathResolver;
        _dataConfig = dataConfig.Value;
        _dbConfig = dbConfig.Value;
    }

    /// <summary>
    /// Lists all countries available for data acquisition.
    /// </summary>
    [HttpGet("countries")]
    [ProducesResponseType(typeof(List<CountryInfo>), 200)]
    public IActionResult GetCountries()
    {
        var countries = _dataConfig.Countries.Select(c => new CountryInfo
        {
            Code = c.Code,
            Name = c.Name,
            GeofabrikUrl = c.GeofabrikUrl
        }).ToList();

        return Ok(countries);
    }

    /// <summary>
    /// Downloads and processes OSM data for the specified country or all countries (menu option 1).
    /// This is a long-running operation — expect minutes to hours for large countries.
    /// Supply either a CountryCode or set All to true.
    /// </summary>
    [HttpPost("process")]
    [ProducesResponseType(typeof(ProcessingResult), 200)]
    [ProducesResponseType(400)]
    public async Task<IActionResult> Process([FromBody] ProcessRequest request)
    {
        List<CountryConfig> targets;

        if (request.All)
        {
            targets = _dataConfig.Countries;
        }
        else if (!string.IsNullOrWhiteSpace(request.CountryCode))
        {
            var country = _dataConfig.Countries
                .FirstOrDefault(c => c.Code.Equals(request.CountryCode, StringComparison.OrdinalIgnoreCase));

            if (country == null)
                return BadRequest(new { error = $"Country '{request.CountryCode}' is not configured. See GET /api/acquisition/countries." });

            targets = new List<CountryConfig> { country };
        }
        else
        {
            return BadRequest(new { error = "Provide either a CountryCode or set All to true." });
        }

        var results = new List<CountryProcessResult>();

        foreach (var country in targets)
        {
            var result = await ProcessCountryAsync(country);
            results.Add(result);
        }

        return Ok(new ProcessingResult
        {
            Total = results.Count,
            Successful = results.Count(r => r.Success),
            Failed = results.Count(r => !r.Success),
            Results = results
        });
    }

    // ── private ──────────────────────────────────────────────────────────────

    private async Task<CountryProcessResult> ProcessCountryAsync(CountryConfig country)
    {
        var startTime = DateTime.UtcNow;

        try
        {
            // Step 1: Download
            string pbfFilePath;
            using (var downloader = new OsmDataDownloader(_dataConfig))
            {
                pbfFilePath = await downloader.DownloadCountryDataAsync(country);
            }

            if (!System.IO.File.Exists(pbfFilePath))
                throw new Exception($"Downloaded file not found: {pbfFilePath}");

            // Step 2: Extract road segments
            var extractor = new OsmRoadExtractor(country);
            var roadSegments = extractor.ExtractRoadSegments(pbfFilePath).ToList();

            if (roadSegments.Count == 0)
                throw new Exception("No road segments extracted — possible parsing error.");

            // Step 3: Build database
            var databasePath = System.IO.Path.Combine(
                _dataConfig.DatabaseDirectory,
                $"{country.Code.ToLower()}_speedlimits.db");

            var builder = new DatabaseBuilder(_dbConfig, country);
            builder.BuildDatabase(databasePath, roadSegments, extractor.PlaceNodes);

            // Step 4: Validate
            var validator = new ValidationHelper(databasePath);
            validator.ValidateAndReport(); // writes to console/log; result still returned below

            var dbFileInfo = new FileInfo(databasePath);
            var elapsed = DateTime.UtcNow - startTime;

            return new CountryProcessResult
            {
                CountryCode = country.Code,
                CountryName = country.Name,
                Success = true,
                RoadSegmentsExtracted = roadSegments.Count,
                PlaceNodesExtracted = extractor.PlaceNodes.Count,
                DatabaseSizeMb = dbFileInfo.Length / (1024.0 * 1024.0),
                ProcessingTimeMinutes = elapsed.TotalMinutes
            };
        }
        catch (Exception ex)
        {
            return new CountryProcessResult
            {
                CountryCode = country.Code,
                CountryName = country.Name,
                Success = false,
                Error = ex.Message,
                ProcessingTimeMinutes = (DateTime.UtcNow - startTime).TotalMinutes
            };
        }
    }
}
