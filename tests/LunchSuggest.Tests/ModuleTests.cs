using System.Text.Json;
using LunchSuggest.Modules.Places;
using LunchSuggest.Modules.Places.Internal;
using LunchSuggest.Modules.Voting.Internal;

namespace LunchSuggest.Tests;

public class PlacesTests
{
    [Fact]
    public void Overpass_parses_nodes_and_ways_and_skips_unnamed()
    {
        using var doc = JsonDocument.Parse("""
        { "elements": [
          { "type": "node", "id": 1, "lat": 21.03, "lon": 105.85,
            "tags": { "name": "Phở Gà", "amenity": "restaurant", "cuisine": "vietnamese;noodle", "addr:street": "Hàng Bông", "addr:housenumber": "5" } },
          { "type": "way", "id": 2, "center": { "lat": 21.04, "lon": 105.86 },
            "tags": { "name": "Bean Café", "amenity": "cafe" } },
          { "type": "node", "id": 3, "lat": 21.0, "lon": 105.0, "tags": { "amenity": "fast_food" } }
        ] }
        """);

        var places = OverpassPlaceProvider.Parse(doc.RootElement);

        Assert.Equal(2, places.Count);
        Assert.Equal("osm:node/1", places[0].Id);
        Assert.Equal("vietnamese, noodle", places[0].Cuisine);
        Assert.Equal("5 Hàng Bông", places[0].Address);
        Assert.Equal("osm:way/2", places[1].Id);
        Assert.Equal(21.04, places[1].Lat);
        Assert.Null(places[1].Cuisine);
    }

    [Fact]
    public void Google_parses_places_and_derives_cuisine()
    {
        using var doc = JsonDocument.Parse("""
        { "places": [
          { "id": "abc", "displayName": { "text": "Sushi Bar" }, "primaryType": "japanese_restaurant",
            "formattedAddress": "1 Main St", "location": { "latitude": 1.5, "longitude": 2.5 } },
          { "id": "def", "displayName": { "text": "Quick Bite" }, "primaryType": "fast_food_restaurant",
            "location": { "latitude": 1, "longitude": 2 } }
        ] }
        """);

        var places = GooglePlacesProvider.Parse(doc.RootElement);

        Assert.Equal("google:abc", places[0].Id);
        Assert.Equal("japanese", places[0].Cuisine);
        Assert.Null(places[1].Cuisine);
        Assert.Empty(GooglePlacesProvider.Parse(JsonDocument.Parse("{}").RootElement));
    }

    [Theory]
    [InlineData("vietnamese_restaurant", true, "vietnamese")]
    [InlineData("pho_restaurant", true, "pho")]
    [InlineData("coffee_shop", true, null)]
    [InlineData("fast_food_restaurant", true, null)]
    [InlineData("bubble_tea", true, "bubble tea")]
    [InlineData("restaurant_equipment_supplier", false, null)]
    [InlineData("food_delivery_service", false, null)]
    [InlineData("hair_salon", false, null)]
    [InlineData("mobile_phone_store", false, null)]
    [InlineData("internet_cafe", false, null)]
    [InlineData("health_food_store", false, null)]
    [InlineData("food_beverage_distributor", false, null)]
    [InlineData("pho_shop", true, "pho shop")]
    public void Overture_categories_map_to_food_and_cuisine(string category, bool isFood, string? cuisine)
    {
        Assert.Equal(isFood, OverturePlaceProvider.IsFood(category));
        if (isFood) Assert.Equal(cuisine, OverturePlaceProvider.CuisineOf(category));
    }

    [Theory]
    [InlineData("Phở Cội", "pho noodle soup")]
    [InlineData("Bún Chả Ô Vuông", "grilled pork with rice noodles")]     // phrase wins over "bún"
    [InlineData("BÁNh MỲ TƯƠI Nga", "banh mi sandwich")]                 // case-insensitive; not "noodles"
    [InlineData("Lẩu dê khô Thu Béo", "hot pot, goat")]
    [InlineData("Cafe 12 phố Huế", null)]                                // phố (street) is not phở
    [InlineData("Twins Coffee", null)]
    public void Dish_glossary_explains_vietnamese_names(string name, string? expected) =>
        Assert.Equal(expected, DishGlossary.Describe(name));

    [Fact]
    public void Search_text_includes_dish_gloss()
    {
        var p = new PlaceInfo("x", "Phở Xanh", null, "restaurant", null, 0, 0, 0);
        Assert.Equal("Phở Xanh. restaurant. Serves: pho noodle soup", PlaceDirectory.DescribeForSearch(p));
    }

    [Fact]
    public void Distance_is_haversine()
    {
        Assert.Equal(0, Geo.DistanceMeters(21, 105, 21, 105));
        Assert.InRange(Geo.DistanceMeters(0, 0, 1, 0), 111_100, 111_300); // one degree of latitude
    }

    [Fact]
    public void Search_text_skips_missing_parts()
    {
        var p = new PlaceInfo("x", "Twins Coffee", null, "cafe", null, 0, 0, 0);
        Assert.Equal("Twins Coffee. cafe", PlaceDirectory.DescribeForSearch(p));
    }
}

public class DishesTests
{
    [Fact]
    public void Llm_output_is_trimmed_deduplicated_and_capped()
    {
        var longName = new string('x', 200);
        var dishes = LunchSuggest.Modules.Dishes.Internal.DishExtractor.Parse($$"""
            {"dishes": [" Phở bò tái ", "phở  bò tái", "", 42, "{{longName}}",
                        "Phở gà", "Quẩy", "Trứng chần", "Nước dùng", "Bò viên"]}
            """, "Phở Cội");
        Assert.Equal(["Phở bò tái", "Phở gà", "Quẩy", "Trứng chần", "Nước dùng"], dishes);
    }

    [Fact]
    public void Junk_the_small_model_returns_is_dropped()
    {
        // Real outputs from qwen2.5:3b on Hanoi places.
        var dishes = LunchSuggest.Modules.Dishes.Internal.DishExtractor.Parse("""
            {"dishes": ["Cafe Bảo Long", "Món Tráng Miệng", "Mát mát làm bát bún các a zai ơi nhé", "Bạc xỉu"]}
            """, "Bảo Long Cafe");
        Assert.Equal(["Bạc xỉu"], dishes);
    }

    [Fact]
    public void Dish_named_in_the_place_name_is_kept() =>
        Assert.Equal(["Chả cá"], LunchSuggest.Modules.Dishes.Internal.DishExtractor.Parse(
            """{"dishes": ["Chả cá", "Nhà hàng chả cá sông Lô"]}""", "Nhà Hàng chả cá sông Lô"));

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"dishes\": \"Phở\"}")]
    [InlineData("{}")]
    public void Bad_llm_output_gives_no_dishes(string output) =>
        Assert.Empty(LunchSuggest.Modules.Dishes.Internal.DishExtractor.Parse(output, "Phở Cội"));

    [Fact]
    public void Searxng_results_are_parsed()
    {
        using var doc = JsonDocument.Parse("""
            { "results": [
              { "title": "Phở Cội - Foody", "url": "https://foody.vn/x", "content": "Phở bò tái lăn ngon" },
              { "title": "", "url": "https://empty", "content": "" },
              { "title": "Second", "url": "https://b", "content": null } ] }
            """);
        var results = LunchSuggest.Modules.Dishes.Internal.WebSearch.Parse(doc.RootElement, 5);
        Assert.Equal(2, results.Count);
        Assert.Equal("Phở bò tái lăn ngon", results[0].Snippet);
        Assert.Equal("", results[1].Snippet);
    }
}

public class VotingTests
{
    static readonly DateOnly Mon = new(2026, 11, 16), Tue = new(2026, 11, 17);

    static Candidate Option(DateOnly day, string dish, int votes, string? pickedBy = null) => new()
    {
        Day = day, PlaceId = "p-" + dish, Name = "Place " + dish, Dish = dish, AddedBy = "x", PickedBy = pickedBy,
        AddedAt = day.ToDateTime(TimeOnly.MinValue),
        Votes = Enumerable.Range(0, votes).Select(i => new Vote { VoterId = $"v{i}", VoterName = $"V{i}" }).ToList(),
    };

    static string?[] Dishes(IEnumerable<PollEntry> cs) => cs.Select(c => c.Dish).ToArray();

    /// <summary>Same path as the app: each finished day's outcome is stored as a DayResult, then runner-ups are picked from those.</summary>
    static List<PollEntry> RunnerUps(IEnumerable<Candidate> past) => LunchSessions.PickRunnerUps(past
        .GroupBy(c => c.Day)
        .Select(g => LunchSessions.Outcome(g.ToList()) is var (winner, runnerUp)
            ? new DayResult { Day = g.Key, Winner = winner is null ? null : PollEntry.From(winner), RunnerUp = runnerUp is null ? null : PollEntry.From(runnerUp) }
            : null!)
        .ToList());

    [Fact]
    public void Day_result_stores_winner_and_runner_up_with_votes()
    {
        var (winner, runnerUp) = LunchSessions.Outcome([Option(Mon, "A", 3), Option(Mon, "B", 2), Option(Mon, "C", 0)]);
        Assert.Equal(("A", 3), (PollEntry.From(winner!).Dish, PollEntry.From(winner!).Votes));
        Assert.Equal(("B", 2), (PollEntry.From(runnerUp!).Dish, PollEntry.From(runnerUp!).Votes));
    }

    [Fact]
    public void Runner_up_is_second_most_voted() =>
        Assert.Equal(["B"], Dishes(RunnerUps([Option(Mon, "A", 3), Option(Mon, "B", 2), Option(Mon, "C", 0)])));

    [Fact]
    public void Random_pick_wins_over_votes() =>
        // C was picked, so A (most votes) becomes the runner-up.
        Assert.Equal(["A"], Dishes(RunnerUps([Option(Mon, "A", 3), Option(Mon, "B", 1), Option(Mon, "C", 0, pickedBy: "Lan")])));

    [Fact]
    public void Runner_up_eaten_since_is_skipped_and_newest_day_comes_first() =>
        Assert.Equal(["D"], Dishes(RunnerUps([
            Option(Mon, "A", 3), Option(Mon, "B", 2),     // Monday's runner-up B…
            Option(Tue, "B", 4), Option(Tue, "D", 1),     // …won on Tuesday, so it's not suggested again
        ])));

    [Fact]
    public void Same_runner_up_twice_appears_once_and_no_votes_means_none()
    {
        Assert.Equal(["B"], Dishes(RunnerUps([
            Option(Mon, "A", 3), Option(Mon, "B", 2), Option(Tue, "C", 3), Option(Tue, "B", 1)])));
        Assert.Empty(RunnerUps([Option(Mon, "A", 0), Option(Mon, "B", 0)]));
    }

    [Fact]
    public void Day_rolls_over_at_the_groups_midnight()
    {
        var utc = new DateTime(2026, 11, 17, 18, 0, 0, DateTimeKind.Utc); // 01:00 on the 18th in Hanoi
        Assert.Equal(new DateOnly(2026, 11, 18), LunchSessions.TodayIn("Asia/Ho_Chi_Minh", utc));
        Assert.Equal(new DateOnly(2026, 11, 17), LunchSessions.TodayIn("Europe/London", utc));
        LunchSessions.TodayIn("Not/AZone", utc); // unknown zone falls back to server time instead of throwing
    }

    [Fact]
    public void Poll_closes_at_the_deadline_in_the_groups_time_zone()
    {
        var deadline = new TimeOnly(11, 30);
        var before = LunchSessions.ClockFor("Asia/Ho_Chi_Minh", deadline, new DateTime(2026, 11, 17, 4, 29, 0, DateTimeKind.Utc)); // 11:29 Hanoi
        var after = LunchSessions.ClockFor("Asia/Ho_Chi_Minh", deadline, new DateTime(2026, 11, 17, 4, 30, 0, DateTimeKind.Utc));  // 11:30 Hanoi
        Assert.False(before.Closed);
        Assert.True(after.Closed);
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 4, 30, 0, TimeSpan.Zero), before.ClosesAt);
        Assert.False(LunchSessions.ClockFor("Asia/Ho_Chi_Minh", null, DateTime.UtcNow).Closed); // no deadline = never closes
    }

    [Fact]
    public void New_poll_is_for_today_before_the_deadline_and_tomorrow_after()
    {
        var deadline = new TimeOnly(11, 30);
        var morning = LunchSessions.ClockFor("Asia/Ho_Chi_Minh", deadline, new DateTime(2026, 11, 17, 3, 0, 0, DateTimeKind.Utc));   // 10:00 Hanoi
        var afternoon = LunchSessions.ClockFor("Asia/Ho_Chi_Minh", deadline, new DateTime(2026, 11, 17, 7, 0, 0, DateTimeKind.Utc)); // 14:00 Hanoi
        Assert.Equal(new DateOnly(2026, 11, 17), LunchSessions.NextPollDay(morning));
        Assert.Equal(new DateOnly(2026, 11, 18), LunchSessions.NextPollDay(afternoon));
        Assert.Equal(new DateOnly(2026, 11, 17), LunchSessions.NextPollDay(LunchSessions.ClockFor("Asia/Ho_Chi_Minh", null, DateTime.UtcNow.Date)));

        // Tomorrow's poll, created this afternoon, stays open until tomorrow 11:30.
        var tomorrow = LunchSessions.ClockFor("Asia/Ho_Chi_Minh", deadline, new DateTime(2026, 11, 17, 7, 0, 0, DateTimeKind.Utc), new DateOnly(2026, 11, 18));
        Assert.False(tomorrow.Closed);
        Assert.Equal(new DateTimeOffset(2026, 11, 18, 4, 30, 0, TimeSpan.Zero), tomorrow.ClosesAt);
    }

    [Fact]
    public void Dice_only_settle_ties_and_no_vote_days()
    {
        Assert.Empty(LunchSessions.DeadlineContenders([Option(Tue, "A", 3), Option(Tue, "B", 2)]));                 // clear winner
        Assert.Empty(LunchSessions.DeadlineContenders([Option(Tue, "A", 0, pickedBy: "Lan"), Option(Tue, "B", 5)])); // random pick already decided
        Assert.Equal(["A", "B"], LunchSessions.DeadlineContenders([Option(Tue, "A", 2), Option(Tue, "B", 2), Option(Tue, "C", 1)]).Select(c => c.Dish));
        Assert.Equal(3, LunchSessions.DeadlineContenders([Option(Tue, "A", 0), Option(Tue, "B", 0), Option(Tue, "C", 0)]).Count);
        Assert.Single(LunchSessions.DeadlineContenders([Option(Tue, "A", 0)])); // a lone option with no votes still gets "chosen"
        Assert.Empty(LunchSessions.DeadlineContenders([]));
    }
}

public class LocalizationTests
{
    static DirectoryInfo WebProject()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "LunchSuggest.Web"))) dir = dir.Parent;
        return new DirectoryInfo(Path.Combine(dir?.FullName ?? throw new DirectoryNotFoundException("repo root"), "src", "LunchSuggest.Web"));
    }

    static Dictionary<string, string> Vietnamese() =>
        System.Xml.Linq.XDocument.Load(Path.Combine(WebProject().FullName, "Resources", "SharedResource.vi.resx"))
            .Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);

    [Fact]
    public void Every_text_on_the_pages_has_a_Vietnamese_translation()
    {
        var vi = Vietnamese();
        var used = new HashSet<string>();
        foreach (var file in WebProject().EnumerateFiles("*.razor", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file.FullName);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"L\[""((?:[^""\\]|\\.)*)"""))
                used.Add(m.Groups[1].Value.Replace("\\\"", "\""));
            if (System.Text.RegularExpressions.Regex.Match(text, @"Ideas = \[(.*?)\];") is { Success: true } ideas)
                foreach (System.Text.RegularExpressions.Match idea in System.Text.RegularExpressions.Regex.Matches(ideas.Groups[1].Value, @"""([^""]+)"""))
                    used.Add(idea.Groups[1].Value);
        }
        Assert.NotEmpty(used);
        Assert.Empty(used.Where(k => !vi.ContainsKey(k)));
        // .NET resource names ignore case: "Random pick" and "random pick" would overwrite each other.
        Assert.Empty(used.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void Translations_keep_their_placeholders()
    {
        static string Holes(string s) => string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Order());
        Assert.Empty(Vietnamese().Where(kv => Holes(kv.Key) != Holes(kv.Value)).Select(kv => kv.Key));
    }

    [Fact]
    public void Voter_names_are_animal_and_personality_in_the_right_order()
    {
        var vi = LunchSuggest.Web.VoterNames.Random(new System.Globalization.CultureInfo("vi"));
        var en = LunchSuggest.Web.VoterNames.Random(new System.Globalization.CultureInfo("en"));
        Assert.InRange(vi.Length, 5, 40);
        Assert.InRange(en.Length, 5, 40);
        Assert.Contains(' ', vi);
        // English puts the mood first ("Chilling Capybara"), Vietnamese the animal ("Chuột Lang Nước Thư Thái").
        Assert.Matches("^(Chilling|Cheerful|Lazy|Curious|Energetic|Shy|Funny|Dreamy|Calm|Sneaky|Brave|Gentle|Playful|Busy|Hungry|Snacky|Foodie|Sunny|Sleepy|Zen) ", en);
    }
}

public class DishIconTests
{
    [Theory]
    [InlineData("Phở bò tái", "🍜")]
    [InlineData("Bún Đậu Rế", "🍜")]
    [InlineData("BÁNh MỲ TƯƠI", "🥖")]                 // bánh mì before plain "mì"/"bánh"
    [InlineData("Lẩu Tomyum Hải Sản chua cay", "🍲")]  // lẩu before hải sản
    [InlineData("Cơm Bò Chao Sốt Tiêu Đen", "🍚")]
    [InlineData("Cánh gà chiên", "🍗")]
    [InlineData("Sườn trâu nướng tảng", "🍢")]
    [InlineData("Ốc Nhật Linh", "🐚")]
    [InlineData("Món gà/ngan", "🍗")]
    [InlineData("Cafe 12 phố Huế", "☕")]               // phố (street) isn't phở
    [InlineData("Something unknown", "🍽️")]
    public void Dish_gets_a_matching_icon(string dish, string icon) => Assert.Equal(icon, LunchSuggest.Web.DishIcon.For(dish));

    [Fact]
    public void Place_name_is_used_when_the_option_is_a_place() =>
        Assert.Equal("🍜", LunchSuggest.Web.DishIcon.For(null, "Phở Cội", null));
}
