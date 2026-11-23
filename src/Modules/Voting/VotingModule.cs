using LunchSuggest.Modules.Voting.Internal;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LunchSuggest.Modules.Voting;

/// <param name="Dish">A specific dish at the place ("Phở bò tái"), or null to vote on the place itself.</param>
public sealed record NewCandidate(string PlaceId, string Name, string? Cuisine, double Lat, double Lng, string? Dish = null);

/// <param name="AddedBy">A voter's name, or one of <see cref="CandidateSource"/> for options the app added.</param>
/// <param name="PickedBy">Who pressed "Random pick" when this option won it; null if it isn't today's pick.</param>
public sealed record CandidateView(Guid Id, string PlaceId, string Name, string? Dish, string? Cuisine, double Lat, double Lng,
    string AddedBy, IReadOnlyList<string> VoterNames, bool VotedByMe, string? PickedBy)
{
    public string Title => Dish is null ? Name : $"{Dish} at {Name}";
}

/// <summary>One day of a lunch group: its poll (if someone created one), options, votes and result.</summary>
/// <param name="Day">The poll day being shown.</param>
/// <param name="HasPoll">A poll exists for <paramref name="Day"/> (someone pressed "Create new poll").</param>
/// <param name="Seeded">The app has put that poll's starting dishes in.</param>
/// <param name="ClosesAt">When <paramref name="Day"/>'s poll closes (UTC); null if there's no deadline.</param>
/// <param name="Winner">The day's lunch, once its poll has closed (random pick, else most votes, ties settled by the dice).</param>
/// <param name="NextPollDay">The day "Create new poll" makes a poll for right now: today before the deadline, the next poll day after.</param>
/// <param name="NextPollExists">That day already has its poll (only one poll a day).</param>
/// <param name="Address">The meeting point's address, for display.</param>
/// <param name="MaxOptions">Most options a poll can have (a group setting).</param>
/// <param name="Holiday"><paramref name="Day"/> is one of the group's public holidays (no poll).</param>
public sealed record SessionView(Guid Id, string Code, string Title, double Lat, double Lng, int RadiusMeters,
    DateOnly Day, DateOnly Today, bool HasPoll, string? CreatedBy, bool Seeded, IReadOnlyList<CandidateView> Candidates,
    TimeOnly? Deadline, DateTimeOffset? ClosesAt, bool Closed, CandidateView? Winner,
    DateOnly NextPollDay, bool NextPollExists, string? Address = null, int MaxOptions = GroupSettings.DefaultMaxOptions,
    string? ShareUrl = null, bool Holiday = false)
{
    public CandidateView? Picked => Candidates.FirstOrDefault(c => c.PickedBy is not null);
    /// <summary>Votes and changes are accepted: the poll exists and hasn't closed.</summary>
    public bool IsOpen => HasPoll && !Closed;
    /// <summary>The poll has its maximum number of options.</summary>
    public bool IsFull => Candidates.Count >= MaxOptions;
}

/// <summary>One day's poll in the history list.</summary>
/// <param name="Closed">Its deadline has passed. Winner and RunnerUp are only set then (same rules as the result screen).</param>
/// <param name="Voters">Everyone who voted in it, sorted.</param>
public sealed record PollSummary(DateOnly Day, string CreatedBy, bool Closed, int OptionCount, IReadOnlyList<string> Voters,
    CandidateView? Winner, CandidateView? RunnerUp);

/// <param name="Locked">The voter has voted, so the name can't change any more.</param>
/// <param name="Approved">Allowed to vote when sign-in is on (set on the Settings page).</param>
public sealed record VoterInfo(string Name, bool Locked, bool Approved = false);

/// <summary>Someone who signed in, as listed on the Settings page for approval.</summary>
public sealed record AccountVoter(string Id, string Name, string Email, bool Approved);

/// <summary>The lunch group. The "Lunch" settings section gives the first values; the Settings page edits them afterwards.</summary>
/// <param name="TimeZone">IANA id ("Asia/Ho_Chi_Minh") deciding when the day starts; server time if empty.</param>
/// <param name="Address">Shown on the page only.</param>
/// <param name="Deadline">Local time the daily poll closes (e.g. 11:30); null = never.</param>
/// <param name="SiteTitle">Name in the top bar and browser tab; null = "LunchSuggest".</param>
/// <param name="LandingIcon">Emoji on the landing page button; null = a different food icon every day.</param>
/// <param name="MaxOptions">Most options one poll can have, including the ones the app picks.</param>
/// <param name="PicksMin">Fewest dishes the app puts in a new poll (runner-ups + random picks).</param>
/// <param name="PicksMax">Most dishes the app puts in a new poll; 0 = new polls start empty.</param>
/// <param name="RunnerUpDays">How many past days' runner-ups can come back; 0 = never.</param>
/// <param name="ShareUrl">Address other computers use to open the site (e.g. http://10.3.0.54:8686), for the invite link;
/// null = whatever address the viewer's browser used.</param>
/// <param name="AutoApprove">With sign-in on, the host's list of regulars is allowed to vote on first sign-in; off = everyone waits.</param>
/// <param name="Holidays">Public holidays: no poll on these days, like weekends.</param>
public sealed record GroupSettings(string Code, string Title, double Lat, double Lng, int RadiusMeters, string? TimeZone,
    string? Address = null, TimeOnly? Deadline = null,
    string? SiteTitle = null, string? LandingIcon = null,
    int MaxOptions = GroupSettings.DefaultMaxOptions, int PicksMin = 3, int PicksMax = 5, int RunnerUpDays = 5,
    string? ShareUrl = null, bool AutoApprove = true, IReadOnlyList<DateOnly>? Holidays = null)
{
    public const int MaxHolidays = 100;
    public const int DefaultMaxOptions = 8;
    /// <summary>Upper limit for <see cref="PicksMax"/>: each new dish can take ~10 s to look up while people wait.</summary>
    public const int PicksLimit = 8;
}

/// <summary>Lunch polls run Monday to Friday, except the group's public holidays.</summary>
// ponytail: Mon-Fri is fixed; make it a group setting if a team ever lunches together on weekends.
public static class PollDays
{
    public static bool IsPollDay(DateOnly day, IReadOnlyCollection<DateOnly>? holidays = null) =>
        day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && holidays?.Contains(day) != true;

    /// <summary><paramref name="day"/> itself if it's a poll day, else the next one (Saturday and Sunday give Monday).</summary>
    public static DateOnly OnOrAfter(DateOnly day, IReadOnlyCollection<DateOnly>? holidays = null)
    {
        while (!IsPollDay(day, holidays)) day = day.AddDays(1);
        return day;
    }

    /// <summary>
    /// Reads a typed list of dates ("24/11/2026, 2027-01-01"), separated by commas, semicolons, spaces or new lines.
    /// Null if one of them isn't a date.
    /// </summary>
    public static List<DateOnly>? Parse(string? text)
    {
        var days = new List<DateOnly>();
        foreach (var part in (text ?? "").Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!DateOnly.TryParseExact(part, ["d/M/yyyy", "yyyy-M-d"], System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day))
                return null;
            days.Add(day);
        }
        return days;
    }
}

/// <summary>Why a poll change was refused. The UI shows these in the user's language.</summary>
public enum PollRule { Closed, NoPoll, OutsideWindow, OptionGone, TooManyOptions }

/// <summary>A poll change refused by a rule (not a bug). <see cref="Deadline"/> / <see cref="Limit"/> fill in the message.</summary>
public sealed class PollRuleException(PollRule rule, string message, TimeOnly? deadline = null, int limit = 0)
    : InvalidOperationException(message)
{
    public PollRule Rule { get; } = rule;
    public TimeOnly? Deadline { get; } = deadline;
    public int Limit { get; } = limit;
}

/// <summary>"Added by" values for options the app puts in the poll itself.</summary>
public static class CandidateSource
{
    public const string RunnerUp = "runner-up", Random = "random";
    /// <summary>"Picked by" value when the deadline had to settle a tie (or nobody voted).</summary>
    public const string Dice = "dice";
}

/// <summary>Public contract of the Voting module: a lunch group with at most one poll per day.</summary>
/// <remarks>Changes are only accepted on an open poll (created, before its deadline) for today or tomorrow.</remarks>
public interface ILunchSessions
{
    /// <summary>Raised with the group code whenever the group or one of its polls changes, so open pages can refresh.</summary>
    event Action<string>? Changed;

    /// <summary>Raised when someone signs in for the first time or is allowed / stopped (the Who can vote list refreshes).</summary>
    event Action? VotersChanged;

    /// <summary>
    /// Creates the group from the settings file if it doesn't exist yet. An existing group keeps its stored values
    /// (those are edited on the Settings page); only a missing address is filled in.
    /// </summary>
    Task EnsureGroupAsync(GroupSettings group, CancellationToken ct = default);
    /// <summary>The group's current settings; null if there's no such group.</summary>
    Task<GroupSettings?> GetGroupAsync(string code, CancellationToken ct = default);
    /// <summary>Saves new settings for the group <see cref="GroupSettings.Code"/>. Invalid values throw <see cref="ArgumentException"/>.</summary>
    Task UpdateGroupAsync(GroupSettings group, CancellationToken ct = default);
    /// <param name="day">Poll day to show; null = today.</param>
    Task<SessionView?> GetAsync(string code, DateOnly? day, string? voterId, CancellationToken ct = default);

    /// <summary>
    /// Creates the poll for <see cref="SessionView.NextPollDay"/>: today before the deadline, the next weekday after it.
    /// Only one poll a day: if it already exists, nothing changes. Returns the poll's day either way.
    /// </summary>
    Task<DateOnly> CreatePollAsync(string code, string createdBy, CancellationToken ct = default);

    Task AddCandidateAsync(string code, DateOnly day, NewCandidate place, string addedBy, CancellationToken ct = default);
    /// <summary>
    /// Adds or removes this voter's vote, under the name saved with <see cref="SaveVoterNameAsync"/> (required first).
    /// One vote per poll: voting for an option removes the voter's vote from any other. The first vote fixes the name.
    /// </summary>
    Task ToggleVoteAsync(string code, DateOnly day, Guid candidateId, string voterId, CancellationToken ct = default);
    /// <summary>
    /// Saves a voter's name. Names are unique: returns null if another voter has it. Once the voter has voted the name
    /// is fixed, and this returns the fixed name without changing it.
    /// </summary>
    Task<VoterInfo?> SaveVoterNameAsync(string voterId, string name, CancellationToken ct = default);
    /// <summary>
    /// <see cref="SaveVoterNameAsync"/> for a signed-in account, also keeping its e-mail for the Settings page.
    /// Null if another voter has the name. New accounts start unapproved, unless <paramref name="approveIfNew"/>
    /// (only on the account's first sign-in, so taking approval away on the Settings page sticks).
    /// </summary>
    Task<VoterInfo?> SaveAccountVoterAsync(string voterId, string name, string email, bool approveIfNew = false, CancellationToken ct = default);
    /// <summary>Whether the voter may vote while sign-in is on; false for unknown voters.</summary>
    Task<bool> IsVoterApprovedAsync(string voterId, CancellationToken ct = default);
    /// <summary>Everyone who has signed in, unapproved first, then by name.</summary>
    Task<IReadOnlyList<AccountVoter>> GetAccountVotersAsync(CancellationToken ct = default);
    /// <summary>Lets a signed-in voter vote, or stops them. Open pages of group <paramref name="code"/> refresh.</summary>
    Task SetVoterApprovedAsync(string code, string voterId, bool approved, CancellationToken ct = default);
    /// <summary>Picks one of the poll's options uniformly at random; null if there are none.</summary>
    Task<Guid?> PickRandomAsync(string code, DateOnly day, string pickedBy, CancellationToken ct = default);

    /// <summary>
    /// Deletes today's or tomorrow's poll with its options, votes and pick, so a new one can be created. Only while it's open:
    /// closed and older polls are history and can't be reset (<see cref="ArgumentException"/>). Logged. False if there was no poll.
    /// </summary>
    Task<bool> ResetPollAsync(string code, DateOnly day, CancellationToken ct = default);

    /// <summary>After the day's deadline: if no option clearly won (tie, or no votes), rolls the dice once and saves it. No-op otherwise.</summary>
    Task CloseDayAsync(string code, DateOnly day, CancellationToken ct = default);

    /// <summary>The group's polls, newest first (at most <paramref name="take"/>). Settles and records closed days first.</summary>
    Task<IReadOnlyList<PollSummary>> GetHistoryAsync(string code, int take = 60, CancellationToken ct = default);

    /// <summary>
    /// Records every closed day's winner and runner-up (stored once per day), then returns the runner-ups of the
    /// <paramref name="days"/> days before <paramref name="day"/>, newest first, minus anything the group has eaten since.
    /// </summary>
    Task<IReadOnlyList<NewCandidate>> GetRecentRunnerUpsAsync(string code, DateOnly day, int days = 5, CancellationToken ct = default);
    /// <summary>Puts a new poll's starting options in, once. False if it was already done (or there's no such poll).</summary>
    Task<bool> SeedPollAsync(string code, DateOnly day, IReadOnlyList<NewCandidate> runnerUps, IReadOnlyList<NewCandidate> randoms, CancellationToken ct = default);
}

public static class VotingModule
{
    public static IServiceCollection AddVotingModule(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContextFactory<VotingDbContext>(o => o.UseNpgsql(config.GetConnectionString("Lunch"),
            n => n.MigrationsHistoryTable("__EFMigrationsHistory", VotingDbContext.Schema)));
        services.AddSingleton<ILunchSessions, LunchSessions>();
        return services;
    }

    public static async Task MigrateVotingModuleAsync(this IServiceProvider sp)
    {
        await using var db = await sp.GetRequiredService<IDbContextFactory<VotingDbContext>>().CreateDbContextAsync();
        await db.Database.MigrateAsync();
    }
}
