# Database Path Resolution

## Problem

When running a .NET application, the working directory can vary:
- **`dotnet run`**: Working directory is the project root
- **Debug**: Running from Visual Studio uses bin/Debug/net8.0
- **Release**: Running compiled exe from bin/Release/net8.0

This caused issues with relative paths like `Database/za_speedlimits.db`.

## Solution

The application now uses intelligent path resolution that works in all scenarios.

### How It Works

1. **Check current directory**: Looks for `./Database/` folder
2. **Check relative to executable**: Looks for `Database/` next to the .exe
3. **Check project root**: Navigates up from bin folder to project root
4. **Create if needed**: Creates Database folder in current directory as fallback

### Code Implementation

```csharp
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
```

### Build Configuration

The `.csproj` file is configured to copy the `Database/` folder to the output directory:

```xml
<ItemGroup>
  <None Update="Database\**\*">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

This ensures that when you build, the Database folder and its contents are copied to:
- `bin/Debug/net8.0/Database/`
- `bin/Release/net8.0/Database/`

## Directory Structure

### Development (Project Root)

```
SpeedLimits/
├── Database/
│   ├── za_speedlimits.db
│   └── au_speedlimits.db
├── Program.cs
├── OsmDataAcquisition.csproj
└── ...
```

**Running:** `dotnet run`
**Working Directory:** `C:\Dev\SandBox\SpeedLimits`
**Database Path:** `C:\Dev\SandBox\SpeedLimits\Database\za_speedlimits.db`

---

### Debug Build

```
SpeedLimits/
├── bin/
│   └── Debug/
│       └── net8.0/
│           ├── Database/              ← Copied during build
│           │   ├── za_speedlimits.db
│           │   └── au_speedlimits.db
│           ├── OsmDataAcquisition.exe
│           └── OsmDataAcquisition.dll
└── ...
```

**Running:** `bin/Debug/net8.0/OsmDataAcquisition.exe`
**Working Directory:** `C:\Dev\SandBox\SpeedLimits\bin\Debug\net8.0`
**Database Path:** `C:\Dev\SandBox\SpeedLimits\bin\Debug\net8.0\Database\za_speedlimits.db`

---

### Release Build (Deployed)

```
OsmDataAcquisition/
├── Database/                          ← Copied during build
│   ├── za_speedlimits.db
│   └── au_speedlimits.db
├── OsmDataAcquisition.exe
├── OsmDataAcquisition.dll
├── appsettings.json
└── ...
```

**Running:** `OsmDataAcquisition.exe`
**Working Directory:** `C:\MyApp\OsmDataAcquisition`
**Database Path:** `C:\MyApp\OsmDataAcquisition\Database\za_speedlimits.db`

## Testing Path Resolution

### Test 1: From Project Root

```bash
cd C:\Dev\SandBox\SpeedLimits
dotnet run
```

**Expected:** Finds `./Database/za_speedlimits.db`

---

### Test 2: From Debug Folder

```bash
cd bin/Debug/net8.0
./OsmDataAcquisition.exe
```

**Expected:** Finds `./Database/za_speedlimits.db` (copied during build)

---

### Test 3: From Release Folder

```bash
cd bin/Release/net8.0
./OsmDataAcquisition.exe
```

**Expected:** Finds `./Database/za_speedlimits.db` (copied during build)

---

### Test 4: From Visual Studio

Press F5 in Visual Studio

**Expected:** Working directory is set to project root, finds `./Database/za_speedlimits.db`

## Deployment

When deploying the application, copy the entire output folder:

```bash
# Copy Release build
cp -r bin/Release/net8.0/* /opt/speedlimit-app/

# Database folder is included automatically
ls /opt/speedlimit-app/Database/
# za_speedlimits.db
# au_speedlimits.db
```

The application will work immediately because:
1. Database folder is in the same directory as the executable
2. Path resolution finds it automatically

## Troubleshooting

### "Database not found" error

**Cause:** Database folder doesn't exist or is empty

**Solutions:**

1. **Check folder exists:**
   ```bash
   ls -la Database/
   ```

2. **Verify databases exist:**
   ```bash
   ls -la Database/*.db
   ```

3. **Rebuild to copy Database folder:**
   ```bash
   dotnet build
   ```

4. **Manually place databases:**
   ```bash
   mkdir -p Database
   cp za_speedlimits.db Database/
   cp au_speedlimits.db Database/
   ```

### "No such file or directory" when running from bin

**Cause:** Database folder wasn't copied during build

**Solution:**

Verify `.csproj` has the copy configuration:
```xml
<None Update="Database\**\*">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

Then rebuild:
```bash
dotnet clean
dotnet build
```

### Databases not updating after regeneration

**Cause:** Old databases in bin folder

**Solution:**

1. Clean build output:
   ```bash
   dotnet clean
   ```

2. Copy new databases to source:
   ```bash
   cp data/za_speedlimits.db Database/
   ```

3. Rebuild:
   ```bash
   dotnet build
   ```

## Best Practices

### ✅ Do:
- Place production databases in `Database/` folder at project root
- Let the build system copy them automatically
- Use `GetDatabasePath()` function in code for paths
- Test from both project root and bin folders

### ❌ Don't:
- Hard-code absolute paths (not portable)
- Use only relative paths without fallback
- Forget to rebuild after updating databases
- Manually copy databases to bin (use build config instead)

## Summary

The path resolution system ensures the application works in all scenarios:
- ✅ Development: `dotnet run` from project root
- ✅ Debug: Running from `bin/Debug/net8.0`
- ✅ Release: Running from `bin/Release/net8.0`
- ✅ Deployed: Running from any installation directory

The `Database/` folder is automatically copied during build, making the application fully self-contained and portable!
