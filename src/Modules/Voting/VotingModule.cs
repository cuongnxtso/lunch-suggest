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
/// <param name="NextPollDay">The day "Create new poll" makes a poll for right now: today before the deadline, tomorrow after.</param>
/// <param name="NextPollExists">That day already has its poll (only one poll a day).</param>
public sealed record SessionView(Guid Id, string Code, string Title, double Lat, double Lng, int RadiusMeters,
    DateOnly Day, DateOnly Today, bool HasPoll, string? CreatedBy, bool Seeded, IReadOnlyList<CandidateView> Candidates,
    TimeOnly? Deadline, DateTimeOffset? ClosesAt, bool Closed, CandidateView? Winner,
    DateOnly NextPollDay, bool NextPollExists)
{
    public CandidateView? Picked => Candidates.FirstOrDefault(c => c.PickedBy is not null);
    /// <summary>Votes and changes are accepted: the poll exists and hasn't closed.</summary>
    public bool IsOpen => HasPoll && !Closed;
}

/// <summary>The lunch group, from the "Lunch" settings section.</summary>
/// <param name="TimeZone">IANA id ("Asia/Ho_Chi_Minh") deciding when the day starts; server time if empty.</param>
/// <param name="Address">Shown on the page only.</param>
/// <param name="Deadline">Local time the daily poll closes (e.g. 11:30); null = never.</param>
public sealed record GroupSettings(string Code, string Title, double Lat, double Lng, int RadiusMeters, string? TimeZone,
    string? Address = null, TimeOnly? Deadline = null);

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
    /// <summary>Raised with the group code whenever one of its polls changes, so open pages can refresh.</summary>
    event Action<string>? Changed;

    /// <summary>Creates the group, or updates its name, place and time zone to match the settings.</summary>
    Task EnsureGroupAsync(GroupSettings group, CancellationToken ct = default);
    /// <param name="day">Poll day to show; null = today.</param>
    Task<SessionView?> GetAsync(string code, DateOnly? day, string? voterId, CancellationToken ct = default);

    /// <summary>
    /// Creates the poll for <see cref="SessionView.NextPollDay"/>: today before the deadline, tomorrow after it.
    /// Only one poll a day: if it already exists, nothing changes. Returns the poll's day either way.
    /// </summary>
    Task<DateOnly> CreatePollAsync(string code, string createdBy, CancellationToken ct = default);

    Task AddCandidateAsync(string code, DateOnly day, NewCandidate place, string addedBy, CancellationToken ct = default);
    Task ToggleVoteAsync(string code, DateOnly day, Guid candidateId, string voterId, string voterName, CancellationToken ct = default);
    /// <summary>Picks one of the poll's options uniformly at random; null if there are none.</summary>
    Task<Guid?> PickRandomAsync(string code, DateOnly day, string pickedBy, CancellationToken ct = default);

    /// <summary>After the day's deadline: if no option clearly won (tie, or no votes), rolls the dice once and saves it. No-op otherwise.</summary>
    Task CloseDayAsync(string code, DateOnly day, CancellationToken ct = default);

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
