using System.Globalization;
using LunchSuggest.Modules.Search;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LunchSuggest.Modules.Places.Internal;

internal sealed record PlacesOptions(TimeSpan CacheFor);

internal sealed class PlaceDirectory(
    IDbContextFactory<PlacesDbContext> dbFactory,
    IPlaceProvider provider,
    ISemanticIndex index,
    PlacesOptions options,
    ILogger<PlaceDirectory> log) : IPlaceDirectory
{
    // Sparse map data (e.g. OSM outside Europe): widen the search until there's enough choice.
    internal const int MinPlaces = 20, MaxRadiusMeters = 2000;
    readonly SemaphoreSlim _fetchLock = new(1, 1);

    public async Task<IReadOnlyList<PlaceInfo>> GetNearbyAsync(double lat, double lng, int radiusMeters, CancellationToken ct = default)
    {
        var radius = radiusMeters;
        var result = await LoadAsync(lat, lng, radius, ct);
        while (result.Count < MinPlaces && radius < MaxRadiusMeters)
        {
            radius = Math.Min(radius * 2, MaxRadiusMeters);
            result = await LoadAsync(lat, lng, radius, ct);
        }
        if (radius > radiusMeters) // keep the nearest few beyond the asked radius, not the whole widened area
            result = result.Where((p, i) => p.DistanceMeters <= radiusMeters || i < MinPlaces).ToList();

        // Embedding is slow on CPU, so don't make the page wait. Already-indexed places are skipped, so this self-heals if Ollama was down.
        var docs = result.Select(p => new SearchDocument(p.Id, DescribeForSearch(p), p.Lat, p.Lng)).ToList();
        _ = Task.Run(async () =>
        {
            try { await index.IndexAsync(docs); }
            catch (Exception ex) { log.LogWarning(ex, "Semantic indexing failed for {Count} places", docs.Count); }
        });
        return result;
    }

    async Task<List<PlaceInfo>> LoadAsync(double lat, double lng, int radiusMeters, CancellationToken ct)
    {
        // ~110 m grid: people in the same office share one fetch.
        // Provider in the key and the filter: switching provider must not serve (or mix in) the old one's data.
        var key = string.Create(CultureInfo.InvariantCulture, $"{provider.Source}:{Math.Round(lat, 3)}:{Math.Round(lng, 3)}:{radiusMeters}");
        await EnsureFetchedAsync(key, lat, lng, radiusMeters, ct);

        var dLat = radiusMeters / 111_320d;
        var dLng = radiusMeters / (111_320d * Math.Cos(lat * Math.PI / 180));
        var idPrefix = provider.Source + ":";
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Places.AsNoTracking()
            .Where(p => p.Id.StartsWith(idPrefix))
            .Where(p => p.Lat >= lat - dLat && p.Lat <= lat + dLat && p.Lng >= lng - dLng && p.Lng <= lng + dLng)
            .ToListAsync(ct);

        return rows
            .Select(p => new PlaceInfo(p.Id, p.Name, p.Cuisine, p.Category, p.Address, p.Lat, p.Lng, Geo.DistanceMeters(lat, lng, p.Lat, p.Lng)))
            .Where(p => p.DistanceMeters <= radiusMeters)
            .OrderBy(p => p.DistanceMeters)
            .ToList();
    }

    internal static string DescribeForSearch(PlaceInfo p) =>
        string.Join(". ", new[] { p.Name, p.Category, p.Cuisine is null ? null : $"Cuisine: {p.Cuisine}",
                DishGlossary.Describe(p.Name) is { } dishes ? $"Serves: {dishes}" : null, p.Address }
            .Where(s => !string.IsNullOrWhiteSpace(s)));

    async Task EnsureFetchedAsync(string key, double lat, double lng, int radiusMeters, CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var fetched = await db.AreaFetches.FindAsync([key], ct);
            if (fetched is not null && fetched.FetchedAt > DateTimeOffset.UtcNow - options.CacheFor) return;

            var places = (await provider.FetchAsync(lat, lng, radiusMeters, ct)).DistinctBy(p => p.Id).ToList();
            var ids = places.Select(p => p.Id).ToList();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Places.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct);
            db.Places.AddRange(places);
            if (fetched is null) db.AreaFetches.Add(new AreaFetch { Key = key, FetchedAt = DateTimeOffset.UtcNow });
            else fetched.FetchedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            log.LogInformation("Fetched {Count} places for area {Key}", places.Count, key);
        }
        finally { _fetchLock.Release(); }
    }
}

internal static class Geo
{
    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6_371_000, rad = Math.PI / 180;
        var a = Math.Pow(Math.Sin((lat2 - lat1) * rad / 2), 2)
              + Math.Cos(lat1 * rad) * Math.Cos(lat2 * rad) * Math.Pow(Math.Sin((lng2 - lng1) * rad / 2), 2);
        return 2 * R * Math.Asin(Math.Sqrt(a));
    }
}
