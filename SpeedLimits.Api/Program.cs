using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;
using SpeedLimits.Core.Configuration;
using SpeedLimits.Api.Configuration;
using SpeedLimits.Api.Middleware;
using SpeedLimits.Api.Services;

namespace SpeedLimits.Api;

public class Program
{
    public static void Main(string[] args)
    {
        LoadDotEnv();

        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new()
            {
                Title = "SpeedLimits API",
                Version = "v1",
                Description = "REST API for querying OSM speed limit databases and triggering data acquisition."
            });

            // API key security definition
            const string securityScheme = "ApiKey";
            options.AddSecurityDefinition(securityScheme, new OpenApiSecurityScheme
            {
                Name = "X-Api-Key",
                Type = SecuritySchemeType.ApiKey,
                In   = ParameterLocation.Header,
                Description = "API key required for all endpoints. Pass via the X-Api-Key header."
            });
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = securityScheme }
                    },
                    Array.Empty<string>()
                }
            });
        });

        // Configuration bindings
        builder.Services.Configure<DatabaseSettings>(
            builder.Configuration.GetSection(DatabaseSettings.SectionName));
        builder.Services.Configure<DataAcquisitionConfig>(
            builder.Configuration.GetSection("DataAcquisition"));
        builder.Services.Configure<DatabaseConfig>(
            builder.Configuration.GetSection("Database"));

        // Application services
        builder.Services.AddSingleton<DatabasePathResolver>();
        builder.Services.AddScoped<SpeedLimitService>();
        builder.Services.AddScoped<DatabaseInfoService>();

        var app = builder.Build();

        app.UseSwagger();
        app.UseSwaggerUI(options =>
        {
            options.SwaggerEndpoint("/swagger/v1/swagger.json", "SpeedLimits API v1");
            options.RoutePrefix = string.Empty; // Swagger at root
        });

        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseAuthorization();
        app.MapControllers();

        app.Run();
    }

    /// <summary>
    /// Loads key=value pairs from a .env file next to the executable (or project root
    /// when running under dotnet run) into the current process environment.
    /// Lines starting with # and blank lines are ignored. Existing env vars are not
    /// overwritten, so real environment variables always take precedence.
    /// </summary>
    private static void LoadDotEnv()
    {
        var envFile = FindDotEnvFile();
        if (envFile == null) return;

        foreach (var raw in File.ReadAllLines(envFile))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;

            var key   = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            // Real env vars (process-inherited, user-registry, machine-registry) always
            // win over .env file values. Promote whichever we find into the process block
            // so the rest of the app can use the plain GetEnvironmentVariable(key) call.
            var real = Environment.GetEnvironmentVariable(key)
                ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User)
                ?? Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine);

            Environment.SetEnvironmentVariable(key, real ?? value);
        }
    }

    private static string? FindDotEnvFile()
    {
        // 1. Current working directory — this is the project root under `dotnet run`
        var cwd = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(cwd)) return cwd;

        // 2. Walk up from the executable directory (covers published / IDE-run scenarios).
        //    Trim the trailing separator first; otherwise Path.GetDirectoryName just
        //    strips it without actually moving up a level.
        var dir = AppContext.BaseDirectory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        for (int i = 0; i < 6; i++)
        {
            var candidate = Path.Combine(dir, ".env");
            if (File.Exists(candidate)) return candidate;

            var parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }

        return null;
    }
}
