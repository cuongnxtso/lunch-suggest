using LunchSuggest.Modules.Places.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LunchSuggest.Modules.Places;

public sealed record PlaceInfo(string Id, string Name, string? Cuisine, string Category, string? Address,
    double Lat, double Lng, double DistanceMeters);

/// <summary>Public contract of the Places module. Everything else in this project is internal.</summary>
public interface IPlaceDirectory
{
    /// <summary>Food places around a point, nearest first. Fetches from the map provider when the area is not cached.</summary>
    Task<IReadOnlyList<PlaceInfo>> GetNearbyAsync(double lat, double lng, int radiusMeters, CancellationToken ct = default);
}

public static class PlacesModule
{
    public static IServiceCollection AddPlacesModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContextFactory<PlacesDbContext>(o => o.UseNpgsql(config.GetConnectionString("Lunch"),
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", PlacesDbContext.Schema)));

        var provider = config["Places:Provider"] ?? "Overture";
        if (string.Equals(provider, "Overture", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton(new OvertureOptions(config["Places:OvertureRelease"] is { Length: > 0 } r ? r : null));
            // The catalog's CDN rejects requests without a User-Agent (403).
            services.AddHttpClient<IPlaceProvider, OverturePlaceProvider>(h => h.DefaultRequestHeaders.UserAgent.ParseAdd("LunchSuggest/1.0"));
        }
        else if (string.Equals(provider, "Google", StringComparison.OrdinalIgnoreCase))
        {
            var googleKey = config["Places:GoogleApiKey"];
            if (string.IsNullOrWhiteSpace(googleKey))
                throw new InvalidOperationException("Places:Provider is Google but Places:GoogleApiKey is not set.");
            services.AddHttpClient<IPlaceProvider, GooglePlacesProvider>(h =>
                h.DefaultRequestHeaders.Add("X-Goog-Api-Key", googleKey));
        }
        else
        {
            services.AddHttpClient<IPlaceProvider, OverpassPlaceProvider>(h =>
            {
                h.BaseAddress = new Uri(config["Places:OverpassUrl"] ?? "https://overpass-api.de/api/interpreter");
                h.Timeout = TimeSpan.FromSeconds(40);
                // Overpass asks apps to identify themselves.
                h.DefaultRequestHeaders.UserAgent.ParseAdd("LunchSuggest/1.0");
            });
        }
        services.AddSingleton(new PlacesOptions(TimeSpan.FromDays(config.GetValue("Places:CacheDays", 7))));
        services.AddSingleton<IPlaceDirectory, PlaceDirectory>();
        return services;
    }

    public static async Task MigratePlacesModuleAsync(this IServiceProvider sp)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<PlacesDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}
