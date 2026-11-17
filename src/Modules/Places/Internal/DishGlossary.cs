using System.Text;
using System.Text.RegularExpressions;

namespace LunchSuggest.Modules.Places.Internal;

/// <summary>
/// English glosses for Vietnamese dish words in place names ("Phở Cội" → "pho noodle soup").
/// Many places only carry a name and "restaurant"; without this, English queries like "noodle soup" can't find them.
/// Tested on Hanoi data: this fixed matches that switching to multilingual embedding models (bge-m3, paraphrase-multilingual) did not.
/// </summary>
internal static class DishGlossary
{
    // Matched on whole words, with diacritics: "phở" (soup) ≠ "phố" (street). Longer phrases win ("bún chả" before "bún").
    static readonly (string Word, string Gloss)[] Entries =
    [
        ("bún chả", "grilled pork with rice noodles"), ("bún bò", "beef noodle soup"), ("bún riêu", "crab noodle soup"),
        ("bún đậu", "rice noodles with fried tofu"), ("bánh mì", "banh mi sandwich"), ("bánh mỳ", "banh mi sandwich"),
        ("bánh cuốn", "steamed rice rolls"), ("cơm tấm", "broken rice with grilled pork"), ("cơm văn phòng", "office lunch rice set"),
        ("chả cá", "grilled fish"), ("hải sản", "seafood"), ("trà sữa", "bubble milk tea"), ("cà phê", "coffee"),
        ("sinh tố", "smoothie"), ("hủ tiếu", "noodle soup"), ("phở", "pho noodle soup"), ("bún", "rice vermicelli noodles"),
        ("mì", "noodles"), ("mỳ", "noodles"), ("miến", "glass noodle soup"), ("cơm", "rice dishes"), ("xôi", "sticky rice"),
        ("cháo", "rice porridge"), ("lẩu", "hot pot"), ("nướng", "grilled barbecue"), ("ốc", "snails shellfish"),
        ("gà", "chicken"), ("vịt", "duck"), ("bò", "beef"), ("lợn", "pork"), ("heo", "pork"), ("dê", "goat"),
        ("chay", "vegetarian"), ("nem", "spring rolls"), ("gỏi", "salad"), ("trà", "tea"), ("cá", "fish"),
    ];

    static readonly (Regex Pattern, string Gloss)[] Patterns = Entries
        .OrderByDescending(e => e.Word.Length)
        .Select(e => (new Regex($@"(?<!\w){Regex.Escape(e.Word)}(?!\w)", RegexOptions.CultureInvariant), e.Gloss))
        .ToArray();

    /// <returns>Comma-separated glosses, or null when the name has no known dish words.</returns>
    public static string? Describe(string name)
    {
        var text = name.ToLowerInvariant().Normalize(NormalizationForm.FormC);
        var found = new List<string>();
        foreach (var (pattern, gloss) in Patterns)
        {
            if (!pattern.IsMatch(text)) continue;
            if (!found.Contains(gloss)) found.Add(gloss);
            text = pattern.Replace(text, " "); // "bún chả" must not also count as "bún"
        }
        return found.Count == 0 ? null : string.Join(", ", found);
    }
}
