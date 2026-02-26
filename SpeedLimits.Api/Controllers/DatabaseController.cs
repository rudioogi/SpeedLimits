using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SpeedLimits.Core.Configuration;
using SpeedLimits.Api.Models;
using SpeedLimits.Api.Services;

namespace SpeedLimits.Api.Controllers;

/// <summary>
/// Database validation and statistics — mirrors menu options 2 and 4.
/// </summary>
[ApiController]
[Route("api/databases")]
public class DatabaseController : ControllerBase
{
    private readonly DatabasePathResolver _pathResolver;
    private readonly DatabaseInfoService _infoService;
    private readonly DataAcquisitionConfig _dataConfig;

    public DatabaseController(
        DatabasePathResolver pathResolver,
        DatabaseInfoService infoService,
        IOptions<DataAcquisitionConfig> dataConfig)
    {
        _pathResolver = pathResolver;
        _infoService = infoService;
        _dataConfig = dataConfig.Value;
    }

    /// <summary>
    /// Lists all configured databases and whether they exist on disk.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<DatabaseEntry>), 200)]
    public IActionResult List()
    {
        var entries = _dataConfig.Countries.Select(c =>
        {
            var dbPath = _pathResolver.GetCountryDbPath(c.Code);
            return new DatabaseEntry
            {
                CountryCode = c.Code,
                CountryName = c.Name,
                Exists = dbPath != null,
                FilePath = dbPath,
                FileSizeMb = dbPath != null ? new FileInfo(dbPath).Length / (1024.0 * 1024.0) : null
            };
        }).ToList();

        return Ok(entries);
    }

    /// <summary>
    /// Validates the database for a specific country (menu option 2).
    /// Returns counts, distributions and metadata.
    /// </summary>
    [HttpGet("{countryCode}/validate")]
    [ProducesResponseType(typeof(ValidationResult), 200)]
    [ProducesResponseType(404)]
    public IActionResult Validate(string countryCode)
    {
        var dbPath = _pathResolver.GetCountryDbPath(countryCode);
        if (dbPath == null)
            return NotFound(new { error = $"Database for country '{countryCode}' not found." });

        var result = _infoService.GetValidation(dbPath, countryCode.ToUpper());
        return Ok(result);
    }

    /// <summary>
    /// Returns statistics for a specific country's database (menu option 4, single country).
    /// </summary>
    [HttpGet("{countryCode}/statistics")]
    [ProducesResponseType(typeof(DatabaseStatistics), 200)]
    [ProducesResponseType(404)]
    public IActionResult Statistics(string countryCode)
    {
        var country = _dataConfig.Countries
            .FirstOrDefault(c => c.Code.Equals(countryCode, StringComparison.OrdinalIgnoreCase));

        if (country == null)
            return NotFound(new { error = $"Country '{countryCode}' is not configured." });

        var dbPath = _pathResolver.GetCountryDbPath(countryCode);
        if (dbPath == null)
            return NotFound(new { error = $"Database for country '{countryCode}' not found." });

        var stats = _infoService.GetStatistics(dbPath, country.Code, country.Name);
        return Ok(stats);
    }

    /// <summary>
    /// Returns statistics for all available databases (menu option 4, all countries).
    /// </summary>
    [HttpGet("statistics")]
    [ProducesResponseType(typeof(List<DatabaseStatistics>), 200)]
    public IActionResult AllStatistics()
    {
        var results = new List<DatabaseStatistics>();

        foreach (var country in _dataConfig.Countries)
        {
            var dbPath = _pathResolver.GetCountryDbPath(country.Code);
            if (dbPath == null)
                continue;

            results.Add(_infoService.GetStatistics(dbPath, country.Code, country.Name));
        }

        return Ok(results);
    }
}
