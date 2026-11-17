using Microsoft.EntityFrameworkCore;

namespace LunchSuggest.Modules.Voting.Internal;

internal sealed class LunchSessions(IDbContextFactory<VotingDbContext> dbFactory) : ILunchSessions
{
    public const int MaxTitle = 80, MaxName = 40, MaxCandidates = 30, MaxDish = 120;

    // ponytail: in-process event and lock = one server instance. Scale-out needs a backplane (Redis pub/sub / Postgres LISTEN).
    public event Action<string>? Changed;

    // Serializes the dice and result recording, so two pages opening right after 11:30 can't both roll.
    readonly SemaphoreSlim _closeLock = new(1, 1);

    /// <param name="ClosesAt">When <c>day</c>'s poll closes (UTC); null without a deadline.</param>
    internal readonly record struct Clock(DateOnly Today, DateTimeOffset? ClosesAt, bool Closed);

    /// <summary>
    /// The group's current day, and <paramref name="day"/>'s deadline (default today), in the group's own time zone
    /// (a team in Hanoi rolls over and closes at their 11:30, not the server's).
    /// </summary>
    internal static Clock ClockFor(string? timeZone, TimeOnly? deadline, DateTime utcNow, DateOnly? day = null)
    {
        var tz = !string.IsNullOrEmpty(timeZone) && TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out var z) ? z : TimeZoneInfo.Local;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz));
        if (deadline is not { } d) return new(today, null, false);
        var local = (day ?? today).ToDateTime(d);
        if (tz.IsInvalidTime(local)) local = local.AddHours(1); // deadline inside a DST gap
        var closesAt = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
        return new(today, closesAt, utcNow >= closesAt.UtcDateTime);
    }

    internal static DateOnly TodayIn(string? timeZone, DateTime utcNow) => ClockFor(timeZone, null, utcNow).Today;

    /// <summary>"Create new poll" makes today's poll until today's deadline, tomorrow's after it.</summary>
    internal static DateOnly NextPollDay(Clock today) => today.Closed ? today.Today.AddDays(1) : today.Today;

    static Clock Now(LunchSession s, DateOnly? day = null) => ClockFor(s.TimeZone, s.Deadline, DateTime.UtcNow, day);

    public async Task EnsureGroupAsync(GroupSettings group, CancellationToken ct = default)
    {
        var code = Required(group.Code, 16, "Lunch:Code");
        var title = Required(group.Title, MaxTitle, "Lunch:Title");
        if (group.Lat is < -90 or > 90 || group.Lng is < -180 or > 180) throw new ArgumentException("Lunch:Lat/Lng is out of range.");
        if (group.RadiusMeters is < 100 or > 5000) throw new ArgumentException("Lunch:RadiusMeters must be between 100 and 5000.");
        if (!string.IsNullOrEmpty(group.TimeZone) && !TimeZoneInfo.TryFindSystemTimeZoneById(group.TimeZone, out _))
            throw new ArgumentException($"Lunch:TimeZone '{group.TimeZone}' is not a known time zone (use an IANA id like Asia/Ho_Chi_Minh).");
        if (group.Deadline is { } d && d <= new TimeOnly(0, 30))
            throw new ArgumentException("Lunch:Deadline must leave some time to vote (e.g. 11:30).");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Code == code, ct);
        if (s is null)
        {
            s = new LunchSession { Id = Guid.CreateVersion7(), Code = code, Title = title, CreatedAt = DateTimeOffset.UtcNow };
            db.Sessions.Add(s);
        }
        // Settings win: changing them and restarting moves the group (polls and history stay).
        (s.Title, s.Lat, s.Lng, s.RadiusMeters, s.TimeZone, s.Deadline) = (title, group.Lat, group.Lng, group.RadiusMeters, group.TimeZone ?? "", group.Deadline);
        await db.SaveChangesAsync(ct);
    }

    public async Task<SessionView?> GetAsync(string code, DateOnly? day, string? voterId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, ct);
        if (s is null) return null;
        var now = Now(s);
        var shownDay = day ?? now.Today;
        var clock = Now(s, shownDay);
        var poll = await db.Polls.AsNoTracking().FirstOrDefaultAsync(p => p.SessionId == s.Id && p.Day == shownDay, ct);
        var options = await db.Candidates.AsNoTracking().Include(c => c.Votes)
            .Where(c => c.SessionId == s.Id && c.Day == shownDay).ToListAsync(ct);

        var views = options
            .OrderByDescending(c => c.Votes.Count).ThenBy(c => c.AddedAt)
            .Select(c => new CandidateView(c.Id, c.PlaceId, c.Name, c.Dish, c.Cuisine, c.Lat, c.Lng, c.AddedBy,
                c.Votes.Select(v => v.VoterName).ToList(), voterId is not null && c.Votes.Any(v => v.VoterId == voterId), c.PickedBy))
            .ToList();
        // The winner is only announced once the poll has closed.
        var winner = clock.Closed && Outcome(options).Winner is { } w ? views.First(v => v.Id == w.Id) : null;
        var next = NextPollDay(now);
        var nextExists = next == shownDay ? poll is not null : await db.Polls.AnyAsync(p => p.SessionId == s.Id && p.Day == next, ct);
        return new SessionView(s.Id, s.Code, s.Title, s.Lat, s.Lng, s.RadiusMeters,
            shownDay, now.Today, poll is not null, poll?.CreatedBy, poll?.SeededAt is not null, views,
            s.Deadline, clock.ClosesAt, clock.Closed, winner, next, nextExists);
    }

    public async Task<DateOnly> CreatePollAsync(string code, string createdBy, CancellationToken ct = default)
    {
        createdBy = Required(createdBy, MaxName, nameof(createdBy));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await FindAsync(db, code, ct);
        var day = NextPollDay(Now(s));
        if (await db.Polls.AnyAsync(p => p.SessionId == s.Id && p.Day == day, ct)) return day; // one poll a day
        db.Polls.Add(new Poll { SessionId = s.Id, Day = day, CreatedBy = createdBy, CreatedAt = DateTimeOffset.UtcNow });
        await SaveIgnoringDuplicateAsync(db, ct); // two people clicking at once: the key keeps it to one
        Changed?.Invoke(code);
        return day;
    }

    /// <summary>Server-side rule, not just hidden buttons: only an existing, still-open poll for today or tomorrow can change.</summary>
    async Task<LunchSession> OpenPollAsync(VotingDbContext db, string code, DateOnly day, CancellationToken ct)
    {
        var s = await FindAsync(db, code, ct);
        var clock = Now(s, day);
        if (day < clock.Today || day > clock.Today.AddDays(1))
            throw new PollRuleException(PollRule.OutsideWindow, "Only today's and tomorrow's polls can change.");
        if (clock.Closed) throw new PollRuleException(PollRule.Closed, $"This poll closed at {s.Deadline:HH\\:mm}.", s.Deadline);
        if (!await db.Polls.AnyAsync(p => p.SessionId == s.Id && p.Day == day, ct))
            throw new PollRuleException(PollRule.NoPoll, "There's no poll for that day yet. Create one first.");
        return s;
    }

    public async Task AddCandidateAsync(string code, DateOnly day, NewCandidate place, string addedBy, CancellationToken ct = default)
    {
        addedBy = Required(addedBy, MaxName, nameof(addedBy));
        var dish = Dish(place);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await OpenPollAsync(db, code, day, ct);
        var existing = await db.Candidates.Where(c => c.SessionId == s.Id && c.Day == day).Select(c => new { c.PlaceId, c.Dish }).ToListAsync(ct);
        if (existing.Any(c => c.PlaceId == place.PlaceId && c.Dish == dish)) return;
        if (existing.Count >= MaxCandidates)
            throw new PollRuleException(PollRule.TooManyOptions, $"A poll can have at most {MaxCandidates} options.", limit: MaxCandidates);

        db.Candidates.Add(NewRow(s, day, place, dish, addedBy));
        await SaveIgnoringDuplicateAsync(db, ct);
        Changed?.Invoke(code);
    }

    public async Task ToggleVoteAsync(string code, DateOnly day, Guid candidateId, string voterId, string voterName, CancellationToken ct = default)
    {
        voterName = Required(voterName, MaxName, nameof(voterName));
        voterId = Required(voterId, 64, nameof(voterId));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await OpenPollAsync(db, code, day, ct);
        // Scoped to this group and day, so a link to one poll can't touch another.
        if (!await db.Candidates.AnyAsync(c => c.Id == candidateId && c.SessionId == s.Id && c.Day == day, ct))
            throw new PollRuleException(PollRule.OptionGone, "That option isn't in this poll. Reload the page.");

        var deleted = await db.Votes.Where(v => v.CandidateId == candidateId && v.VoterId == voterId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            db.Votes.Add(new Vote { CandidateId = candidateId, VoterId = voterId, VoterName = voterName });
            await SaveIgnoringDuplicateAsync(db, ct);
        }
        Changed?.Invoke(code);
    }

    public async Task<Guid?> PickRandomAsync(string code, DateOnly day, string pickedBy, CancellationToken ct = default)
    {
        pickedBy = Required(pickedBy, MaxName, nameof(pickedBy));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await OpenPollAsync(db, code, day, ct);
        var options = await db.Candidates.Where(c => c.SessionId == s.Id && c.Day == day).ToListAsync(ct);
        if (options.Count == 0) return null;

        foreach (var c in options) c.PickedBy = null;
        var chosen = options[Random.Shared.Next(options.Count)];
        chosen.PickedBy = pickedBy;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(code);
        return chosen.Id;
    }

    public async Task CloseDayAsync(string code, DateOnly day, CancellationToken ct = default)
    {
        await _closeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await FindAsync(db, code, ct);
            if (!Now(s, day).Closed) return;
            if (await SettleWithDiceAsync(db, s.Id, day, ct)) Changed?.Invoke(code);
        }
        finally { _closeLock.Release(); }
    }

    /// <summary>Rolls the dice for a closed day that has no clear winner. Caller holds the close lock. True if it picked.</summary>
    static async Task<bool> SettleWithDiceAsync(VotingDbContext db, Guid sessionId, DateOnly day, CancellationToken ct)
    {
        var options = await db.Candidates.Include(c => c.Votes).Where(c => c.SessionId == sessionId && c.Day == day).ToListAsync(ct);
        var contenders = DeadlineContenders(options);
        if (contenders.Count == 0) return false;
        contenders[Random.Shared.Next(contenders.Count)].PickedBy = CandidateSource.Dice;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// At the deadline: options the dice must choose between. Empty when the day is already decided
    /// (someone used Random pick, or one option has the most votes). Tied top → the tied ones; nobody voted → all of them.
    /// </summary>
    internal static List<Candidate> DeadlineContenders(IReadOnlyCollection<Candidate> options)
    {
        if (options.Count == 0 || options.Any(c => c.PickedBy is not null)) return [];
        var top = options.Max(c => c.Votes.Count);
        var tied = options.Where(c => c.Votes.Count == top).ToList();
        return tied.Count > 1 || top == 0 ? tied : [];
    }

    public async Task<IReadOnlyList<NewCandidate>> GetRecentRunnerUpsAsync(string code, DateOnly day, int days = 5, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await FindAsync(db, code, ct);
        await RecordClosedDaysAsync(db, s, ct);

        var from = day.AddDays(-days);
        var results = await db.DayResults.AsNoTracking()
            .Where(r => r.SessionId == s.Id && r.Day >= from && r.Day < day).ToListAsync(ct);
        return PickRunnerUps(results).Select(e => new NewCandidate(e.PlaceId, e.Name, e.Cuisine, e.Lat, e.Lng, e.Dish)).ToList();
    }

    /// <summary>
    /// Stores the winner and runner-up of every closed poll without a result yet: past days, and today once its deadline passed
    /// (so tomorrow's poll, created this afternoon, already gets today's runner-up). A closed day can't change any more.
    /// </summary>
    async Task RecordClosedDaysAsync(VotingDbContext db, LunchSession s, CancellationToken ct)
    {
        var now = Now(s);
        var lastClosed = now.Closed ? now.Today : now.Today.AddDays(-1);
        await _closeLock.WaitAsync(ct);
        try
        {
            var recorded = db.DayResults.Where(r => r.SessionId == s.Id).Select(r => r.Day);
            var days = await db.Polls.Where(p => p.SessionId == s.Id && p.Day <= lastClosed && !recorded.Contains(p.Day))
                .Select(p => p.Day).ToListAsync(ct);
            foreach (var day in days)
            {
                await SettleWithDiceAsync(db, s.Id, day, ct); // same winner the result screen shows
                var options = await db.Candidates.AsNoTracking().Include(c => c.Votes).Where(c => c.SessionId == s.Id && c.Day == day).ToListAsync(ct);
                var (winner, runnerUp) = Outcome(options);
                db.DayResults.Add(new DayResult
                {
                    SessionId = s.Id, Day = day, RecordedAt = DateTimeOffset.UtcNow,
                    Winner = winner is null ? null : PollEntry.From(winner),
                    RunnerUp = runnerUp is null ? null : PollEntry.From(runnerUp),
                });
            }
            if (days.Count > 0) await SaveIgnoringDuplicateAsync(db, ct);
        }
        finally { _closeLock.Release(); }
    }

    /// <summary>Winner: the random pick, else the most-voted option. Runner-up: the most-voted other option (at least one vote).</summary>
    internal static (Candidate? Winner, Candidate? RunnerUp) Outcome(IReadOnlyCollection<Candidate> dayOptions)
    {
        static IEnumerable<Candidate> ByVotes(IEnumerable<Candidate> cs) =>
            cs.Where(c => c.Votes.Count > 0).OrderByDescending(c => c.Votes.Count).ThenBy(c => c.AddedAt);
        var winner = dayOptions.FirstOrDefault(c => c.PickedBy is not null) ?? ByVotes(dayOptions).FirstOrDefault();
        return (winner, ByVotes(dayOptions.Where(c => c != winner)).FirstOrDefault());
    }

    /// <summary>Runner-ups newest day first, skipping any the group ate that day or later, and repeats.</summary>
    internal static List<PollEntry> PickRunnerUps(IReadOnlyCollection<DayResult> results)
    {
        var picked = new List<PollEntry>();
        foreach (var r in results.OrderByDescending(r => r.Day))
        {
            if (r.RunnerUp is not { } runnerUp) continue;
            var eatenSince = results.Any(o => o.Day >= r.Day && o.Winner is { } w && w.SameAs(runnerUp));
            if (!eatenSince && !picked.Any(p => p.SameAs(runnerUp))) picked.Add(runnerUp);
        }
        return picked;
    }

    public async Task<bool> SeedPollAsync(string code, DateOnly day, IReadOnlyList<NewCandidate> runnerUps, IReadOnlyList<NewCandidate> randoms, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await FindAsync(db, code, ct);
        var poll = await db.Polls.FirstOrDefaultAsync(p => p.SessionId == s.Id && p.Day == day, ct);
        if (poll is null || poll.SeededAt is not null) return false;
        poll.SeededAt = DateTimeOffset.UtcNow;

        var taken = (await db.Candidates.Where(c => c.SessionId == s.Id && c.Day == day).Select(c => new { c.PlaceId, c.Dish }).ToListAsync(ct))
            .Select(c => (c.PlaceId, c.Dish)).ToHashSet();
        var seeds = runnerUps.Select(p => (Place: p, By: CandidateSource.RunnerUp)).Concat(randoms.Select(p => (Place: p, By: CandidateSource.Random)));
        foreach (var (place, by) in seeds)
        {
            var dish = Dish(place);
            if (taken.Add((place.PlaceId, dish)) && taken.Count <= MaxCandidates) db.Candidates.Add(NewRow(s, day, place, dish, by));
        }
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(code);
        return true;
    }

    static Candidate NewRow(LunchSession s, DateOnly day, NewCandidate place, string? dish, string addedBy) => new()
    {
        Id = Guid.CreateVersion7(), SessionId = s.Id, Day = day, PlaceId = place.PlaceId, Name = place.Name, Dish = dish,
        Cuisine = place.Cuisine, Lat = place.Lat, Lng = place.Lng, AddedBy = addedBy, AddedAt = DateTimeOffset.UtcNow,
    };

    static string? Dish(NewCandidate place) => place.Dish is null ? null : Required(place.Dish, MaxDish, "dish");

    static async Task<LunchSession> FindAsync(VotingDbContext db, string code, CancellationToken ct) =>
        await db.Sessions.FirstOrDefaultAsync(x => x.Code == code, ct) ?? throw new ArgumentException("Lunch group not found.");

    static string Required(string? value, int max, string name)
    {
        value = value?.Trim();
        if (string.IsNullOrEmpty(value)) throw new ArgumentException($"{name} is required.");
        if (value.Length > max) throw new ArgumentException($"{name} must be at most {max} characters.");
        return value;
    }

    // Two people clicking at the same moment hit the unique index; the result they wanted already exists.
    static async Task SaveIgnoringDuplicateAsync(VotingDbContext db, CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" }) { }
    }
}
