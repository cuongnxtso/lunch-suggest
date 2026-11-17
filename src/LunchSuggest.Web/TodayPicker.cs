using System.Collections.Concurrent;
using LunchSuggest.Modules.Dishes;
using LunchSuggest.Modules.Places;
using LunchSuggest.Modules.Voting;

namespace LunchSuggest.Web;

/// <summary>
/// Fills a newly created poll with 3–5 dishes: recent runner-ups plus random dishes from random nearby places.
/// Lives in the host because it combines three modules (Voting, Places, Dishes) through their public interfaces.
/// </summary>
public sealed class TodayPicker(ILunchSessions sessions, IDishGuide dishes, ILogger<TodayPicker> log)
{
    internal const int MinPicks = 3, MaxPicks = 5, RunnerUpDays = 5;
    static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(2);

    // One run per poll at a time: everyone who opens the page meanwhile waits on the same run.
    readonly ConcurrentDictionary<(string Code, DateOnly Day), Lazy<Task>> _running = new();

    /// <summary>Puts the starting dishes in the poll for <paramref name="day"/> if it exists and hasn't had them yet.</summary>
    public Task EnsureSeededAsync(string code, DateOnly day, IReadOnlyList<PlaceInfo> nearby) =>
        _running.GetOrAdd((code, day), key => new Lazy<Task>(() => RunAsync(key.Code, key.Day, nearby))).Value;

    async Task RunAsync(string code, DateOnly day, IReadOnlyList<PlaceInfo> nearby)
    {
        try
        {
            using var timeout = new CancellationTokenSource(GiveUpAfter);
            await PickAsync(code, day, nearby, timeout.Token);
        }
        catch (Exception ex) { log.LogWarning(ex, "Picking dishes for the {Day} poll failed ({Code})", day, code); }
        finally { _running.TryRemove((code, day), out _); }
    }

    async Task PickAsync(string code, DateOnly day, IReadOnlyList<PlaceInfo> nearby, CancellationToken ct)
    {
        var poll = await sessions.GetAsync(code, day, null, ct);
        if (poll is not { HasPoll: true, Seeded: false } || nearby.Count == 0) return;

        var count = Random.Shared.Next(MinPicks, MaxPicks + 1);
        // Leave at least one slot for something new.
        var runnerUps = (await sessions.GetRecentRunnerUpsAsync(code, day, RunnerUpDays, ct)).Take(count - 1).ToList();
        var need = count - runnerUps.Count;

        // Random places (not just the nearest), one dish each for variety. A couple of spares in case a place has no dishes online.
        var used = runnerUps.Select(r => r.PlaceId).ToHashSet();
        var pool = nearby.Where(p => !used.Contains(p.Id)).OrderBy(_ => Random.Shared.Next()).Take(need + 2).ToList();
        var looked = await Task.WhenAll(pool.Select(async p =>
        {
            try { return (Place: p, Dishes: await dishes.GetOrLookUpAsync(new DishQuery(p.Id, p.Name, p.Category, p.Address), ct)); }
            catch (OperationCanceledException) { return (Place: p, Dishes: (IReadOnlyList<string>)[]); }
        }));

        var randoms = looked.Where(l => l.Dishes.Count > 0).Take(need)
            .Select(l => ToCandidate(l.Place, l.Dishes[Random.Shared.Next(l.Dishes.Count)]))
            .ToList();
        // Places with no dishes online still make the poll, as the place itself.
        randoms.AddRange(looked.Where(l => l.Dishes.Count == 0).Take(need - randoms.Count).Select(l => ToCandidate(l.Place, null)));

        if (runnerUps.Count + randoms.Count == 0) return; // don't mark the poll done with nothing; the next visit retries
        await sessions.SeedPollAsync(code, day, runnerUps, randoms, CancellationToken.None);
        log.LogInformation("Picks for the {Day} poll ({Code}): {RunnerUps} runner-ups + {Randoms} random", day, code, runnerUps.Count, randoms.Count);
    }

    static NewCandidate ToCandidate(PlaceInfo p, string? dish) => new(p.Id, p.Name, p.Cuisine, p.Lat, p.Lng, dish);
}
