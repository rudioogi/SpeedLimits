using Microsoft.Extensions.Configuration;
using OsmDataAcquisition.Configuration;
using OsmDataAcquisition.Models;
using OsmDataAcquisition.Services;
using OsmDataAcquisition.Utilities;
using Microsoft.Data.Sqlite;

namespace OsmDataAcquisition;

class Program
{
    private static DataAcquisitionConfig? _dataConfig;
    private static DatabaseConfig? _dbConfig;

    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║      OSM Speed Limit Data Acquisition System              ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.WriteLine();

        try
        {
            // Load configuration
            LoadConfiguration();

            // Main menu loop
            while (true)
            {
                ShowMainMenu();
                var choice = Console.ReadLine()?.Trim();

                Console.WriteLine();

                switch (choice)
                {
                    case "1":
                        await DownloadAndProcessData();
                        break;
                    case "2":
                        ValidateDatabases();
                        break;
                    case "3":
                        TestLocationLookup();
                        break;
                    case "4":
                        ViewDatabaseStatistics();
                        break;
                    case "5":
                        TestKnownLocations();
                        break;
                    case "6":
                        ReverseGeocodeLookup();
                        break;
                    case "7":
                        Console.WriteLine("Goodbye!");
                        return 0;
                    default:
                        Console.WriteLine("Invalid option. Please try again.\n");
                        break;
                }

                Console.WriteLine("\nPress any key to continue...");
                Console.ReadKey();
                Console.Clear();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\nFatal error: {ex.Message}");
            Console.WriteLine($"Details: {ex}");
            return 1;
        }
    }

    static void LoadConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .Build();

        _dataConfig = configuration.GetSection("DataAcquisition").Get<DataAcquisitionConfig>()
            ?? throw new Exception("Failed to load DataAcquisition configuration");

        _dbConfig = configuration.GetSection("Database").Get<DatabaseConfig>()
            ?? throw new Exception("Failed to load Database configuration");

        // Ensure directories exist
        Directory.CreateDirectory(_dataConfig.DatabaseDirectory);
        Directory.CreateDirectory(GetDatabaseFolder());
    }

    /// <summary>
    /// Gets the absolute path to the Database folder (works from bin or project root)
    /// </summary>
    static string GetDatabaseFolder()
    {
        // Try current directory first (when running from project root)
        var currentDir = Path.Combine(Directory.GetCurrentDirectory(), "Database");
        if (Directory.Exists(currentDir))
            return currentDir;

        // Try relative to executable (when running from bin folder)
        var exeDir = AppContext.BaseDirectory;
        var relativeToExe = Path.Combine(exeDir, "Database");
        if (Directory.Exists(relativeToExe))
            return relativeToExe;

        // Try going up from bin folder to project root
        var projectRoot = Path.GetFullPath(Path.Combine(exeDir, "..", "..", "..", "Database"));
        if (Directory.Exists(projectRoot))
            return projectRoot;

        // If none exist, create in current directory
        Directory.CreateDirectory(currentDir);
        return currentDir;
    }

    /// <summary>
    /// Gets the absolute path to a database file
    /// </summary>
    static string GetDatabasePath(string filename)
    {
        return Path.Combine(GetDatabaseFolder(), filename);
    }

    static void ShowMainMenu()
    {
        Console.WriteLine("\n╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                      MAIN MENU                             ║");
        Console.WriteLine("╠════════════════════════════════════════════════════════════╣");
        Console.WriteLine("║  1. Download and Process OSM Data (Full Pipeline)          ║");
        Console.WriteLine("║  2. Validate Pre-Built Databases                           ║");
        Console.WriteLine("║  3. Test Location Lookup (Custom Coordinates)              ║");
        Console.WriteLine("║  4. View Database Statistics                               ║");
        Console.WriteLine("║  5. Test Known Locations                                   ║");
        Console.WriteLine("║  6. Reverse Geocode (Coordinates to Address)               ║");
        Console.WriteLine("║  7. Exit                                                   ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.Write("\nEnter your choice (1-7): ");
    }

    static async Task DownloadAndProcessData()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║         DOWNLOAD AND PROCESS OSM DATA                      ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        Console.WriteLine("Available countries:");
        for (int i = 0; i < _dataConfig!.Countries.Count; i++)
        {
            var country = _dataConfig.Countries[i];
            Console.WriteLine($"  {i + 1}. {country.Name} ({country.Code})");
        }
        Console.WriteLine($"  {_dataConfig.Countries.Count + 1}. All countries");
        Console.WriteLine("  0. Cancel");

        Console.Write("\nSelect country: ");
        var choice = Console.ReadLine()?.Trim();

        if (choice == "0")
        {
            Console.WriteLine("Cancelled.");
            return;
        }

        List<CountryConfig> countriesToProcess;

        if (int.TryParse(choice, out int countryIndex))
        {
            if (countryIndex == _dataConfig.Countries.Count + 1)
            {
                countriesToProcess = _dataConfig.Countries;
            }
            else if (countryIndex > 0 && countryIndex <= _dataConfig.Countries.Count)
            {
                countriesToProcess = new List<CountryConfig> { _dataConfig.Countries[countryIndex - 1] };
            }
            else
            {
                Console.WriteLine("Invalid selection.");
                return;
            }
        }
        else
        {
            Console.WriteLine("Invalid input.");
            return;
        }

        var successCount = 0;
        var failureCount = 0;

        foreach (var country in countriesToProcess)
        {
            Console.WriteLine($"\n{'=',60}");
            Console.WriteLine($"Processing {country.Name} ({country.Code})");
            Console.WriteLine($"{'=',60}\n");

            try
            {
                await ProcessCountryAsync(country, _dataConfig!, _dbConfig!);
                successCount++;
                Console.WriteLine($"\n✓ {country.Name} processing complete!\n");
            }
            catch (Exception ex)
            {
                failureCount++;
                Console.WriteLine($"\n✗ {country.Name} processing failed: {ex.Message}");
                Console.WriteLine($"Error details: {ex}");
            }
        }

        Console.WriteLine($"\n{'=',60}");
        Console.WriteLine("Processing Summary");
        Console.WriteLine($"{'=',60}");
        Console.WriteLine($"Successful: {successCount}/{countriesToProcess.Count}");
        Console.WriteLine($"Failed: {failureCount}/{countriesToProcess.Count}");
    }

    static void ValidateDatabases()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║            VALIDATE PRE-BUILT DATABASES                    ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        var databasePaths = new[]
        {
            ("South Africa", GetDatabasePath("za_speedlimits.db")),
            ("Australia", GetDatabasePath("au_speedlimits.db"))
        };

        foreach (var (country, path) in databasePaths)
        {
            Console.WriteLine($"Checking {country}: {path}");

            if (!File.Exists(path))
            {
                Console.WriteLine($"  ✗ Database not found\n");
                continue;
            }

            try
            {
                var validator = new ValidationHelper(path);
                validator.ValidateAndReport();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ Validation failed: {ex.Message}\n");
            }
        }
    }

    static void TestLocationLookup()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              TEST LOCATION LOOKUP                          ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        // Select database
        Console.WriteLine("Select database:");
        Console.WriteLine("  1. South Africa");
        Console.WriteLine("  2. Australia");
        Console.Write("\nChoice: ");

        var dbChoice = Console.ReadLine()?.Trim();
        string dbPath;

        if (dbChoice == "1")
            dbPath = GetDatabasePath("za_speedlimits.db");
        else if (dbChoice == "2")
            dbPath = GetDatabasePath("au_speedlimits.db");
        else
        {
            Console.WriteLine("Invalid selection.");
            return;
        }

        Console.WriteLine($"Using database: {dbPath}");

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"Database not found: {dbPath}");
            return;
        }

        // Get coordinates
        Console.Write("\nEnter coordinates as lat,lon (e.g. -33.9249,18.4241): ");
        var coordInput = Console.ReadLine()?.Trim();

        if (!TryParseCoordinates(coordInput, out double lat, out double lon))
            return;

        // Perform lookup
        Console.WriteLine($"\nSearching for roads near ({lat}, {lon})...\n");

        try
        {
            using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = @"
                SELECT
                    speed_limit_kmh,
                    name,
                    highway_type,
                    is_inferred,
                    center_lat,
                    center_lon
                FROM road_segments
                WHERE center_lat BETWEEN @lat - 0.02 AND @lat + 0.02
                  AND center_lon BETWEEN @lon - 0.02 AND @lon + 0.02
                ORDER BY
                    (center_lat - @lat) * (center_lat - @lat) +
                    (center_lon - @lon) * (center_lon - @lon)
                LIMIT 5";

            cmd.Parameters.AddWithValue("@lat", lat);
            cmd.Parameters.AddWithValue("@lon", lon);

            using var reader = cmd.ExecuteReader();
            var count = 0;

            while (reader.Read())
            {
                count++;
                var speedLimit = reader.GetInt32(0);
                var name = reader.IsDBNull(1) ? "(unnamed)" : reader.GetString(1);
                var highwayType = reader.GetString(2);
                var isInferred = reader.GetBoolean(3);
                var centerLat = reader.GetDouble(4);
                var centerLon = reader.GetDouble(5);

                var centerPoint = new GeoPoint(centerLat, centerLon);
                var queryPoint = new GeoPoint(lat, lon);
                var distance = queryPoint.DistanceTo(centerPoint);

                Console.WriteLine($"{count}. {name}");
                Console.WriteLine($"   Type: {highwayType}");
                Console.WriteLine($"   Speed: {speedLimit} km/h {(isInferred ? "(inferred)" : "(explicit)")}");
                Console.WriteLine($"   Distance: {distance:F0}m");
                Console.WriteLine($"   Center: ({centerLat:F6}, {centerLon:F6})");
                Console.WriteLine();
            }

            if (count == 0)
            {
                Console.WriteLine("No roads found within 2km of this location.");
            }
            else
            {
                Console.WriteLine($"\n✓ Found {count} road(s) nearby");
                Console.WriteLine($"✓ Nearest speed limit: Use the first result");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Lookup failed: {ex.Message}");
        }
    }

    static void ViewDatabaseStatistics()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           VIEW DATABASE STATISTICS                         ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        var databases = new[]
        {
            ("South Africa", GetDatabasePath("za_speedlimits.db")),
            ("Australia", GetDatabasePath("au_speedlimits.db"))
        };

        foreach (var (country, path) in databases)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"{country}: Database not found\n");
                continue;
            }

            try
            {
                using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
                connection.Open();

                var fileInfo = new FileInfo(path);

                Console.WriteLine($"{'=',60}");
                Console.WriteLine($"{country}");
                Console.WriteLine($"{'=',60}");
                Console.WriteLine($"File size: {fileInfo.Length / (1024.0 * 1024.0):F1} MB");
                Console.WriteLine($"Path: {path}");
                Console.WriteLine();

                // Get counts
                var totalRoads = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments");
                var explicitCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 0");
                var inferredCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM road_segments WHERE is_inferred = 1");
                var gridCells = GetScalar<long>(connection, "SELECT COUNT(DISTINCT grid_x || '_' || grid_y) FROM spatial_grid");

                Console.WriteLine($"Total road segments: {totalRoads:N0}");
                Console.WriteLine($"  Explicit speed limits: {explicitCount:N0} ({(explicitCount * 100.0 / totalRoads):F1}%)");
                Console.WriteLine($"  Inferred speed limits: {inferredCount:N0} ({(inferredCount * 100.0 / totalRoads):F1}%)");
                Console.WriteLine($"Grid cells populated: {gridCells:N0}");

                // Show place counts (backward compatible with old databases)
                try
                {
                    var placeCount = GetScalar<long>(connection, "SELECT COUNT(*) FROM places");
                    Console.WriteLine($"Total place nodes: {placeCount:N0}");
                    if (placeCount > 0)
                    {
                        var cities = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('city', 'town')");
                        var suburbs = GetScalar<long>(connection, "SELECT COUNT(*) FROM places WHERE place_type IN ('suburb', 'neighbourhood', 'village', 'hamlet')");
                        Console.WriteLine($"  Cities/Towns: {cities:N0}");
                        Console.WriteLine($"  Suburbs/Villages: {suburbs:N0}");
                    }
                }
                catch (SqliteException) { /* places table doesn't exist in old databases */ }

                Console.WriteLine();

                // Metadata
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT key, value FROM metadata WHERE key IN ('created_date', 'min_latitude', 'max_latitude', 'min_longitude', 'max_longitude')";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var key = reader.GetString(0);
                        var value = reader.GetString(1);
                        Console.WriteLine($"{key}: {value}");
                    }
                }

                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reading {country}: {ex.Message}\n");
            }
        }
    }

    static void TestKnownLocations()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║              TEST KNOWN LOCATIONS                          ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        // South Africa
        var zaDbPath = GetDatabasePath("za_speedlimits.db");
        if (File.Exists(zaDbPath))
        {
            Console.WriteLine("South Africa Test Locations:");
            Console.WriteLine("─────────────────────────────────────────────────────────────");
            var validator = new ValidationHelper(zaDbPath);

            validator.TestLocationQuery(new GeoPoint(-33.9249, 18.4241), "Cape Town N1");
            validator.TestLocationQuery(new GeoPoint(-26.2041, 28.0473), "Johannesburg M1");
            validator.TestLocationQuery(new GeoPoint(-33.9258, 18.4232), "Cape Town Residential");
        }
        else
        {
            Console.WriteLine($"South Africa database not found at: {zaDbPath}\n");
        }

        // Australia
        var auDbPath = GetDatabasePath("au_speedlimits.db");
        if (File.Exists(auDbPath))
        {
            Console.WriteLine("\nAustralia Test Locations:");
            Console.WriteLine("─────────────────────────────────────────────────────────────");
            var validator = new ValidationHelper(auDbPath);

            validator.TestLocationQuery(new GeoPoint(-33.8688, 151.2093), "Sydney M1");
            validator.TestLocationQuery(new GeoPoint(-37.8136, 144.9631), "Melbourne M1");
            validator.TestLocationQuery(new GeoPoint(-33.8675, 151.2070), "Sydney Residential");
        }
        else
        {
            Console.WriteLine($"Australia database not found at: {auDbPath}\n");
        }
    }

    static void ReverseGeocodeLookup()
    {
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        REVERSE GEOCODE (Coordinates to Address)            ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝\n");

        // Select database
        Console.WriteLine("Select database:");
        Console.WriteLine("  1. South Africa");
        Console.WriteLine("  2. Australia");
        Console.Write("\nChoice: ");

        var dbChoice = Console.ReadLine()?.Trim();
        string dbPath;

        if (dbChoice == "1")
            dbPath = GetDatabasePath("za_speedlimits.db");
        else if (dbChoice == "2")
            dbPath = GetDatabasePath("au_speedlimits.db");
        else
        {
            Console.WriteLine("Invalid selection.");
            return;
        }

        if (!File.Exists(dbPath))
        {
            Console.WriteLine($"Database not found: {dbPath}");
            return;
        }

        // Get coordinates
        Console.Write("\nEnter coordinates as lat,lon (e.g. -33.9249,18.4241): ");
        var coordInput = Console.ReadLine()?.Trim();

        if (!TryParseCoordinates(coordInput, out double lat, out double lon))
            return;

        Console.WriteLine($"\nReverse geocoding ({lat}, {lon})...\n");

        try
        {
            using var geocoder = new ReverseGeocoder(dbPath);

            if (!geocoder.HasPlaceData)
            {
                Console.WriteLine("Note: This database does not contain place data.");
                Console.WriteLine("Rebuild with option 1 to include suburb/city information.\n");
            }

            var result = geocoder.Lookup(lat, lon);

            Console.WriteLine("Results:");
            Console.WriteLine("─────────────────────────────────────────────────────────────");

            if (result.Street != null)
                Console.WriteLine($"  Street:  {result.Street} [{result.HighwayType}] ({result.StreetDistanceM:F0}m away)");
            else
                Console.WriteLine($"  Street:  (not found)");

            if (result.Suburb != null)
                Console.WriteLine($"  Suburb:  {result.Suburb} [{result.SuburbType}] ({result.SuburbDistanceM:F0}m away)");
            else
                Console.WriteLine($"  Suburb:  (not found)");

            if (result.City != null)
                Console.WriteLine($"  City:    {result.City} [{result.CityType}] ({result.CityDistanceM:F0}m away)");
            else
                Console.WriteLine($"  City:    (not found)");

            Console.WriteLine("─────────────────────────────────────────────────────────────");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Reverse geocode failed: {ex.Message}");
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
        Console.WriteLine($"Total place nodes extracted: {extractor.PlaceNodes.Count:N0}");
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
        builder.BuildDatabase(databasePath, roadSegments, extractor.PlaceNodes, extractor.PlaceBoundaries);

        var dbFileInfo = new FileInfo(databasePath);
        Console.WriteLine($"Database size: {dbFileInfo.Length / (1024.0 * 1024.0):F1} MB");
        Console.WriteLine();

        // Step 4: Validate database
        Console.WriteLine("Step 4: Validating database");

        var validator = new ValidationHelper(databasePath);
        validator.ValidateAndReport();

        var duration = DateTime.UtcNow - startTime;
        Console.WriteLine($"Total processing time: {duration.TotalMinutes:F1} minutes");

        Console.WriteLine($"\n💡 Tip: Copy {databasePath} to Database/ folder for use with lookup tools");
    }

    /// <summary>
    /// Parses "lat,lon" input (e.g. "-33.9249,18.4241" or "-33.9249, 18.4241")
    /// Tip: you can paste directly from Google Maps
    /// </summary>
    private static bool TryParseCoordinates(string? input, out double latitude, out double longitude)
    {
        latitude = 0;
        longitude = 0;

        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("No coordinates entered.");
            return false;
        }

        var parts = input.Split(',');
        if (parts.Length != 2)
        {
            Console.WriteLine("Invalid format. Expected: lat,lon (e.g. -33.9249,18.4241)");
            return false;
        }

        if (!double.TryParse(parts[0].Trim(), out latitude))
        {
            Console.WriteLine($"Invalid latitude: '{parts[0].Trim()}'");
            return false;
        }

        if (!double.TryParse(parts[1].Trim(), out longitude))
        {
            Console.WriteLine($"Invalid longitude: '{parts[1].Trim()}'");
            return false;
        }

        if (latitude < -90 || latitude > 90)
        {
            Console.WriteLine($"Latitude {latitude} out of range (-90 to 90).");
            return false;
        }

        if (longitude < -180 || longitude > 180)
        {
            Console.WriteLine($"Longitude {longitude} out of range (-180 to 180).");
            return false;
        }

        return true;
    }

    private static T GetScalar<T>(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = cmd.ExecuteScalar();
        return result == null || result == DBNull.Value ? default(T)! : (T)Convert.ChangeType(result, typeof(T));
    }
}
