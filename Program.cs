using Microsoft.Extensions.Configuration;
using OsmDataAcquisition.Configuration;
using OsmDataAcquisition.Models;
using OsmDataAcquisition.Services;
using OsmDataAcquisition.Utilities;

namespace OsmDataAcquisition;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== OSM Speed Limit Data Acquisition ===");
        Console.WriteLine();

        try
        {
            // Load configuration
            var configuration = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                .Build();

            var dataConfig = configuration.GetSection("DataAcquisition").Get<DataAcquisitionConfig>()
                ?? throw new Exception("Failed to load DataAcquisition configuration");

            var dbConfig = configuration.GetSection("Database").Get<DatabaseConfig>()
                ?? throw new Exception("Failed to load Database configuration");

            // Ensure output directories exist
            Directory.CreateDirectory(dataConfig.DatabaseDirectory);

            // Process each country
            var successCount = 0;
            var failureCount = 0;

            foreach (var country in dataConfig.Countries)
            {
                Console.WriteLine($"\n{'=',60}");
                Console.WriteLine($"Processing {country.Name} ({country.Code})");
                Console.WriteLine($"{'=',60}\n");

                try
                {
                    await ProcessCountryAsync(country, dataConfig, dbConfig);
                    successCount++;
                    Console.WriteLine($"\n✓ {country.Name} processing complete!\n");
                }
                catch (Exception ex)
                {
                    failureCount++;
                    Console.WriteLine($"\n✗ {country.Name} processing failed: {ex.Message}");
                    Console.WriteLine($"Error details: {ex}");
                    Console.WriteLine();

                    // Continue to next country instead of failing completely
                    Console.WriteLine("Continuing to next country...\n");
                }
            }

            // Summary
            Console.WriteLine($"\n{'=',60}");
            Console.WriteLine("Processing Summary");
            Console.WriteLine($"{'=',60}");
            Console.WriteLine($"Successful: {successCount}/{dataConfig.Countries.Count}");
            Console.WriteLine($"Failed: {failureCount}/{dataConfig.Countries.Count}");
            Console.WriteLine();

            return failureCount == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFatal error: {ex.Message}");
            Console.WriteLine($"Details: {ex}");
            return 1;
        }
    }

    static async Task ProcessCountryAsync(
        CountryConfig country,
        DataAcquisitionConfig dataConfig,
        DatabaseConfig dbConfig)
    {
        var startTime = DateTime.UtcNow;

        // Step 1: Download OSM data
        Console.WriteLine("Step 1: Downloading OSM data");
        Console.WriteLine($"Source: {country.GeofabrikUrl}");
        Console.WriteLine();

        string pbfFilePath;
        using (var downloader = new OsmDataDownloader(dataConfig))
        {
            pbfFilePath = await downloader.DownloadCountryDataAsync(country);
        }

        // Verify file exists
        if (!File.Exists(pbfFilePath))
        {
            throw new Exception($"Downloaded file not found: {pbfFilePath}");
        }

        var fileInfo = new FileInfo(pbfFilePath);
        Console.WriteLine($"Downloaded file size: {fileInfo.Length / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine();

        // Step 2: Extract road segments
        Console.WriteLine("Step 2: Extracting road segments");
        Console.WriteLine();

        var extractor = new OsmRoadExtractor(country);
        var roadSegments = extractor.ExtractRoadSegments(pbfFilePath).ToList();

        Console.WriteLine($"Total road segments extracted: {roadSegments.Count:N0}");
        Console.WriteLine();

        if (roadSegments.Count == 0)
        {
            throw new Exception("No road segments extracted - possible parsing error");
        }

        // Step 3: Build database
        Console.WriteLine("Step 3: Building SQLite database");
        Console.WriteLine();

        var databasePath = Path.Combine(
            dataConfig.DatabaseDirectory,
            $"{country.Code.ToLower()}_speedlimits.db");

        var builder = new DatabaseBuilder(dbConfig, country);
        builder.BuildDatabase(databasePath, roadSegments);

        var dbFileInfo = new FileInfo(databasePath);
        Console.WriteLine($"Database size: {dbFileInfo.Length / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine();

        // Step 4: Validate database
        Console.WriteLine("Step 4: Validating database");

        var validator = new ValidationHelper(databasePath);
        validator.ValidateAndReport();

        // Test known locations
        TestKnownLocations(country, validator);

        // Processing time
        var duration = DateTime.UtcNow - startTime;
        Console.WriteLine($"Total processing time: {duration.TotalMinutes:F1} minutes");
    }

    static void TestKnownLocations(CountryConfig country, ValidationHelper validator)
    {
        Console.WriteLine("\n=== Testing Known Locations ===");

        if (country.Code == "ZA")
        {
            validator.TestLocationQuery(
                new GeoPoint(-33.9249, 18.4241),
                "Cape Town N1");

            validator.TestLocationQuery(
                new GeoPoint(-26.2041, 28.0473),
                "Johannesburg M1");

            validator.TestLocationQuery(
                new GeoPoint(-33.9258, 18.4232),
                "Cape Town Residential");
        }
        else if (country.Code == "AU")
        {
            validator.TestLocationQuery(
                new GeoPoint(-33.8688, 151.2093),
                "Sydney M1");

            validator.TestLocationQuery(
                new GeoPoint(-37.8136, 144.9631),
                "Melbourne M1");

            validator.TestLocationQuery(
                new GeoPoint(-33.8675, 151.2070),
                "Sydney Residential");
        }

        Console.WriteLine("=== Location Tests Complete ===\n");
    }
}
