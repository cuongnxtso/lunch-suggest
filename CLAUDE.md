# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

LunchSuggest: a .NET 10 Blazor Server app (Interactive Server render mode) where a team gets daily lunch suggestions near a fixed meeting point, votes or picks at random, and shares one `/today` link. README.md has the product rules (daily flow, deadline, results) in detail; read it before changing voting behaviour.

## Commands

```bash
# Dependencies for local dev: Postgres+pgvector on 127.0.0.1:5442, Ollama on :11434, SearXNG on :8888
docker compose up -d db ollama searxng
dotnet run --project src/LunchSuggest.Web            # http profile, port 5063

dotnet build LunchSuggest.slnx
dotnet test                                          # pure unit tests, no DB/Ollama needed
dotnet test --filter "FullyQualifiedName~VotingTests"                 # one class
dotnet test --filter "FullyQualifiedName~VotingTests.<MethodName>"    # one test

# Full stack as the LAN deployment (Production env, port 8686)
docker compose up -d --build

# EF Core migrations (dotnet-ef is a local tool: `dotnet tool restore` first). One DbContext per module,
# migrations live in each module's Internal/Migrations folder.
dotnet ef migrations add <Name> --project src/Modules/Voting --startup-project src/LunchSuggest.Web \
  --context VotingDbContext --output-dir Internal/Migrations
```

Migrations are applied automatically at startup (`Program.cs`); there is no separate migrate step.

## Architecture

Modular monolith: one host (`src/LunchSuggest.Web`) and four modules under `src/Modules/`, each a separate project:

| Module | Public interface | Schema |
|---|---|---|
| Places | `IPlaceDirectory` — nearby food places, cached per area; provider is Overture (DuckDB over GeoParquet), Overpass or Google via `Places:Provider` | `places` |
| Search | `ISemanticIndex` — pgvector embeddings via `Microsoft.Extensions.AI` `IEmbeddingGenerator` (Ollama `nomic-embed-text`, 768 dims) | `search` |
| Voting | `ILunchSessions` — one group, at most one poll per day, votes, random pick, deadline/dice, day results | `voting` |
| Dishes | `IDishGuide` — top dishes per place: SearXNG search + `IChatClient` (Ollama `qwen2.5:3b`), background queue, 30-day cache | `dishes` |

Module rules (keep them when adding code):
- Each module's single public file (`<Name>Module.cs`) holds the public records, the interface, and the `Add<Name>Module` / `Migrate<Name>ModuleAsync` extension methods. Everything else is `internal` in an `Internal/` folder; tests reach internals via `InternalsVisibleTo("LunchSuggest.Tests")` in `AssemblyInfo.cs`.
- Each module has its own `DbContext` (registered with `AddDbContextFactory`), its own Postgres schema, and its own `__EFMigrationsHistory` table in that schema. No foreign keys across schemas.
- The only module-to-module dependency is Places → Search. Logic that combines several modules lives in the host, e.g. `TodayPicker.cs` (seeds a new poll from Voting runner-ups + Places + Dishes).
- Services are singletons that create short-lived DbContexts from the factory.

Realtime: `ILunchSessions.Changed` and `IDishGuide.LookupFinished` are in-process C# events; `Components/Pages/Session.razor` subscribes and re-renders. Event handlers re-render through `OnCircuit`, which restores the viewer's culture (events arrive with another thread's culture, which turned pages English). This (plus the in-memory `TodayPicker` and dish queue) is why the app must run as a single instance.

Configuration: the one lunch group is created at startup from the `Lunch` section of `appsettings.json` (bound to `GroupSettings`) if it doesn't exist; after that the database row is the source of truth, edited on `/settings` (`ILunchSessions.GetGroupAsync` / `UpdateGroupAsync`). Pages read group values from the database, not from the `GroupSettings` singleton (which only supplies the group `Code`). Besides location/deadline, the group row holds the display settings (`SiteTitle`, `LandingIcon`) and poll tuning (`MaxOptions`, `PicksMin`/`PicksMax` used by `TodayPicker`, `RunnerUpDays`); add new settings there and validate them in `LunchSessions.Validated`. Dev connection string is in `appsettings.Development.json`; Docker overrides it via env vars in `docker-compose.yml`.

Settings access: `/settings` is only for this computer. `AdminAccess.IsAllowed` plus a middleware in `Program.cs` enforce it: with `Admin:Port` set (Docker: container port 8081, published on the host's `127.0.0.1:8690` only), only requests on that port; otherwise only loopback IPs. Keep `Settings.razor` statically rendered (`[ExcludeFromInteractiveRouting]`) so it can't be reached inside a live Blazor connection. The one live part is `ReviewQueue.razor` (suggestions and sign-ins to review), an interactive island that only the Settings page renders; keep admin actions out of components that other pages render.

UI: pages are `Landing.razor` (`/`), `Session.razor` (`/today`, `/poll/yyyy-MM-dd`) and `Settings.razor` (`/settings`, static). `App.razor` picks the render mode per page: interactive server without prerender, except pages marked `[ExcludeFromInteractiveRouting]`. The map is Leaflet, loaded as a JS module from `wwwroot/js/map.js`.

## Localization

Vietnamese is the default culture; English is the alternative (cookie set by `/culture/{culture}`, browser language ignored). UI text is written as `L["English text"]` — the English text is the resource key, so only `Resources/SharedResource.vi.resx` exists. `LocalizationTests` fail if a page uses a string without a Vietnamese entry, if a translation drops a `{0}` placeholder, or if two keys differ only by case. Whenever you add or change UI text, add/update the `.resx` entry.

## Tests

`tests/LunchSuggest.Tests/ModuleTests.cs` (xUnit) covers provider parsing, dish filtering, voting outcome/runner-up logic (`LunchSessions.Outcome` / `PickRunnerUps` are static and pure for this reason), localization coverage, and `DishIcon`. Prefer pulling decision logic into pure static methods so it can be tested without a database.
