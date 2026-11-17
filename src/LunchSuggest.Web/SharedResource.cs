namespace LunchSuggest.Web;

/// <summary>
/// Marker for the app's one translation file, Resources/SharedResource.vi.resx.
/// Keys are the English text itself, so English needs no file (a missing key shows the key).
/// </summary>
public sealed class SharedResource
{
    public static readonly string[] Cultures = ["vi", "en"];
    public const string DefaultCulture = "vi";
}
