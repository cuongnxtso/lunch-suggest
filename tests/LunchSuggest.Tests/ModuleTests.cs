using System.Text.Json;
using LunchSuggest.Modules.Places;
using LunchSuggest.Modules.Places.Internal;
using LunchSuggest.Modules.Voting;
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
    [InlineData("coffee_shop", false, null)]  // drinks, not lunch
    [InlineData("fast_food_restaurant", true, null)]
    [InlineData("bubble_tea", false, null)]
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
    [InlineData("coffee shop", true)]   // Overture, humanized
    [InlineData("cafe", true)]          // Overpass / Google
    [InlineData("bubble tea", true)]
    [InlineData("wine_bar", true)]
    [InlineData("bar_and_grill", false)]
    [InlineData("sushi bar", false)]
    [InlineData("restaurant", false)]
    [InlineData("vietnamese", false)]
    public void Drink_only_places_are_recognised(string category, bool drinkOnly) =>
        Assert.Equal(drinkOnly, FoodCategory.IsDrinkOnly(category));

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
        Assert.Empty(dishes); // "Bạc xỉu" is a drink
    }

    [Theory]
    [InlineData("Cà phê muối", true)]
    [InlineData("Trà đào cam sả", true)]
    [InlineData("Bia hơi", true)]
    [InlineData("Sinh tố bơ", true)]
    [InlineData("Egg coffee", true)]
    [InlineData("Cold Brew Túi Lọc", true)]
    [InlineData("Bò nhúng bia", false)]   // cooked in beer: food
    [InlineData("Bánh mì", false)]
    [InlineData("Chè thập cẩm", false)]
    public void Drinks_are_not_dishes(string dish, bool drink) =>
        Assert.Equal(drink, LunchSuggest.Modules.Dishes.Internal.DishExtractor.IsDrink(dish));

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
        // No deadline: always today, even late in the day.
        Assert.Equal(new DateOnly(2026, 11, 17), LunchSessions.NextPollDay(LunchSessions.ClockFor("Asia/Ho_Chi_Minh", null, new DateTime(2026, 11, 17, 15, 0, 0, DateTimeKind.Utc))));

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

public class SettingsTests
{
    static readonly System.Net.IPAddress Lan = System.Net.IPAddress.Parse("10.3.0.77"), DockerGateway = System.Net.IPAddress.Parse("172.18.0.1");

    [Fact]
    public void Without_an_admin_port_only_loopback_callers_get_in()
    {
        Assert.True(LunchSuggest.Web.AdminAccess.IsAllowed(System.Net.IPAddress.Loopback, 5063, null));
        Assert.True(LunchSuggest.Web.AdminAccess.IsAllowed(System.Net.IPAddress.IPv6Loopback, 5063, null));
        Assert.True(LunchSuggest.Web.AdminAccess.IsAllowed(System.Net.IPAddress.Parse("::ffff:127.0.0.1"), 5063, null));
        Assert.False(LunchSuggest.Web.AdminAccess.IsAllowed(Lan, 5063, null));
        Assert.False(LunchSuggest.Web.AdminAccess.IsAllowed(null, 5063, null));
    }

    [Fact]
    public void With_an_admin_port_only_that_port_counts_whatever_the_ip()
    {
        // In Docker every caller looks like the gateway, so the IP can't tell this computer from the LAN.
        Assert.True(LunchSuggest.Web.AdminAccess.IsAllowed(DockerGateway, 8081, 8081));
        Assert.False(LunchSuggest.Web.AdminAccess.IsAllowed(DockerGateway, 8080, 8081));
        Assert.False(LunchSuggest.Web.AdminAccess.IsAllowed(System.Net.IPAddress.Loopback, 8080, 8081));
    }

    [Theory]
    [InlineData("http://10.3.0.54:8686", "http://10.3.0.54:8686")]
    [InlineData(" http://10.3.0.54:8686/ ", "http://10.3.0.54:8686")]      // trailing slash dropped, so "/today" can be appended
    [InlineData("https://lunch.example.com", "https://lunch.example.com")]
    [InlineData("", null)]                                                // empty = the viewer's own address
    public void Share_address_is_normalized(string input, string? expected) =>
        Assert.Equal(expected, LunchSessions.ShareUrlOrNull(input));

    [Theory]
    [InlineData("10.3.0.54:8686")]                   // no scheme
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://10.3.0.54")]
    [InlineData("http://10.3.0.54:8686/today")]      // a path would double up with the page's
    [InlineData("http://10.3.0.54:8686/?x=1")]
    [InlineData("http://user:pw@10.3.0.54:8686")]
    public void Bad_share_addresses_are_refused(string input) =>
        Assert.Throws<ArgumentException>(() => LunchSessions.ShareUrlOrNull(input));

    [Fact]
    public void Only_todays_and_tomorrows_polls_can_be_reset()
    {
        var today = new DateOnly(2026, 11, 18);
        Assert.True(LunchSessions.CanReset(today, today, closed: false));
        Assert.True(LunchSessions.CanReset(today.AddDays(1), today, closed: false));
        Assert.False(LunchSessions.CanReset(today, today, closed: true)); // past the deadline: the result stays in history
        Assert.False(LunchSessions.CanReset(today.AddDays(-1), today, closed: true)); // history: runner-ups come from it
        Assert.False(LunchSessions.CanReset(today.AddDays(2), today, closed: false));
    }

    static GroupSettings Valid => new("default", " Sport Hotel lunch ", 21.0014, 105.8029, 300, "Asia/Ho_Chi_Minh", "  ", new TimeOnly(11, 30));

    [Fact]
    public void Group_settings_are_tidied()
    {
        var g = LunchSessions.Validated(Valid with { SiteTitle = "  Cơm trưa ", LandingIcon = " " });
        Assert.Equal("Sport Hotel lunch", g.Title);
        Assert.Null(g.Address); // blank = none
        Assert.Equal("Cơm trưa", g.SiteTitle);
        Assert.Null(g.LandingIcon); // blank = the daily icon
        LunchSessions.Validated(Valid with { LandingIcon = "👨‍👩‍👧‍👦", PicksMin = 0, PicksMax = 0, RunnerUpDays = 0 }); // ZWJ emoji, empty polls, no runner-ups
    }

    [Theory]
    [InlineData("lat")]
    [InlineData("nan")]
    [InlineData("radius")]
    [InlineData("tz")]
    [InlineData("deadline")]
    [InlineData("title")]
    [InlineData("maxOptions")]
    [InlineData("picksAboveMaxOptions")]
    [InlineData("picksAboveLimit")]
    [InlineData("picksMinAboveMax")]
    [InlineData("runnerUpDays")]
    [InlineData("icon")]
    public void Bad_group_settings_are_refused(string field)
    {
        var bad = field switch
        {
            "maxOptions" => Valid with { MaxOptions = 1 },
            "picksAboveMaxOptions" => Valid with { MaxOptions = 3, PicksMax = 4 },
            "picksAboveLimit" => Valid with { MaxOptions = 20, PicksMax = 9 },
            "picksMinAboveMax" => Valid with { PicksMin = 4, PicksMax = 3 },
            "runnerUpDays" => Valid with { RunnerUpDays = -1 },
            "icon" => Valid with { LandingIcon = "this is not an icon at all" },
            "lat" => Valid with { Lat = 95 },
            "nan" => Valid with { Lng = double.NaN },
            "radius" => Valid with { RadiusMeters = 50 },
            "tz" => Valid with { TimeZone = "Mars/Olympus" },
            "deadline" => Valid with { Deadline = new TimeOnly(0, 10) },
            _ => Valid with { Title = "  " },
        };
        Assert.Throws<ArgumentException>(() => LunchSessions.Validated(bad));
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

public class SignInTests
{
    static System.Security.Claims.ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new System.Security.Claims.ClaimsIdentity(claims.Select(c => new System.Security.Claims.Claim(c.Type, c.Value)), "test"));

    [Fact]
    public void A_signed_in_account_votes_under_its_google_id_and_display_name()
    {
        var v = LunchSuggest.Web.GoogleSignIn.Voter(User(("sub", "1234"), ("name", "Nguyễn Văn A"), ("email", "a@gmail.com")));
        Assert.Equal(("g-1234", "Nguyễn Văn A", "a@gmail.com"), v);
        // No display name: the part of the e-mail before the @ stands in (the full address never shows on polls).
        Assert.Equal("a", LunchSuggest.Web.GoogleSignIn.Voter(User(("sub", "1"), ("email", "a@gmail.com")))?.Name);
    }

    [Fact]
    public void Visitors_who_are_not_signed_in_or_have_no_email_are_not_voters()
    {
        Assert.Null(LunchSuggest.Web.GoogleSignIn.Voter(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
        Assert.Null(LunchSuggest.Web.GoogleSignIn.Voter(User(("name", "No sub"), ("email", "x@gmail.com"))));
        Assert.Null(LunchSuggest.Web.GoogleSignIn.Voter(User(("sub", "1"), ("name", "No e-mail"))));
    }

    [Theory]
    [InlineData("/today", "/today")]
    [InlineData("/poll/2026-11-18", "/poll/2026-11-18")]
    [InlineData("//evil.com", "/")]
    [InlineData(@"/\evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData(null, "/")]
    public void Sign_in_only_returns_to_pages_on_this_site(string? url, string expected) =>
        Assert.Equal(expected, LunchSuggest.Web.GoogleSignIn.LocalOrHome(url));

    static readonly string[] Listed = ["Đào Đình Luyện", "Nguyễn Huỳnh Đức", "Phạm Đỗ Duy", "Trương Trọng Lợi"];

    [Theory]
    [InlineData("Đào Đình Luyện", true)]
    [InlineData("Dao Đinh Luyen", true)]       // the spellings from the request
    [InlineData("Nguyen Huynh Duc", true)]
    [InlineData("Pham Do Duy", true)]
    [InlineData("Truong Trong Loi", true)]
    [InlineData("Lợi Trương Trọng", true)]     // Google's given-name-first order
    [InlineData("  PHẠM   ĐỖ duy ", true)]
    [InlineData("Phạm Duy", false)]           // a word short is someone else
    [InlineData("Phạm Đỗ Duy Anh", false)]
    [InlineData("", false)]
    public void Listed_names_match_with_or_without_accents(string googleName, bool expected) =>
        Assert.Equal(expected, LunchSuggest.Web.GoogleSignIn.IsListed(googleName, Listed));
}

public class SuggestionTests
{
    [Fact]
    public void Suggested_dishes_come_first_and_each_dish_shows_once()
    {
        var dishes = LunchSuggest.Modules.Dishes.Internal.DishGuide.WithSuggested(["Bún chả", "Nem rán"], ["bún chả ", "Phở bò"]);
        Assert.Equal(["Bún chả", "Nem rán", "Phở bò"], dishes);
        Assert.Equal(["Bún chả"], LunchSuggest.Modules.Dishes.Internal.DishGuide.WithSuggested(["Bún chả"], null)); // never looked up
    }

    [Fact]
    public void A_suggested_place_is_tidied_and_waits_for_approval()
    {
        var p = LunchSuggest.Modules.Places.Internal.PlaceDirectory.Validated("  Phở Thìn ", " ", null, 21.0, 105.8, "Cuong NX");
        Assert.Equal("Phở Thìn", p.Name);
        Assert.Null(p.Cuisine); // blank = none
        Assert.StartsWith(LunchSuggest.Modules.Places.PlacesModule.SuggestedIdPrefix, p.Id);
        Assert.False(p.Approved);
    }

    [Theory]
    [InlineData("", 21.0, 105.8)]            // no name
    [InlineData("Phở", 91.0, 105.8)]         // not a point on Earth
    [InlineData("Phở", double.NaN, 105.8)]
    public void Bad_place_suggestions_are_refused(string name, double lat, double lng) =>
        Assert.Throws<ArgumentException>(() => LunchSuggest.Modules.Places.Internal.PlaceDirectory.Validated(name, null, null, lat, lng, "Cuong NX"));

    [Fact]
    public void Place_names_have_a_length_limit() =>
        Assert.Throws<ArgumentException>(() =>
            LunchSuggest.Modules.Places.Internal.PlaceDirectory.Validated(new string('a', 121), null, null, 21.0, 105.8, "Cuong NX"));
}

public class WeekdayTests
{
    static readonly TimeOnly Deadline = new(11, 30);
    // Hanoi is UTC+7: 03:00 UTC = 10:00 there (before the deadline), 07:00 UTC = 14:00 (after).
    static DateOnly NextPoll(int day, int utcHour) =>
        LunchSessions.NextPollDay(LunchSessions.ClockFor("Asia/Ho_Chi_Minh", Deadline, new DateTime(2026, 11, day, utcHour, 0, 0, DateTimeKind.Utc)));

    [Fact]
    public void Polls_skip_the_weekend()
    {
        Assert.Equal(new DateOnly(2026, 11, 20), NextPoll(20, 3));  // Friday morning: today
        Assert.Equal(new DateOnly(2026, 11, 23), NextPoll(20, 7));  // Friday afternoon: Monday, not Saturday
        Assert.Equal(new DateOnly(2026, 11, 23), NextPoll(21, 3));  // Saturday: Monday
        Assert.Equal(new DateOnly(2026, 11, 23), NextPoll(22, 3));  // Sunday morning: Monday
        Assert.Equal(new DateOnly(2026, 11, 19), NextPoll(18, 7));  // a normal weekday: tomorrow
        Assert.False(PollDays.IsPollDay(new DateOnly(2026, 11, 21)));
        Assert.True(PollDays.IsPollDay(new DateOnly(2026, 11, 23)));
    }

    [Fact]
    public void Mondays_poll_can_change_from_friday_on()
    {
        var friday = new DateOnly(2026, 11, 20);
        Assert.True(LunchSessions.CanChange(friday, friday));
        Assert.True(LunchSessions.CanChange(new DateOnly(2026, 11, 23), friday));   // Monday
        Assert.False(LunchSessions.CanChange(new DateOnly(2026, 11, 24), friday));  // Tuesday: too far ahead
        Assert.False(LunchSessions.CanChange(new DateOnly(2026, 11, 19), friday));  // Thursday: history
        Assert.True(LunchSessions.CanReset(new DateOnly(2026, 11, 23), friday, closed: false));
    }

    [Fact]
    public void Polls_skip_public_holidays()
    {
        DateOnly[] holidays = [new(2026, 11, 24)]; // Tuesday
        var monday = new DateOnly(2026, 11, 23);
        Assert.False(PollDays.IsPollDay(new DateOnly(2026, 11, 24), holidays));
        // Monday afternoon: Wednesday's poll, and it can change (and be reset) from Monday on.
        var mondayAfternoon = LunchSessions.ClockFor("Asia/Ho_Chi_Minh", Deadline, new DateTime(2026, 11, 23, 7, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateOnly(2026, 11, 25), LunchSessions.NextPollDay(mondayAfternoon, holidays));
        Assert.True(LunchSessions.CanChange(new DateOnly(2026, 11, 25), monday, holidays));
        Assert.True(LunchSessions.CanReset(new DateOnly(2026, 11, 25), monday, closed: false, holidays));
        Assert.False(LunchSessions.CanChange(new DateOnly(2026, 11, 25), monday)); // without the holiday: too far ahead
    }

    [Fact]
    public void Holiday_lists_are_read_and_tidied()
    {
        Assert.Equal([new DateOnly(2026, 11, 24), new DateOnly(2027, 1, 1)], PollDays.Parse("24/11/2026,\n2027-01-01"));
        Assert.Equal([new DateOnly(2026, 9, 2)], PollDays.Parse(" 2/9/2026 "));
        Assert.Empty(PollDays.Parse("  ")!);
        Assert.Null(PollDays.Parse("24/13/2026"));
        Assert.Null(PollDays.Parse("tomorrow"));
        var g = LunchSessions.Validated(new GroupSettings("zz", "Test", 21, 105, 500, null,
            Holidays: [new(2027, 1, 1), new(2026, 11, 24), new(2027, 1, 1)]));
        Assert.Equal([new DateOnly(2026, 11, 24), new DateOnly(2027, 1, 1)], g.Holidays);
    }
}

public class RemovalTests
{
    [Fact]
    public void Removed_dishes_are_left_out()
    {
        var dishes = LunchSuggest.Modules.Dishes.Internal.DishGuide.WithSuggested(["Bún chả"], ["Phở bò", "Nem rán"], ["phở bò "]);
        Assert.Equal(["Bún chả", "Nem rán"], dishes);
        // A removal of a suggested dish hides it too.
        Assert.Empty(LunchSuggest.Modules.Dishes.Internal.DishGuide.WithSuggested(["Bún chả"], null, ["BÚN CHẢ"]));
    }
}
