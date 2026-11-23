using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LunchSuggest.Modules.Voting.Internal;

internal sealed class LunchSessions(IDbContextFactory<VotingDbContext> dbFactory, ILogger<LunchSessions> log) : ILunchSessions
{
    public const int MaxTitle = 80, MaxName = 40, MaxEmail = 200, MaxDish = 120, MaxAddress = 200, MaxSiteTitle = 40, MaxIcon = 16, MaxOptionsLimit = 20;

    // ponytail: in-process event and lock = one server instance. Scale-out needs a backplane (Redis pub/sub / Postgres LISTEN).
    public event Action<string>? Changed;
    public event Action? VotersChanged;

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

    /// <summary>
    /// "Create new poll" makes today's poll until today's deadline, the next weekday's after it
    /// (Friday afternoon and the weekend give Monday).
    /// </summary>
    internal static DateOnly NextPollDay(Clock today, IReadOnlyCollection<DateOnly>? holidays = null) =>
        PollDays.OnOrAfter(today.Closed ? today.Today.AddDays(1) : today.Today, holidays);

    /// <summary>Polls that can still change: today's and the next poll day's (Monday's from Friday on, skipping holidays).</summary>
    internal static bool CanChange(DateOnly day, DateOnly today, IReadOnlyCollection<DateOnly>? holidays = null) =>
        day >= today && day <= PollDays.OnOrAfter(today.AddDays(1), holidays);

    static Clock Now(LunchSession s, DateOnly? day = null) => ClockFor(s.TimeZone, s.Deadline, DateTime.UtcNow, day);

    /// <summary>Checks and tidies group settings, from the settings file or the Settings page (both are trust boundaries).</summary>
    internal static GroupSettings Validated(GroupSettings g)
    {
        var code = Required(g.Code, 16, "Code");
        var title = Required(g.Title, MaxTitle, "Title");
        var address = string.IsNullOrWhiteSpace(g.Address) ? null : g.Address.Trim();
        if (address?.Length > MaxAddress) throw new ArgumentException($"Address must be at most {MaxAddress} characters.");
        if (!double.IsFinite(g.Lat) || !double.IsFinite(g.Lng) || g.Lat is < -90 or > 90 || g.Lng is < -180 or > 180)
            throw new ArgumentException("Latitude must be between -90 and 90, longitude between -180 and 180.");
        if (g.RadiusMeters is < 100 or > 5000) throw new ArgumentException("Radius must be between 100 and 5000 m.");
        var tz = string.IsNullOrWhiteSpace(g.TimeZone) ? null : g.TimeZone.Trim();
        if (tz is not null && (tz.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(tz, out _)))
            throw new ArgumentException($"'{tz}' is not a known time zone (use an IANA id like Asia/Ho_Chi_Minh).");
        if (g.Deadline is { } d && d <= new TimeOnly(0, 30))
            throw new ArgumentException("The deadline must leave some time to vote (e.g. 11:30).");
        var siteTitle = string.IsNullOrWhiteSpace(g.SiteTitle) ? null : g.SiteTitle.Trim();
        if (siteTitle?.Length > MaxSiteTitle) throw new ArgumentException($"The site title must be at most {MaxSiteTitle} characters.");
        var icon = string.IsNullOrWhiteSpace(g.LandingIcon) ? null : g.LandingIcon.Trim();
        // An emoji can be several UTF-16 units (skin tones, ZWJ sequences); this still keeps it to "an icon".
        if (icon?.Length > MaxIcon) throw new ArgumentException($"The landing icon must be at most {MaxIcon} characters (one emoji).");
        if (g.MaxOptions is < 2 or > MaxOptionsLimit) throw new ArgumentException($"Max options per poll must be between 2 and {MaxOptionsLimit}.");
        if (g.PicksMax < 0 || g.PicksMax > Math.Min(GroupSettings.PicksLimit, g.MaxOptions))
            throw new ArgumentException($"The most dishes picked for a new poll must be between 0 and {Math.Min(GroupSettings.PicksLimit, g.MaxOptions)} (and not above the max options).");
        if (g.PicksMin < 0 || g.PicksMin > g.PicksMax)
            throw new ArgumentException("The fewest dishes picked for a new poll must be between 0 and the most.");
        if (g.RunnerUpDays is < 0 or > 30) throw new ArgumentException("Runner-up days must be between 0 and 30.");
        var holidays = (g.Holidays ?? []).Distinct().Order().ToList();
        if (holidays.Count > GroupSettings.MaxHolidays) throw new ArgumentException($"At most {GroupSettings.MaxHolidays} holidays.");
        return g with
        {
            Holidays = holidays,
            Code = code, Title = title, Address = address, TimeZone = tz, SiteTitle = siteTitle, LandingIcon = icon,
            ShareUrl = ShareUrlOrNull(g.ShareUrl),
        };
    }

    /// <summary>Just "http(s)://host[:port]", so the page path can be appended; it ends up in links people click.</summary>
    internal static string? ShareUrlOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/")
            throw new ArgumentException("The share link address must look like http://10.3.0.54:8686 (no path).");
        return uri.GetLeftPart(UriPartial.Authority);
    }

    public async Task EnsureGroupAsync(GroupSettings group, CancellationToken ct = default)
    {
        group = Validated(group);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Sessions.FirstOrDefaultAsync(x => x.Code == group.Code, ct);
        if (s is null)
        {
            s = new LunchSession { Id = Guid.CreateVersion7(), Code = group.Code, Title = group.Title, CreatedAt = DateTimeOffset.UtcNow };
            db.Sessions.Add(s);
            Apply(s, group);
        }
        // The stored group wins once it exists: the Settings page edits it, and a restart must not undo that.
        else (s.Address, s.ShareUrl) = (s.Address ?? group.Address, s.ShareUrl ?? group.ShareUrl); // fill only what's missing
        await db.SaveChangesAsync(ct);
    }

    public async Task<GroupSettings?> GetGroupAsync(string code, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Code == code, ct);
        return s is null ? null : new GroupSettings(s.Code, s.Title, s.Lat, s.Lng, s.RadiusMeters,
            s.TimeZone is { Length: > 0 } tz ? tz : null, s.Address, s.Deadline,
            s.SiteTitle, s.LandingIcon, s.MaxOptions, s.PicksMin, s.PicksMax, s.RunnerUpDays, s.ShareUrl, s.AutoApprove, s.Holidays);
    }

    public async Task UpdateGroupAsync(GroupSettings group, CancellationToken ct = default)
    {
        group = Validated(group);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await FindAsync(db, group.Code, ct);
        Apply(s, group);
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(group.Code);
    }

    static void Apply(LunchSession s, GroupSettings g)
    {
        (s.Title, s.Address, s.Lat, s.Lng, s.RadiusMeters, s.TimeZone, s.Deadline) =
            (g.Title, g.Address, g.Lat, g.Lng, g.RadiusMeters, g.TimeZone ?? "", g.Deadline);
        (s.SiteTitle, s.LandingIcon, s.MaxOptions, s.PicksMin, s.PicksMax, s.RunnerUpDays, s.ShareUrl, s.AutoApprove) =
            (g.SiteTitle, g.LandingIcon, g.MaxOptions, g.PicksMin, g.PicksMax, g.RunnerUpDays, g.ShareUrl, g.AutoApprove);
        s.Holidays = [.. g.Holidays ?? []];
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
            // Fixed order: sorting by votes made options jump under people's fingers as votes came in.
            .OrderBy(c => c.AddedAt)
            .Select(c => View(c, voterId))
            .ToList();
        // The winner is only announced once the poll has closed.
        var winner = clock.Closed && Outcome(options).Winner is { } w ? views.First(v => v.Id == w.Id) : null;
        var next = NextPollDay(now, s.Holidays);
        var nextExists = next == shownDay ? poll is not null : await db.Polls.AnyAsync(p => p.SessionId == s.Id && p.Day == next, ct);
        return new SessionView(s.Id, s.Code, s.Title, s.Lat, s.Lng, s.RadiusMeters,
            shownDay, now.Today, poll is not null, poll?.CreatedBy, poll?.SeededAt is not null, views,
            s.Deadline, clock.ClosesAt, clock.Closed, winner, next, nextExists, s.Address, s.MaxOptions, s.ShareUrl,
            s.Holidays.Contains(shownDay));
    }

    static CandidateView View(Candidate c, string? voterId = null) =>
        new(c.Id, c.PlaceId, c.Name, c.Dish, c.Cuisine, c.Lat, c.Lng, c.AddedBy,
            c.Votes.Select(v => v.VoterName).ToList(), voterId is not null && c.Votes.Any(v => v.VoterId == voterId), c.PickedBy);

    public async Task<IReadOnlyList<PollSummary>> GetHistoryAsync(string code, int take = 60, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await FindAsync(db, code, ct);
        await RecordClosedDaysAsync(db, s, ct); // rolls the dice for unsettled past ties, so history matches the result screen
        var polls = await db.Polls.AsNoTracking().Where(p => p.SessionId == s.Id)
            .OrderByDescending(p => p.Day).Take(Math.Clamp(take, 1, 365)).ToListAsync(ct);
        var days = polls.Select(p => p.Day).ToList();
        var options = (await db.Candidates.AsNoTracking().Include(c => c.Votes)
            .Where(c => c.SessionId == s.Id && days.Contains(c.Day)).ToListAsync(ct)).ToLookup(c => c.Day);

        return polls.Select(p =>
        {
            var dayOptions = options[p.Day].ToList();
            var closed = Now(s, p.Day).Closed;
            var (winner, runnerUp) = closed ? Outcome(dayOptions) : default;
            return new PollSummary(p.Day, p.CreatedBy, closed, dayOptions.Count,
                dayOptions.SelectMany(c => c.Votes).Select(v => v.VoterName).Distinct().Order().ToList(),
                winner is null ? null : View(winner), runnerUp is null ? null : View(runnerUp));
        }).ToList();
    }

    public async Task<DateOnly> CreatePollAsync(string code, string createdBy, CancellationToken ct = default)
    {
        createdBy = Required(createdBy, MaxName, nameof(createdBy));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await FindAsync(db, code, ct);
        var day = NextPollDay(Now(s), s.Holidays);
        if (await db.Polls.AnyAsync(p => p.SessionId == s.Id && p.Day == day, ct)) return day; // one poll a day
        db.Polls.Add(new Poll { SessionId = s.Id, Day = day, CreatedBy = createdBy, CreatedAt = DateTimeOffset.UtcNow });
        await SaveIgnoringDuplicateAsync(db, ct); // two people clicking at once: the key keeps it to one
        Changed?.Invoke(code);
        return day;
    }

    /// <summary>Server-side rule, not just hidden buttons: only an existing, still-open poll for today or the next weekday can change.</summary>
    async Task<LunchSession> OpenPollAsync(VotingDbContext db, string code, DateOnly day, CancellationToken ct)
    {
        var s = await FindAsync(db, code, ct);
        var clock = Now(s, day);
        if (!CanChange(day, clock.Today, s.Holidays))
            throw new PollRuleException(PollRule.OutsideWindow, "Only today's poll and the next weekday's can change.");
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
        if (existing.Count >= s.MaxOptions)
            throw new PollRuleException(PollRule.TooManyOptions, $"A poll can have at most {s.MaxOptions} options.", limit: s.MaxOptions);

        db.Candidates.Add(NewRow(s, day, place, dish, addedBy));
        await SaveIgnoringDuplicateAsync(db, ct);
        Changed?.Invoke(code);
    }

    public async Task ToggleVoteAsync(string code, DateOnly day, Guid candidateId, string voterId, CancellationToken ct = default)
    {
        voterId = Required(voterId, 64, nameof(voterId));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await OpenPollAsync(db, code, day, ct);
        // Scoped to this group and day, so a link to one poll can't touch another.
        if (!await db.Candidates.AnyAsync(c => c.Id == candidateId && c.SessionId == s.Id && c.Day == day, ct))
            throw new PollRuleException(PollRule.OptionGone, "That option isn't in this poll. Reload the page.");

        // Locks the voter's row, so two clicks at once (two tabs) can't leave two votes standing.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var voter = (await db.Voters.FromSql($"""SELECT * FROM voting."Voters" WHERE "Id" = {voterId} FOR UPDATE""").ToListAsync(ct))
            .FirstOrDefault();
        var deleted = await db.Votes.Where(v => v.CandidateId == candidateId && v.VoterId == voterId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            if (voter is null) throw new InvalidOperationException("Save the voter's name before voting.");
            // One vote per poll: voting for an option takes the vote away from any other.
            await db.Votes.Where(v => v.VoterId == voterId
                    && db.Candidates.Any(c => c.Id == v.CandidateId && c.SessionId == s.Id && c.Day == day))
                .ExecuteDeleteAsync(ct);
            voter.Locked = true; // the first vote fixes the name
            db.Votes.Add(new Vote { CandidateId = candidateId, VoterId = voterId, VoterName = voter.Name });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        Changed?.Invoke(code);
    }

    public async Task<VoterInfo?> SaveVoterNameAsync(string voterId, string name, CancellationToken ct = default)
    {
        voterId = Required(voterId, 64, nameof(voterId));
        name = Required(name, MaxName, nameof(name));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var voter = await db.Voters.FindAsync([voterId], ct);
        if (voter is { Locked: true }) return new(voter.Name, true);

        // Voted before names were saved here: those votes fix the name.
        var locked = false;
        if (voter is null && await VotedNameAsync(db, voterId, ct) is { } voted) (name, locked) = (voted, true);
        if (voter?.Name == name) return new(name, false);
        if (!locked && await NameTakenAsync(db, voterId, name, ct)) return null;

        if (voter is null) db.Voters.Add(new Voter { Id = voterId, Name = name, Locked = locked });
        else voter.Name = name;
        try { await db.SaveChangesAsync(ct); }
        // Someone saved the same name at the same moment; the unique index let only one through.
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            return locked ? new(name, true) : null;
        }
        return new(name, locked);
    }

    public async Task<VoterInfo?> SaveAccountVoterAsync(string voterId, string name, string email, bool approveIfNew = false, CancellationToken ct = default)
    {
        email = Required(email, MaxEmail, nameof(email));
        if (await SaveVoterNameAsync(voterId, name, ct) is not { } saved) return null; // name taken
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Voters.FindAsync([voterId], ct) is not { } voter) return saved; // lost a same-moment race; next visit fixes it
        if (voter.Email is null && approveIfNew) voter.Approved = true; // first sign-in of a listed name
        if (voter.Email != email)
        {
            voter.Email = email;
            await db.SaveChangesAsync(ct);
            VotersChanged?.Invoke(); // a new sign-in to review
        }
        return saved with { Approved = voter.Approved };
    }

    public async Task<bool> IsVoterApprovedAsync(string voterId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Voters.AnyAsync(v => v.Id == voterId && v.Approved, ct);
    }

    public async Task<IReadOnlyList<AccountVoter>> GetAccountVotersAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Voters.Where(v => v.Email != null).OrderBy(v => v.Approved).ThenBy(v => v.Name)
            .Select(v => new AccountVoter(v.Id, v.Name, v.Email!, v.Approved)).ToListAsync(ct);
    }

    public async Task SetVoterApprovedAsync(string code, string voterId, bool approved, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.Voters.FindAsync([voterId], ct) is not { Email: not null } voter)
            throw new ArgumentException("No signed-in voter with that id.");
        if (voter.Approved == approved) return;
        voter.Approved = approved;
        await db.SaveChangesAsync(ct);
        Changed?.Invoke(code);
        VotersChanged?.Invoke();
    }

    // Also checks past votes, whose voters may not have saved a name here yet.
    // ponytail: no index on Votes.VoterName / VoterId; fine for one group's votes, add one if it grows large.
    static async Task<bool> NameTakenAsync(VotingDbContext db, string voterId, string name, CancellationToken ct) =>
        await db.Voters.AnyAsync(v => v.Name == name && v.Id != voterId, ct)
        || await db.Votes.AnyAsync(v => v.VoterName == name && v.VoterId != voterId, ct);

    // Newest day first, in case a voter has older votes under different names.
    static Task<string?> VotedNameAsync(VotingDbContext db, string voterId, CancellationToken ct) =>
        (from c in db.Candidates from v in c.Votes where v.VoterId == voterId orderby c.Day descending select v.VoterName)
            .FirstOrDefaultAsync(ct);

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

    /// <summary>Only today's and tomorrow's polls can be reset; older days are the history runner-ups come from.</summary>
    // Closed polls are history (the result, runner-ups), so only an open poll of today or tomorrow can go.
    internal static bool CanReset(DateOnly day, DateOnly today, bool closed, IReadOnlyCollection<DateOnly>? holidays = null) =>
        !closed && CanChange(day, today, holidays);

    public async Task<bool> ResetPollAsync(string code, DateOnly day, CancellationToken ct = default)
    {
        // Under the close lock, so a reset can't interleave with the dice or result recording for the same day.
        await _closeLock.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await FindAsync(db, code, ct);
            var clock = Now(s, day);
            if (!CanReset(day, clock.Today, clock.Closed, s.Holidays))
                throw new ArgumentException("Only an open poll for today or the next weekday can be reset; closed polls stay in history.");
            var options = await db.Candidates.CountAsync(c => c.SessionId == s.Id && c.Day == day, ct);
            var votes = await db.Candidates.Where(c => c.SessionId == s.Id && c.Day == day).SelectMany(c => c.Votes).CountAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.DayResults.Where(r => r.SessionId == s.Id && r.Day == day).ExecuteDeleteAsync(ct);
            // Options and their votes go with the poll (cascading foreign keys).
            var deleted = await db.Polls.Where(p => p.SessionId == s.Id && p.Day == day).ExecuteDeleteAsync(ct);
            await tx.CommitAsync(ct);
            if (deleted > 0)
            {
                // Warning, so it stands out: a reset deletes votes for good.
                log.LogWarning("Poll for {Day} in group {Code} was reset: {Options} options and {Votes} votes deleted", day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), code, options, votes);
                Changed?.Invoke(code);
            }
            return deleted > 0;
        }
        finally { _closeLock.Release(); }
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
            if (taken.Add((place.PlaceId, dish)) && taken.Count <= s.MaxOptions) db.Candidates.Add(NewRow(s, day, place, dish, by));
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
