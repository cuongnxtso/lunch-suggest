using LunchSuggest.Modules.Dishes;
using LunchSuggest.Modules.Places;
using LunchSuggest.Modules.Search;
using LunchSuggest.Modules.Voting;
using LunchSuggest.Web;
using LunchSuggest.Web.Components;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddLocalization(o => o.ResourcesPath = "Resources");

// In a container the default key folder is wiped on every restart, which would make everyone re-enter their name.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysPath));

// Modules: each owns its schema and exposes one public interface.
builder.Services
    .AddSearchModule(builder.Configuration)
    .AddPlacesModule(builder.Configuration)
    .AddVotingModule(builder.Configuration)
    .AddDishesModule(builder.Configuration);
builder.Services.AddSingleton<LunchSuggest.Web.TodayPicker>();
builder.Services.AddGoogleSignIn(builder.Configuration);

// The one lunch group this app runs. The "Lunch" settings section creates it; the Settings page edits it afterwards.
var group = builder.Configuration.GetSection("Lunch").Get<GroupSettings>()
    ?? throw new InvalidOperationException("Missing the 'Lunch' settings section (group name, location, radius, time zone).");
builder.Services.AddSingleton(group);

var app = builder.Build();

// ponytail: migrate on startup is fine for one instance; run migrations as a deploy step when scaling out.
await app.Services.MigrateSearchModuleAsync();
await app.Services.MigratePlacesModuleAsync();
await app.Services.MigrateVotingModuleAsync();
await app.Services.MigrateDishesModuleAsync();
await app.Services.GetRequiredService<ILunchSessions>().EnsureGroupAsync(group);

// The ngrok tunnel (HTTPS) talks plain http to this container and says "X-Forwarded-Proto: https". Trust that, so sign-in
// builds https return addresses and secure cookies. Only the scheme is taken; the Settings check goes by port, not caller IP.
var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto };
forwarded.KnownIPNetworks.Clear(); // the tunnel's address isn't known in advance
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Settings (page and form posts) only for this computer; everyone else gets a plain 404, so it isn't even advertised.
var adminPort = app.Configuration.GetValue<int?>("Admin:Port");
app.Use(async (http, next) =>
{
    if (http.Request.Path.StartsWithSegments(AdminAccess.Path)
        && !AdminAccess.IsAllowed(http.Connection.RemoteIpAddress, http.Connection.LocalPort, adminPort))
    {
        http.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }
    await next();
});

// Vietnamese unless the visitor picked English in the header (cookie). The browser's language is ignored on purpose.
// Blazor Server keeps the culture of the request that opened the page, so switching reloads the page.
var localization = new RequestLocalizationOptions()
    .SetDefaultCulture(SharedResource.DefaultCulture)
    .AddSupportedCultures(SharedResource.Cultures)
    .AddSupportedUICultures(SharedResource.Cultures);
localization.RequestCultureProviders = [new CookieRequestCultureProvider()];
app.UseRequestLocalization(localization);

if (GoogleSignIn.IsOn(app.Configuration)) app.UseAuthentication();
app.UseAntiforgery();
app.MapGoogleSignIn();

app.MapGet("/culture/{culture}", (string culture, string? redirect, HttpContext http) =>
{
    if (!SharedResource.Cultures.Contains(culture)) culture = SharedResource.DefaultCulture;
    http.Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
        CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(culture)),
        new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, HttpOnly = true, SameSite = SameSiteMode.Lax });
    return Results.LocalRedirect(GoogleSignIn.LocalOrHome(redirect));
});

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
