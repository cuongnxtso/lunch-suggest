# LunchSuggest

Suggests lunch places near a meeting point, lets a team vote or pick at random, shared through an invite link.

## Stack

| Concern | Tech |
|---|---|
| App | .NET 10, ASP.NET Core Blazor (Interactive Server), Bootstrap 5 |
| Architecture | Modular monolith: one host, three modules, each with its own schema, DbContext and one public interface |
| Database | PostgreSQL 17 + EF Core 10 (Npgsql), migrations per module |
| Semantic search | pgvector (HNSW, cosine) + `Microsoft.Extensions.AI` `IEmbeddingGenerator` → Ollama `nomic-embed-text` (free, local) |
| Place data | Overture Maps Places (free, open) read with DuckDB by default; OpenStreetMap/Overpass and Google Places API (New) are switchable |
| Top dishes | SearXNG (self-hosted meta search, JSON API) + `Microsoft.Extensions.AI` `IChatClient` → Ollama `qwen2.5:3b` |
| Map | Leaflet 1.9 + OpenStreetMap tiles |
| Realtime | Blazor Server circuits + an in-process change event |
| Tests | xUnit |

## Modules

```
src/LunchSuggest.Web        host: Blazor pages, wiring, startup migrations
src/Modules/Places          IPlaceDirectory: fetch + cache nearby food places (schema "places")
src/Modules/Search          ISemanticIndex: embed + vector search (schema "search")
src/Modules/Voting          ILunchSessions: polls of dishes or places, invite codes, votes, random pick (schema "voting")
src/Modules/Dishes          IDishGuide: top dishes per place via web search + local LLM (schema "dishes")
```

Modules only see each other's public interface (`Places → Search`). Everything else is `internal`, and there are no foreign keys across schemas.

## Run (Docker, shared on the LAN)

```bash
docker compose up -d --build
```

This serves the site on **port 8686 of this machine**, e.g. `http://10.3.0.54:8686`. The first start pulls the Ollama models (about 2.3 GB). Only the web app is reachable from the network; Postgres, Ollama and SearXNG listen on 127.0.0.1 only. After changing code or settings, run the same command again.

On Windows, other computers also need an inbound firewall rule (run PowerShell as administrator):

```powershell
New-NetFirewallRule -DisplayName "LunchSuggest 8686" -Direction Inbound -Protocol TCP -LocalPort 8686 -Action Allow -Profile Private
```

In Docker the app runs as `Production`. Its settings come from `appsettings.json` plus the environment variables in `docker-compose.yml` (e.g. add `Lunch__Deadline: "12:00"` under `web.environment`). The keys that protect browser-stored voter names live in the `dpkeys` volume, so people keep their names across restarts.

Run only one copy of the app per database. "Today's picks" and the deadline dice are guarded per process, so two copies could both act.

## Run for development

```bash
docker compose up -d db ollama searxng
dotnet run --project src/LunchSuggest.Web
```

## Daily flow

1. **`/`: one big "Choose meal" button.** Its food icon changes every day (🍜 phở is in the set), the same for everyone, at the group's midnight. It opens **`/today`**, which is also the link to share.
2. **"➕ Create new poll", one poll a day.** Anyone who has entered a name can create a poll. Before the deadline it's for **today**; after the deadline it's for **tomorrow**. If that day already has a poll, the button becomes "Open tomorrow's poll →" (one poll per day is enforced by the database key too). Polls live at `/today` and `/poll/yyyy-MM-dd`. Tomorrow's poll can be voted on until tomorrow's deadline, and past days show their result.
3. **A new poll's picks.** Right after creation the poll gets 3–5 dishes (with a spinner meanwhile): recent runner-ups (up to `count - 1`) plus random dishes from random nearby places. Places with nothing found online go in as the place itself. Lookups for these go ahead of the list's background lookups. The first time in a new area takes about a minute, then it's mostly cached.
4. **Deadline (11:30 by default).** The page counts down ("⏰ Voting closes at 11:30 · 25 min left") and switches to the result at the deadline without a reload. After it, the server rejects adding options, votes and random picks. Anyone opening `/today` later sees only the result: a large icon for the dish (🍜 phở/bún, 🍚 cơm, 🍲 lẩu, 🥖 bánh mì…, see [DishIcon.cs](src/LunchSuggest.Web/DishIcon.cs)), where to eat, why it won, a Google Maps directions link and the runner-up. The winner is a random pick if someone made one, else the most votes. A tie, or a day with no votes, is settled once by the dice and saved, so everyone sees the same result. A day without a poll shows "No poll for today yet" and the create button.
5. **Results are stored.** As soon as a poll has closed (for example when tomorrow's poll is created at 2 PM), its **winner** and **runner-up** are saved in `voting."DayResults"` with their vote counts, so tomorrow's picks already include today's runner-up. The winner is the random pick, or else the most-voted option. The runner-up is the most-voted other option (at least 1 vote). The next days' picks take runner-ups from that table: last 5 days, newest first, skipping anything the group has eaten since. Logic: `LunchSessions.Outcome` / `PickRunnerUps`, unit tested.

## Names and languages

- **Voter names are random**, e.g. *Chilling Capybara*, or in Vietnamese *Chuột Lang Nước Thư Thái*: Vietnamese puts the animal first, English the personality. A name is created on the first visit, kept in the browser, and can be re-rolled with **🎲 New name**. Lists: [VoterNames.cs](src/LunchSuggest.Web/VoterNames.cs).
- **Vietnamese by default**, with a Tiếng Việt / English switch in the header (saved in a cookie for a year). The browser's language is ignored on purpose.
- Text lives in `L["English text"]` calls; the English text is the key, so only [Resources/SharedResource.vi.resx](src/LunchSuggest.Web/Resources/SharedResource.vi.resx) is needed. A unit test fails if any page uses text with no Vietnamese entry, if a translation drops a `{0}` placeholder, or if two keys differ only by upper/lower case (.NET resources would mix those up). Dates use translated patterns too (`Thứ Ba, 17/11`).
- To add a language: add it to `SharedResource.Cultures`, add a `SharedResource.<lang>.resx`, and add a button in `MainLayout.razor`.

## Settings

The lunch group comes from the `Lunch` section of [appsettings.json](src/LunchSuggest.Web/appsettings.json). Default: **Khách sạn Thể Thao (Sport Hotel), 15 Lê Văn Thiêm, Thanh Xuân, Hà Nội**, 300 m, `Asia/Ho_Chi_Minh`. Change it and restart; polls and history stay. Any setting can also be overridden with environment variables, e.g. `Lunch__RadiusMeters=500`.

| Key | Meaning |
|---|---|
| `Code` | Internal id of the group (keep it, or a new empty group starts) |
| `Title`, `Address` | Shown on the poll page |
| `Lat`, `Lng`, `RadiusMeters` | Meeting point and walking radius (100–5000 m) |
| `TimeZone` | IANA id; when the day (and the icon) changes |
| `Deadline` | Local time voting closes, e.g. `11:30`; remove it for no deadline |

The orchestration lives in the host ([TodayPicker.cs](src/LunchSuggest.Web/TodayPicker.cs)) because it combines three modules through their public interfaces.

## "What dish should we eat?"

Each listed place shows its top dishes as chips. Clicking one adds "*dish* at *place*" to today's poll, and votes and the random pick work on those. The 8 places at the top of the list are looked up automatically; the others have a **Find top dishes** button.

A lookup searches SearXNG for `<name> <address> menu`. qwen2.5:3b then picks up to 5 dishes from the top 8 results and copies the names as written (no translation, because the 3B model mistranslated Vietnamese dishes). Output is filtered: the place's own name, menu headings and review sentences are dropped. Lookups run one at a time (about 9 s each on CPU) and are cached for 30 days, shared by everyone.

Quality is good for places with an online menu (Foody, ShopeeFood, Restaurant Guru…) and noisy otherwise. For example, a dish from a different restaurant with a similar name can appear. For better results, point `Dishes:ChatModel` at a bigger Ollama model, or register any other `IChatClient` in `DishesModule.cs`.

## Place data providers

Set `Places:Provider` to one of these:

| Provider | Cost | Coverage | First load of an area |
|---|---|---|---|
| `Overture` (default) | Free, no key | Dense in many countries (Meta, Microsoft and other sources). Around one Hanoi test point: 150+ food places within 300 m | About 25 s (DuckDB reads Overture's GeoParquet on S3) |
| `Osm` | Free, no key | Good in Europe, sparse elsewhere (6 within 300 m at the same Hanoi point) | 5–30 s, and the public server is often busy |
| `Google` | Key with billing | Best | About 1 s, max 20 places per call |

Areas are cached for `Places:CacheDays` (7 by default). If fewer than 20 places fall within the poll's radius, the search widens up to 2 km and keeps the nearest ones.

Overture: the latest release comes from Overture's STAC catalog. Pin a release with `Places:OvertureRelease` (e.g. `2026-10-21.0`) for reproducible results. Overture includes some mis-categorised entries (an apartment block tagged as a restaurant, for example), and only entries with confidence ≥ 0.4 that are still open are kept.

## Google Maps instead

Possible, but **not free without limits**. Places API (New) Nearby Search is a "Pro" SKU: 5,000 free calls/month, then about $32 per 1,000. It needs a billing-enabled Google Cloud project, and returns at most 20 places per call. Areas are cached for `Places:CacheDays`, so each area costs one call per week.

```bash
dotnet user-secrets --project src/LunchSuggest.Web set "Places:GoogleApiKey" "<key>"
dotnet user-secrets --project src/LunchSuggest.Web set "Places:Provider" "Google"
```

The map tiles stay on OpenStreetMap. Google's terms require Google data to be shown on a Google map, so a production Google setup should swap Leaflet for the Maps JavaScript API.

## Vietnamese dish names

Many places only have a name and "restaurant", so "Phở Cội" says nothing an English query can match. Before embedding, `DishGlossary` adds an English gloss of Vietnamese dish words found in the name (phở → noodle soup, lẩu → hot pot, bánh mì → sandwich, chay → vegetarian…). Words are matched with diacritics, so *phố* (street) isn't *phở*. To cover more dishes, add entries to [DishGlossary.cs](src/Modules/Places/Internal/DishGlossary.cs). Places whose text changes are re-embedded automatically.

Measured on ~185 Hanoi places: multilingual models (`bge-m3`, `paraphrase-multilingual`) did **not** fix English→Vietnamese matches on their own ("noodle soup" returned cafés). `nomic-embed-text` + the gloss did, so the default model stays.

## Swapping the embedding model

Any `IEmbeddingGenerator<string, Embedding<float>>` works (OpenAI, Azure OpenAI, ONNX). Change the registration in `SearchModule.cs`. If the vector size isn't 768, update `SearchDbContext.Dimensions`, add a migration, and clear `search."Documents"` so everything gets re-indexed.

## Known limits

- Live updates use an in-process event, so this runs as **one instance**. To scale out, add a backplane (Redis pub/sub or Postgres `LISTEN/NOTIFY`).
- Voter identity is a random id in browser storage, not authentication. Anyone with the link can vote.
- One lunch group per app instance (from settings). Anyone with the `/today` link can vote; there's no sign-in.
- Past days' options, votes and results are kept forever (runner-ups only look back 5 days).
- The daily icon uses `System.Random` seeded with the date, so a different .NET version could pick a different icon for the same day.
- `overpass-api.de` only accepts TLS 1.3, which Windows 10's TLS stack (used by .NET) lacks, so `appsettings.Development.json` uses `http://`. Use `https://` on Linux or Windows 11.
- Place descriptions don't yet include OSM `diet:*` tags, so searches like "vegetarian" are weaker than cuisine searches.
- Vietnamese names without diacritics ("Pho Co 6") or dishes missing from `DishGlossary` get no gloss.
- The dish lookup queue is in memory: queued lookups are lost on restart (users click again). SearXNG's upstream engines can rate-limit; then lookups return no dishes and aren't cached, so they're retried next time.
- A refetch adds or updates places but never removes ones the provider stopped returning (closed places linger until cleaned up).
- The public Overpass server is rate limited. For heavy use, host your own or use a paid provider.
