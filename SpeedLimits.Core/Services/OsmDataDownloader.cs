using SpeedLimits.Core.Configuration;
using SpeedLimits.Core.Utilities;

namespace SpeedLimits.Core.Services;

/// <summary>
/// Downloads OSM PBF files from Geofabrik with retry logic
/// </summary>
public class OsmDataDownloader : IDisposable
{
    private readonly DataAcquisitionConfig _config;
    private readonly HttpClient _httpClient;

    public OsmDataDownloader(DataAcquisitionConfig config)
    {
        _config = config;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromHours(2) // Large files take time
        };
    }

    /// <summary>
    /// Downloads OSM data for a country
    /// </summary>
    public async Task<string> DownloadCountryDataAsync(CountryConfig country)
    {
        // Ensure download directory exists
        Directory.CreateDirectory(_config.DownloadDirectory);

        var fileName = $"{country.Code.ToLower()}-latest.osm.pbf";
        var filePath = Path.Combine(_config.DownloadDirectory, fileName);

        // Check if file already exists
        if (File.Exists(filePath))
        {
            ConsoleProgressReporter.Report($"Using existing file: {filePath}");
            return filePath;
        }

        ConsoleProgressReporter.Report($"Downloading OSM data from: {country.GeofabrikUrl}");

        // Download with retry logic
        for (int attempt = 1; attempt <= _config.RetryAttempts; attempt++)
        {
            try
            {
                await DownloadFileAsync(country.GeofabrikUrl, filePath);
                ConsoleProgressReporter.Report($"Download complete: {filePath}");
                return filePath;
            }
            catch (Exception ex) when (attempt < _config.RetryAttempts)
            {
                ConsoleProgressReporter.Report($"Download attempt {attempt} failed: {ex.Message}");
                ConsoleProgressReporter.Report($"Retrying in {_config.RetryDelaySeconds} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(_config.RetryDelaySeconds * attempt));

                // Clean up partial file
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
        }

        throw new Exception($"Failed to download after {_config.RetryAttempts} attempts");
    }

    private async Task DownloadFileAsync(string url, string destinationPath)
    {
        var progress = new ConsoleProgressReporter("Downloading");

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;

        await using var contentStream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true);

        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;
        var lastReportTime = DateTime.UtcNow;

        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;

            // Report progress every 500ms
            if ((DateTime.UtcNow - lastReportTime).TotalMilliseconds > 500)
            {
                progress.ReportDownload(totalRead, totalBytes);
                lastReportTime = DateTime.UtcNow;
            }
        }

        progress.Complete($"Downloaded {totalRead / (1024.0 * 1024.0):F1} MB");
    }

    public void Dispose()
    {
        _httpClient?.Dispose();
    }
}
