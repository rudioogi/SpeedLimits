# Menu-Driven Interface Guide

## Overview

The application now features an interactive menu system that lets you choose which functionality to use without running the full data acquisition pipeline every time.

## Running the Application

```bash
dotnet run
```

Or use the compiled version:

```bash
cd bin/Release/net8.0
./OsmDataAcquisition.exe
```

## Main Menu

```
╔════════════════════════════════════════════════════════════╗
║                      MAIN MENU                             ║
╠════════════════════════════════════════════════════════════╣
║  1. Download and Process OSM Data (Full Pipeline)          ║
║  2. Validate Pre-Built Databases                           ║
║  3. Test Location Lookup (Custom Coordinates)              ║
║  4. View Database Statistics                               ║
║  5. Test Known Locations                                   ║
║  6. Exit                                                   ║
╚════════════════════════════════════════════════════════════╝
```

## Menu Options Explained

### 1️⃣ Download and Process OSM Data (Full Pipeline)

**Purpose:** Download fresh OSM data and build new databases

**When to use:**
- First-time setup (no pre-built databases)
- Updating databases with latest OSM data
- Regenerating databases after configuration changes

**Process:**
1. Choose country (ZA, AU, or All)
2. Downloads OSM PBF file from Geofabrik
3. Extracts road segments with speed limits
4. Builds SQLite database with spatial grid
5. Validates the new database

**Time:** 8-12 min (ZA), 45-75 min (AU)

**Output:** Creates databases in `data/` folder

**💡 Tip:** After completion, copy the database to `Database/` folder:
```bash
cp data/za_speedlimits.db Database/
```

---

### 2️⃣ Validate Pre-Built Databases

**Purpose:** Verify integrity of databases in `Database/` folder

**When to use:**
- After downloading pre-built databases
- After copying databases from another location
- Checking if databases are corrupted

**Checks performed:**
- File exists and readable
- Schema is correct
- Record counts (roads, grid cells)
- Speed limit distribution
- Highway type distribution
- Metadata completeness

**Example output:**
```
Total road segments: 85,423
  Explicit speed limits: 12,567 (14.7%)
  Inferred speed limits: 72,856 (85.3%)
Grid cells populated: 4,521
```

---

### 3️⃣ Test Location Lookup (Custom Coordinates)

**Purpose:** Test speed limit lookup for any GPS coordinates

**When to use:**
- Testing specific locations
- Verifying database accuracy
- Debugging lookup queries
- Learning how the system works

**How to use:**
1. Choose database (ZA or AU)
2. Enter latitude (e.g., -33.9249)
3. Enter longitude (e.g., 18.4241)
4. View nearby roads and speed limits

**Example output:**
```
1. N1
   Type: motorway
   Speed: 120 km/h (inferred)
   Distance: 245m
   Center: (-33.925123, 18.424567)

2. Main Road
   Type: primary
   Speed: 100 km/h (inferred)
   Distance: 512m
   Center: (-33.926789, 18.423456)
```

**Search radius:** ±2km (approximately 0.02 degrees)

---

### 4️⃣ View Database Statistics

**Purpose:** View summary statistics for all databases

**When to use:**
- Quick overview of database contents
- Checking file sizes
- Viewing coverage bounds
- Understanding data quality

**Information shown:**
- File size (MB)
- Total road segments
- Explicit vs inferred speed limits (percentage)
- Grid cells populated
- Geometry points
- Geographic bounds (min/max lat/lon)
- Creation date

**Example output:**
```
South Africa
============================================================
File size: 87.5 MB
Path: Database/za_speedlimits.db

Total road segments: 85,423
  Explicit speed limits: 12,567 (14.7%)
  Inferred speed limits: 72,856 (85.3%)
Grid cells populated: 4,521
Geometry points: 1,234,567

created_date: 2024-02-11T10:30:00Z
min_latitude: -34.8
max_latitude: -22.1
min_longitude: 16.5
max_longitude: 32.9
```

---

### 5️⃣ Test Known Locations

**Purpose:** Test pre-defined locations to verify database accuracy

**When to use:**
- Validating database after creation
- Checking if data looks reasonable
- Quick smoke test

**Tested locations:**

**South Africa:**
- Cape Town N1 (-33.9249, 18.4241) - Expected: ~120 km/h
- Johannesburg M1 (-26.2041, 28.0473) - Expected: ~120 km/h
- Cape Town Residential (-33.9258, 18.4232) - Expected: ~60 km/h

**Australia:**
- Sydney M1 (-33.8688, 151.2093) - Expected: ~110 km/h
- Melbourne M1 (-37.8136, 144.9631) - Expected: ~100 km/h
- Sydney Residential (-33.8675, 151.2070) - Expected: ~50 km/h

**Example output:**
```
Testing location: Cape Town N1 (-33.924900, 18.424100)
  N1 [motorway] 120 km/h (inferred) - 245m away
```

---

### 6️⃣ Exit

Exits the application.

---

## Typical Workflows

### 🆕 First-Time Setup (No Pre-Built Databases)

1. Start application: `dotnet run`
2. Choose **Option 1** (Download and Process OSM Data)
3. Select country or "All countries"
4. Wait for processing to complete
5. Copy databases to `Database/` folder:
   ```bash
   cp data/za_speedlimits.db Database/
   cp data/au_speedlimits.db Database/
   ```
6. Choose **Option 2** (Validate) to verify

---

### ✅ Using Pre-Built Databases

1. Place databases in `Database/` folder:
   - `Database/za_speedlimits.db`
   - `Database/au_speedlimits.db`
2. Start application: `dotnet run`
3. Choose **Option 2** (Validate) to verify integrity
4. Choose **Option 3** (Test Location) to try lookups
5. Use databases with lookup code (see `speedlimit_lookup.c` or `.cs`)

---

### 🔄 Updating Databases (Monthly/Quarterly)

1. Start application: `dotnet run`
2. Choose **Option 1** (Download and Process OSM Data)
3. Select specific country to update
4. Wait for processing
5. Choose **Option 2** (Validate) to verify new database
6. Choose **Option 5** (Test Known Locations) for quick check
7. Copy new database to `Database/` folder

---

### 🧪 Testing and Development

1. Start application: `dotnet run`
2. Choose **Option 3** (Test Location Lookup)
3. Enter various coordinates to test
4. Check **Option 4** (Statistics) to understand data
5. Use **Option 5** (Known Locations) as baseline

---

## Tips

### 💡 Skip Data Processing

If you already have pre-built databases in `Database/` folder, you don't need to run Option 1 at all! Just use Options 2-5 for validation and testing.

### 💡 Testing Lookups

Use Option 3 to test any GPS coordinates before implementing in your application. This helps verify:
- Database coverage for your region
- Expected speed limits for road types
- Query accuracy and distance calculations

### 💡 Performance Monitoring

Option 4 shows how many grid cells are populated. More cells = better coverage but larger file size. Typical:
- ZA: 4,000-5,000 cells
- AU: 20,000-30,000 cells

### 💡 Data Quality

Check the explicit vs inferred ratio in Option 4:
- **10-20% explicit** is normal (most roads lack maxspeed tags)
- **Higher % explicit** means better data quality
- **Inferred speeds** are still reliable (based on highway type)

---

## Pre-Built Database Locations

**Default paths** (used by menu Options 2-5):
- `Database/za_speedlimits.db`
- `Database/au_speedlimits.db`

**Generated paths** (created by Option 1):
- `data/za_speedlimits.db`
- `data/au_speedlimits.db`

After generating new databases with Option 1, copy them to `Database/` folder to use with other menu options and lookup tools.

---

## Keyboard Shortcuts

- **1-6**: Select menu option
- **Enter**: Confirm selection
- **Any key**: Continue after operation completes
- **Ctrl+C**: Exit application immediately

---

## Error Handling

The menu system handles errors gracefully:
- Invalid input → Returns to menu
- Missing database → Shows warning, continues
- Processing failure → Shows error details, continues to next country
- File not found → Prompts for correct path

You can always return to the main menu and try again!

---

## Command-Line Alternative

If you prefer non-interactive usage, use the lookup tools directly:

**C:**
```bash
gcc -o speedlimit speedlimit_lookup.c -lsqlite3 -lm
./speedlimit Database/za_speedlimits.db -33.9249 18.4241
```

**C#:**
```csharp
using var lookup = new SpeedLimitLookup("Database/za_speedlimits.db");
int speed = lookup.GetSpeedLimit(-33.9249, 18.4241);
```

**SQL:**
```bash
sqlite3 Database/za_speedlimits.db < query.sql
```

---

## Summary

**For first-time users:** Use Option 1 to build databases, then Options 2-5 to explore

**For IoT developers:** Use Options 2-3 to validate databases, then integrate `speedlimit_lookup.c` or `.cs`

**For updates:** Use Option 1 periodically (monthly/quarterly) to refresh OSM data

**For testing:** Use Options 3-5 to verify accuracy before deployment

The menu system makes it easy to access all functionality without writing code or running complex commands!
