using Microsoft.Extensions.Options;
using OsmDataAcquisition.Configuration;
using SpeedLimits.Api.Services;

namespace SpeedLimits.Api;

public class Program
{
    public static void Main(string[] args)
    {
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
        });

        // Configuration bindings
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

        app.UseAuthorization();
        app.MapControllers();

        app.Run();
    }
}
