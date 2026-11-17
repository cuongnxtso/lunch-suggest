using System.Text;
using System.Text.RegularExpressions;

namespace LunchSuggest.Web;

/// <summary>Picks an emoji for a dish ("Phở bò tái" → 🍜) for the big result icon.</summary>
public static class DishIcon
{
    public const string Fallback = "🍽️";

    // First match wins, so specific words go before general ones ("bánh mì" before "bánh", "lẩu" before "bò").
    // Vietnamese words keep their diacritics: "phở" (soup) is not "phố" (street).
    static readonly (string[] Words, string Icon)[] Rules =
    [
        (["bánh mì", "bánh mỳ", "banh mi", "sandwich", "baguette"], "🥖"),
        (["lẩu", "hot pot", "hotpot"], "🍲"),
        (["phở", "pho", "bún", "mì", "mỳ", "miến", "hủ tiếu", "noodle", "noodles", "ramen", "udon"], "🍜"),
        (["cơm tấm", "cơm rang", "cơm chiên", "fried rice"], "🍛"),
        (["xôi", "sticky rice"], "🍙"),
        (["cháo", "porridge", "congee", "soup", "súp"], "🥣"),
        (["cơm", "rice"], "🍚"),
        (["sushi", "sashimi"], "🍣"),
        (["pizza"], "🍕"),
        (["burger", "hamburger"], "🍔"),
        (["pasta", "spaghetti", "mì ý"], "🍝"),
        (["nem", "chả giò", "spring roll", "spring rolls", "dumpling", "dumplings", "há cảo", "sủi cảo"], "🥟"),
        (["tôm", "shrimp", "prawn"], "🦐"),
        (["cua", "ghẹ", "crab"], "🦀"),
        (["ốc", "sò", "nghêu", "snail", "snails", "clam", "oyster"], "🐚"),
        (["hải sản", "seafood", "mực", "squid"], "🦑"),
        (["cá", "chả cá", "fish", "salmon"], "🐟"),
        (["gà", "chicken"], "🍗"),
        (["vịt", "ngan", "duck"], "🦆"),
        (["nướng", "xiên", "bbq", "barbecue", "grill", "grilled", "skewer"], "🍢"),
        (["bò", "trâu", "beef", "steak"], "🥩"),
        (["heo", "lợn", "sườn", "thịt", "pork", "ribs"], "🥓"),
        (["trứng", "egg", "eggs"], "🍳"),
        (["chay", "đậu", "rau", "vegetarian", "vegan", "tofu", "salad", "gỏi"], "🥗"),
        (["trà sữa", "milk tea", "bubble tea"], "🧋"),
        (["cà phê", "cafe", "café", "coffee", "bạc xỉu"], "☕"),
        (["trà", "tea"], "🍵"),
        (["sinh tố", "smoothie", "nước ép", "juice"], "🥤"),
        (["bánh", "cake", "chè", "kem", "dessert", "ice cream"], "🍰"),
    ];

    static readonly (Regex Pattern, string Icon)[] Patterns = Rules
        .Select(r => (new Regex(@"(?<!\w)(" + string.Join("|", r.Words.Select(Regex.Escape)) + @")(?!\w)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase), r.Icon))
        .ToArray();

    /// <summary>Looks at the dish first, then the place name and cuisine ("Phở Cội" → 🍜 even when the option is the place).</summary>
    public static string For(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var normalized = text.Normalize(NormalizationForm.FormC);
            foreach (var (pattern, icon) in Patterns)
                if (pattern.IsMatch(normalized)) return icon;
        }
        return Fallback;
    }
}
