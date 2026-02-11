namespace OsmDataAcquisition.Configuration;

/// <summary>
/// Configuration for database creation and optimization
/// </summary>
public class DatabaseConfig
{
    /// <summary>
    /// Grid size for spatial indexing (e.g., 1000 = 1000x1000 grid)
    /// </summary>
    public int GridSize { get; set; } = 1000;

    /// <summary>
    /// SQLite cache size in KB
    /// </summary>
    public int CacheSizeKB { get; set; } = 65536; // 64MB

    /// <summary>
    /// Memory-mapped I/O size in MB
    /// </summary>
    public int MmapSizeMB { get; set; } = 256;

    /// <summary>
    /// Database page size in bytes
    /// </summary>
    public int PageSize { get; set; } = 4096;
}
