using System.Globalization;

namespace LunchSuggest.Web;

/// <summary>Random fun voter names, so nobody has to type one: "Chilling Capybara" / "Chuột Lang Nước Thư Thái".</summary>
public static class VoterNames
{
    // Same order in both lists, so a name keeps its meaning if someone switches language (it doesn't change on its own).
    static readonly string[] AnimalsEn = ["Capybara", "Panda", "Fox", "Cat", "Puppy", "Otter", "Penguin", "Koala", "Tiger", "Squirrel",
        "Bunny", "Owl", "Dolphin", "Giraffe", "Alpaca", "Red Panda", "Hedgehog", "Duck", "Elephant", "Monkey"];
    static readonly string[] AnimalsVi = ["Chuột Lang Nước", "Gấu Trúc", "Cáo", "Mèo", "Cún", "Rái Cá", "Chim Cánh Cụt", "Gấu Koala", "Hổ", "Sóc",
        "Thỏ", "Cú Mèo", "Cá Heo", "Hươu Cao Cổ", "Lạc Đà Alpaca", "Gấu Mèo", "Nhím", "Vịt", "Voi", "Khỉ"];
    static readonly string[] MoodsEn = ["Chilling", "Cheerful", "Lazy", "Curious", "Energetic", "Shy", "Funny", "Dreamy", "Calm", "Sneaky",
        "Brave", "Gentle", "Playful", "Busy", "Hungry", "Snacky", "Foodie", "Sunny", "Sleepy", "Zen"];
    static readonly string[] MoodsVi = ["Thư Thái", "Vui Vẻ", "Lười Biếng", "Tò Mò", "Năng Động", "Nhút Nhát", "Hài Hước", "Mơ Mộng", "Điềm Tĩnh", "Lém Lỉnh",
        "Dũng Cảm", "Hiền Lành", "Tinh Nghịch", "Chăm Chỉ", "Đói Bụng", "Ham Ăn", "Sành Ăn", "Lạc Quan", "Ngái Ngủ", "Bình Thản"];

    /// <summary>Vietnamese puts the animal first ("Cáo Tò Mò"); English the mood ("Curious Fox").</summary>
    public static string Random(CultureInfo culture)
    {
        var animal = System.Random.Shared.Next(AnimalsEn.Length);
        var mood = System.Random.Shared.Next(MoodsEn.Length);
        return culture.TwoLetterISOLanguageName == "vi"
            ? $"{AnimalsVi[animal]} {MoodsVi[mood]}"
            : $"{MoodsEn[mood]} {AnimalsEn[animal]}";
    }
}
