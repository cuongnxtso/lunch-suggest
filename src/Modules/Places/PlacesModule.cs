using LunchSuggest.Modules.Places.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LunchSuggest.Modules.Places;

public sealed record PlaceInfo(string Id, string Name, string? Cuisine, string Category, string? Address,
    double Lat, double Lng, double DistanceMeters);

/// <summary>A place someone suggested on the poll page. Listed nearby once approved on the Settings page.</summary>
/// <param name="Id">Place id, starting with <see cref="PlacesModule.SuggestedIdPrefix"/>.</param>
public sealed record PlaceSuggestion(string Id, string Name, string? Cuisine, string? Address, double Lat, double Lng,
    string SuggestedBy, DateTimeOffset SuggestedAt, bool Approved);

/// <summary>A request to take a place off the list (closed, wrong, not food). Hidden for everyone once approved.</summary>
public sealed record PlaceRemoval(string PlaceId, string Name, double Lat, double Lng, string SuggestedBy,
    DateTimeOffset SuggestedAt, bool Approved);

/// <summary>Public contract of the Places module. Everything else in this project is internal.</summary>
public interface IPlaceDirectory
{
    /// <summary>Raised when a place (or its removal) is suggested, approved or deleted (the review list and nearby lists refresh).</summary>
    event Action? SuggestionsChanged;

    /// <summary>
    /// Food places around a point, nearest first: the map provider's (fetched when the area is not cached) plus approved suggestions,
    /// minus approved removals.
    /// </summary>
    Task<IReadOnlyList<PlaceInfo>> GetNearbyAsync(double lat, double lng, int radiusMeters, CancellationToken ct = default);

    /// <summary>Saves a suggested place, waiting for approval, and returns its place id. Bad input: <see cref="ArgumentException"/>.</summary>
    Task<string> SuggestPlaceAsync(string name, string? cuisine, string? address, double lat, double lng, string suggestedBy, CancellationToken ct = default);

    /// <summary>Every suggested place: waiting ones first, then newest first.</summary>
    Task<IReadOnlyList<PlaceSuggestion>> GetSuggestedPlacesAsync(CancellationToken ct = default);

    /// <summary>Approves a suggested place, or deletes it (rejecting it, or removing an approved one). False if there is no such place.</summary>
    Task<bool> ReviewPlaceAsync(string id, bool approve, CancellationToken ct = default);

    /// <summary>Asks for a place to be taken off the list, waiting for approval. Already asked for: no-op.</summary>
    Task SuggestRemovalAsync(PlaceInfo place, string suggestedBy, CancellationToken ct = default);

    /// <summary>Every removal request: waiting ones first, then newest first.</summary>
    Task<IReadOnlyList<PlaceRemoval>> GetRemovalsAsync(CancellationToken ct = default);

    /// <summary>Approves a removal (the place is hidden), or deletes the request (rejecting it, or putting the place back).</summary>
    Task<bool> ReviewRemovalAsync(string placeId, bool approve, CancellationToken ct = default);
}

public static class PlacesModule
{
    /// <summary>Place ids of suggested places start with this ("user:..."); provider places have their own ("overture:...").</summary>
    public const string SuggestedIdPrefix = "user:";

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
