namespace LunchSuggest.Modules.Places.Internal;

/// <summary>Which place categories are lunch (food), from any provider: "vietnamese_restaurant", "coffee shop", "cafe".</summary>
internal static class FoodCategory
{
    // Whole words, so "pho" doesn't match "mobile_phone_store".
    static readonly HashSet<string> MealWords = ["restaurant", "bakery", "food", "bistro", "diner", "eatery", "canteen", "buffet",
        "noodle", "noodles", "pho", "bbq", "barbecue", "grill", "sandwich", "burger", "pizza", "sushi", "dessert", "snack"];
    static readonly HashSet<string> DrinkWords = ["cafe", "café", "coffee", "tea", "juice", "smoothie", "bar", "pub", "beer",
        "wine", "cocktail", "brewery", "boba"];
    static readonly HashSet<string> NotFoodWords = ["supplier", "wholesale", "equipment", "delivery", "consultant", "school",
        "grocery", "supermarket", "catering", "internet", "pet", "store", "distributor", "manufacturer", "market"];

    static string[] Words(string category) => category.ToLowerInvariant().Split(['_', ' '], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Somewhere to eat lunch: a meal word ("bar_and_grill", "sushi_bar" count) and not a shop or service.</summary>
    public static bool IsFood(string? category) =>
        category is not null && Words(category) is var w && w.Any(MealWords.Contains) && !w.Any(NotFoodWords.Contains);

    /// <summary>Cafés, tea and juice shops, bars: drinks, not lunch. Filters cached rows and other providers too.</summary>
    public static bool IsDrinkOnly(string? category) =>
        category is not null && Words(category) is var w && w.Any(DrinkWords.Contains) && !w.Any(MealWords.Contains);
}
