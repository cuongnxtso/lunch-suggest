using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LunchSuggest.Modules.Dishes.Internal;

/// <summary>Queues dish lookups and runs them one at a time: the local LLM is CPU-bound, so parallel runs only slow each other down.</summary>
internal sealed class DishGuide(
    IDbContextFactory<DishesDbContext> dbFactory,
    IServiceProvider services,
    DishExtractor extractor,
    ILogger<DishGuide> log) : BackgroundService, IDishGuide
{
    static readonly TimeSpan CacheFor = TimeSpan.FromDays(30);
    // Two lanes: urgent lookups (someone is waiting on a spinner for today's picks) go before background ones.
    readonly Channel<DishQuery> _urgent = Channel.CreateUnbounded<DishQuery>(), _normal = Channel.CreateUnbounded<DishQuery>();
    readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();

    // ponytail: in-process queue and event = one server instance; queued lookups are lost on restart (users just click again).
    public event Action<string>? LookupFinished;

    public bool IsPending(string placeId) => _pending.ContainsKey(placeId);

    public void Request(DishQuery place) => Enqueue(place, urgent: false);

    public async Task<IReadOnlyList<string>> GetOrLookUpAsync(DishQuery place, CancellationToken ct = default)
    {
        if ((await GetKnownAsync([place.PlaceId], ct)).TryGetValue(place.PlaceId, out var known)) return known;
        await Enqueue(place, urgent: true).WaitAsync(ct);
        return (await GetKnownAsync([place.PlaceId], ct)).GetValueOrDefault(place.PlaceId) ?? [];
    }

    Task Enqueue(DishQuery place, bool urgent)
    {
        var added = false;
        var tcs = _pending.GetOrAdd(place.PlaceId, _ => { added = true; return new(TaskCreationOptions.RunContinuationsAsynchronously); });
        // Already waiting in the normal lane and now urgent: queue it again up front; the worker skips the stale copy.
        if (added || urgent) (urgent ? _urgent : _normal).Writer.TryWrite(place);
        return tcs.Task;
    }

    Task<bool>? _waitUrgent, _waitNormal; // reused, so the idle lane doesn't pile up a waiter per lookup

    async Task<DishQuery> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            if (_urgent.Reader.TryRead(out var u) || _normal.Reader.TryRead(out u)) return u;
            _waitUrgent = _waitUrgent is { IsCompleted: false } ? _waitUrgent : _urgent.Reader.WaitToReadAsync(ct).AsTask();
            _waitNormal = _waitNormal is { IsCompleted: false } ? _waitNormal : _normal.Reader.WaitToReadAsync(ct).AsTask();
            await Task.WhenAny(_waitUrgent, _waitNormal);
            ct.ThrowIfCancellationRequested();
        }
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetKnownAsync(IReadOnlyCollection<string> placeIds, CancellationToken ct = default)
    {
        var fresh = DateTimeOffset.UtcNow - CacheFor;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.PlaceDishes.AsNoTracking()
            .Where(d => placeIds.Contains(d.PlaceId) && d.LookedUpAt > fresh)
            .ToDictionaryAsync(d => d.PlaceId, d => (IReadOnlyList<string>)d.Dishes, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            DishQuery place;
            try { place = await NextAsync(stoppingToken); }
            catch (OperationCanceledException) { return; }
            if (!_pending.ContainsKey(place.PlaceId)) continue; // stale duplicate, already done
            try
            {
                if ((await GetKnownAsync([place.PlaceId], stoppingToken)).Count == 0)
                    await LookUpAsync(place, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            // Not cached on failure, so the next request retries.
            catch (Exception ex) { log.LogWarning(ex, "Dish lookup failed for {Place}", place.Name); }
            finally
            {
                if (_pending.TryRemove(place.PlaceId, out var done)) done.TrySetResult();
                LookupFinished?.Invoke(place.PlaceId);
            }
        }
    }

    async Task LookUpAsync(DishQuery place, CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;
        // Typed HttpClients are transient; resolve per lookup instead of holding one forever.
        var search = services.GetRequiredService<WebSearch>();
        var results = await search.SearchAsync($"{place.Name} {place.Address} menu", 8, ct);
        if (results.Count == 0)
        {
            // Usually upstream engines rate-limiting SearXNG: don't cache "no dishes" for a month, let the next request retry.
            log.LogWarning("Web search returned nothing for {Place}; not caching", place.Name);
            return;
        }
        var dishes = await extractor.ExtractAsync(place, results, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PlaceDishes.Where(d => d.PlaceId == place.PlaceId).ExecuteDeleteAsync(ct);
        db.PlaceDishes.Add(new PlaceDishes { PlaceId = place.PlaceId, Dishes = dishes, LookedUpAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        log.LogInformation("Found {Count} dishes for {Place} from {Results} results in {Seconds:F0}s",
            dishes.Count, place.Name, results.Count, (DateTimeOffset.UtcNow - started).TotalSeconds);
    }
}
