using Microsoft.EntityFrameworkCore;

namespace LunchSuggest.Modules.Voting.Internal;

/// <summary>A lunch group: one link, a new poll every day.</summary>
internal sealed class LunchSession
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Title { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int RadiusMeters { get; set; }
    public string TimeZone { get; set; } = "";   // IANA id; "" = server time
    public TimeOnly? Deadline { get; set; }       // local time each day's poll closes; null = never
    public DateTimeOffset CreatedAt { get; set; }
    public List<Candidate> Candidates { get; set; } = [];
}

/// <summary>A day's poll. At most one per group per day (the key); options and votes hang off (SessionId, Day).</summary>
internal sealed class Poll
{
    public Guid SessionId { get; set; }
    public DateOnly Day { get; set; }
    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SeededAt { get; set; }   // when the app put the starting dishes in
}

/// <summary>An option in one day's poll.</summary>
internal sealed class Candidate
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public DateOnly Day { get; set; }
    public required string PlaceId { get; set; }  // id from the Places module; no FK across modules
    public required string Name { get; set; }
    public string? Dish { get; set; }             // null = the place itself
    public string? Cuisine { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public required string AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
    public string? PickedBy { get; set; }         // set on the day's random pick
    public List<Vote> Votes { get; set; } = [];
}

internal sealed class Vote
{
    public Guid CandidateId { get; set; }
    public required string VoterId { get; set; }
    public required string VoterName { get; set; }
}

/// <summary>How a finished day ended: what the group ate (1st) and what came 2nd. Recorded once the day is over.</summary>
internal sealed class DayResult
{
    public Guid SessionId { get; set; }
    public DateOnly Day { get; set; }
    public PollEntry? Winner { get; set; }
    public PollEntry? RunnerUp { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

/// <summary>A copy of an option, so results stay readable even if the day's options are cleaned up later.</summary>
internal sealed class PollEntry
{
    public required string PlaceId { get; set; }
    public required string Name { get; set; }
    public string? Dish { get; set; }
    public string? Cuisine { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public int Votes { get; set; }

    public static PollEntry From(Candidate c) => new()
    {
        PlaceId = c.PlaceId, Name = c.Name, Dish = c.Dish, Cuisine = c.Cuisine, Lat = c.Lat, Lng = c.Lng, Votes = c.Votes.Count,
    };

    public bool SameAs(PollEntry other) => PlaceId == other.PlaceId && Dish == other.Dish;
}

internal sealed class VotingDbContext(DbContextOptions<VotingDbContext> options) : DbContext(options)
{
    public const string Schema = "voting";
    public DbSet<LunchSession> Sessions => Set<LunchSession>();
    public DbSet<Candidate> Candidates => Set<Candidate>();
    public DbSet<Vote> Votes => Set<Vote>();
    public DbSet<DayResult> DayResults => Set<DayResult>();
    public DbSet<Poll> Polls => Set<Poll>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<LunchSession>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).HasMaxLength(16);
            e.Property(x => x.Title).HasMaxLength(LunchSessions.MaxTitle);
            e.Property(x => x.TimeZone).HasMaxLength(64).HasDefaultValue("");
            e.HasMany(x => x.Candidates).WithOne().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Poll>(e =>
        {
            e.HasKey(x => new { x.SessionId, x.Day }); // only one poll a day
            e.Property(x => x.CreatedBy).HasMaxLength(LunchSessions.MaxName);
            e.HasOne<LunchSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Candidate>(e =>
        {
            // Options only exist inside a poll.
            e.HasOne<Poll>().WithMany().HasForeignKey(x => new { x.SessionId, x.Day }).OnDelete(DeleteBehavior.Cascade);
            // NULLS NOT DISTINCT: the place-only option (Dish = null) is unique per day too.
            e.HasIndex(x => new { x.SessionId, x.Day, x.PlaceId, x.Dish }).IsUnique().AreNullsDistinct(false);
            e.Property(x => x.Dish).HasMaxLength(LunchSessions.MaxDish);
            e.Property(x => x.PickedBy).HasMaxLength(LunchSessions.MaxName);
            e.HasMany(x => x.Votes).WithOne().HasForeignKey(x => x.CandidateId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Vote>(e =>
        {
            e.HasKey(x => new { x.CandidateId, x.VoterId });
            e.Property(x => x.VoterName).HasMaxLength(LunchSessions.MaxName);
        });
        b.Entity<DayResult>(e =>
        {
            e.HasKey(x => new { x.SessionId, x.Day });
            e.HasOne<LunchSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            // Stored as JSON columns: a snapshot, not a link to the (per-day) option rows.
            e.OwnsOne(x => x.Winner, o => o.ToJson());
            e.OwnsOne(x => x.RunnerUp, o => o.ToJson());
        });
    }
}
