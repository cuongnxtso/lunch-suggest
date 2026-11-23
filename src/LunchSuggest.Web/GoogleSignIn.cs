using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace LunchSuggest.Web;

/// <summary>
/// Google sign-in. On when "Google:ClientId" is set. Then anyone with the link can watch, but only signed-in people whom the
/// organiser approved on the Settings page can vote, add options or create a poll. Off: anyone with the link votes under a random name.
/// </summary>
public static class GoogleSignIn
{
    const string Section = "Google";

    public static bool IsOn(IConfiguration config) => config[$"{Section}:ClientId"] is { Length: > 0 };

    public static IServiceCollection AddGoogleSignIn(this IServiceCollection services, IConfiguration config)
    {
        if (!IsOn(config)) return services;
        var s = config.GetSection(Section);
        string Need(string key) => s[key] is { Length: > 0 } v ? v
            : throw new InvalidOperationException($"'{Section}:ClientId' is set, so '{Section}:{key}' is required too.");

        services.AddCascadingAuthenticationState(); // pages read the signed-in user from it
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie(o =>
            {
                o.Cookie.Name = "lunch.auth";
                o.ExpireTimeSpan = TimeSpan.FromDays(30); // sign in once a month, not every lunch
                o.SlidingExpiration = true;
            })
            .AddOpenIdConnect(o =>
            {
                o.Authority = "https://accounts.google.com";
                o.ClientId = Need("ClientId");
                o.ClientSecret = Need("ClientSecret");
                o.ResponseType = OpenIdConnectResponseType.Code;
                // Google sends the browser back with a plain GET instead of a cross-site form POST, which would lose
                // ngrok's "visit site" cookie and land on its warning page instead of finishing the sign-in.
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                o.CallbackPath = "/signin-oidc";
                o.Scope.Add("email");
                o.MapInboundClaims = false; // keep the short names: sub, name, email
                o.TokenValidationParameters.NameClaimType = "name";
            });
        return services;
    }

    public static void MapGoogleSignIn(this WebApplication app)
    {
        if (!IsOn(app.Configuration)) return;
        app.MapGet("/signin", (string? returnUrl) =>
            Results.Challenge(new AuthenticationProperties { RedirectUri = LocalOrHome(returnUrl) }));
        app.MapGet("/signout", async (string? returnUrl, HttpContext http) =>
        {
            // Signs out of this site only, not of Google.
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect(LocalOrHome(returnUrl));
        });
    }

    /// <summary>
    /// The signed-in person as a voter: the Google account id (stable per person), display name and e-mail.
    /// Null if not signed in, or if Google sent no e-mail (the organiser needs it to know who's asking).
    /// </summary>
    public static (string Id, string Name, string Email)? Voter(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true
            || user.FindFirst("sub")?.Value is not { Length: > 0 } sub
            || user.FindFirst("email")?.Value is not { Length: > 0 } email) return null;
        var name = user.FindFirst("name")?.Value is { Length: > 0 } n ? n : email[..Math.Max(1, email.IndexOf('@'))];
        return ("g-" + sub, name, email);
    }

    /// <summary>The "Google:AutoApprove" names: people allowed to vote as soon as they first sign in.</summary>
    public static IReadOnlyList<string> AutoApproveNames(IConfiguration config) =>
        config.GetSection($"{Section}:AutoApprove").Get<string[]>() ?? [];

    /// <summary>
    /// Whether a Google display name is on the list. Ignores accents (Đào = Dao), case, spacing and word order,
    /// since Google names are often "Luyện Đào Đình" or typed without accents.
    /// </summary>
    public static bool IsListed(string name, IEnumerable<string> list)
    {
        var key = NameKey(name);
        return key.Length > 0 && list.Any(n => NameKey(n) == key);
    }

    static string NameKey(string name)
    {
        var plain = new string(name.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray())
            .Replace('đ', 'd').Replace('Đ', 'd').ToLowerInvariant();
        return string.Join(' ', plain.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal));
    }

    /// <summary>Only a path on this site ("/today"), never an open redirect to another one.</summary>
    public static string LocalOrHome(string? url) =>
        url is { Length: > 0 } && url[0] == '/' && !url.StartsWith("//") && !url.StartsWith("/\\") ? url : "/";
}
