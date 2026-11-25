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
2. **"➕ Create new poll", one poll a day.** Anyone who has entered a name can create a poll. Before the deadline it's for **today**; after the deadline it's for **the next weekday**. **Polls run Monday to Friday only:** after Friday's deadline, or any time on Saturday or Sunday, the button makes Monday's poll, and a weekend `/today` shows "No lunch poll on weekends". The polls that can still change are today's and the next weekday's (so Monday's can be voted on from Friday afternoon). **Public holidays** (Settings → Poll → *Public holidays*, dates like `24/11/2026`) are skipped the same way: no poll that day, `/today` shows "Public holiday: no lunch poll", and the button jumps to the next working day. The rule is `PollDays` in [VotingModule.cs](src/Modules/Voting/VotingModule.cs); the holidays are stored on the group row. If that day already has a poll, the button becomes "Open tomorrow's poll →" (one poll per day is enforced by the database key too). Polls live at `/today` and `/poll/yyyy-MM-dd`. Tomorrow's poll can be voted on until tomorrow's deadline, and past days show their result.
3. **A new poll's picks.** Right after creation the poll gets 3–5 dishes (with a spinner meanwhile): recent runner-ups (up to `count - 1`) plus random dishes from random nearby places. Places with nothing found online go in as the place itself. Lookups for these go ahead of the list's background lookups. The first time in a new area takes about a minute, then it's mostly cached.
4. **Deadline (11:30 by default).** The page counts down ("⏰ Voting closes at 11:30 · 25 min left") and switches to the result at the deadline without a reload. After it, the server rejects adding options, votes and random picks. Anyone opening `/today` later sees only the result: a large icon for the dish (🍜 phở/bún, 🍚 cơm, 🍲 lẩu, 🥖 bánh mì…, see [DishIcon.cs](src/LunchSuggest.Web/DishIcon.cs)), where to eat, why it won, a Google Maps directions link and the runner-up. The winner is a random pick if someone made one, else the most votes. A tie, or a day with no votes, is settled once by the dice and saved, so everyone sees the same result. A day without a poll shows "No poll for today yet" and the create button.
5. **Results are stored.** As soon as a poll has closed (for example when tomorrow's poll is created at 2 PM), its **winner** and **runner-up** are saved in `voting."DayResults"` with their vote counts, so tomorrow's picks already include today's runner-up. The winner is the random pick, or else the most-voted option. The runner-up is the most-voted other option (at least 1 vote). The next days' picks take runner-ups from that table: last 5 days, newest first, skipping anything the group has eaten since. Logic: `LunchSessions.Outcome` / `PickRunnerUps`, unit tested.

## Names and languages

- **Voter names are random**, e.g. *Chilling Capybara*, or in Vietnamese *Chuột Lang Nước Thư Thái*: Vietnamese puts the animal first, English the personality. A name is created on the first visit, kept in the browser, and can be re-rolled with **🎲 New name** until the first vote. Names are saved on the server too (`voting."Voters"`, unique), so no two people share one: a name that's taken is re-rolled. The first vote fixes the name, and every later vote by that browser uses the server's copy.
- **One vote per person per poll.** Voting for another option moves the vote there; pressing **Voted** again takes it back. **🎲 Random pick** also casts the picker's vote for the pick (moving any earlier vote). Under the poll, **🙋 Voters (n):** lists everyone who has voted. Votes, picks and new options show up on every open page right away, without a reload (Blazor Server's SignalR connection).
- **Map colors:** grey = nearby place, blue = poll option, green = the option you voted for, red = the random pick, orange = the place you clicked (📍 name) in the list; click it again to clear. Lists: [VoterNames.cs](src/LunchSuggest.Web/VoterNames.cs).
- **Vietnamese by default**, with a Tiếng Việt / English switch in the header (saved in a cookie for a year). The browser's language is ignored on purpose.
- Text lives in `L["English text"]` calls; the English text is the key, so only [Resources/SharedResource.vi.resx](src/LunchSuggest.Web/Resources/SharedResource.vi.resx) is needed. A unit test fails if any page uses text with no Vietnamese entry, if a translation drops a `{0}` placeholder, or if two keys differ only by upper/lower case (.NET resources would mix those up). Dates use translated patterns too (`Thứ Ba, 17/11`).
- To add a language: add it to `SharedResource.Cultures`, add a `SharedResource.<lang>.resx`, and add a button in `MainLayout.razor`.

## Suggesting places and dishes

The map data misses places, and the dish lookup misses dishes, so people can add them. Both wait for your approval.

- **➕ Suggest a place** (above the place list):
  - Click the map where the place is (a purple pin; click again to move it), type its name, and optionally the cuisine, address and a few good dishes separated by commas.
  - It must be within the walking radius, or it would never show up in the list.
- **＋ Suggest a dish** (under any place): type one dish. Drinks are refused (food only).
- **🚫 Suggest a removal** (under any place): pick the whole place (closed, wrong, not a food place) or one of its dishes. Once approved it disappears for everyone: from the nearby list, the search and the app's own picks. Options already in a poll stay there.
- **Who can suggest:** the same people who can vote. With sign-in on, that's signed-in, allowed people.
- **Review** under **Suggested places and dishes** on the Settings page:
  - **Approve** or **Reject** each suggestion. Approving a place also approves the dishes sent with it.
  - **Remove** takes an approved one away again; **Put back** undoes an approved removal.
  - For dishes the latest approved decision wins: approving "add Phở gà" cancels an earlier approved "remove Phở gà", and the other way round.
  - The review lists (suggestions and **Who can vote**) update live: new suggestions and sign-ins appear without reloading Settings.
  - They're a small interactive part (`ReviewQueue.razor`, over Blazor's SignalR connection) inside the otherwise static Settings page. Only this computer is served that page, so no other browser can start it.
- **Once approved:**
  - A place joins the nearby list for everyone (with a 👥 *suggested* badge), the semantic search and the app's own picks. Pages already open show it right away.
  - A dish comes first in that place's dish list and shows on open pages right away.
- **Where it's stored:**
  - Places: `places."SuggestedPlaces"`. They stay when you change the place provider, and are never touched by the provider cache.
  - Dishes: `dishes."SuggestedDishes"`. They don't expire like the 30-day lookup cache.
  - Every suggestion is logged with who sent it.

## Sign-in (Google)

Off by default: anyone with the link votes under a random name. Turn it on when the link gets shared in a big group chat and people who aren't coming to lunch start voting. Then:

- **Anyone with the link can still view** the poll, the map, the places and the results. They see **Sign in with Google** instead of working buttons.
- **After signing in, people wait for you.** Everyone who signs in shows up under **Who can vote** on the Settings page (this computer only) with their Google name and e-mail. Click **Allow voting** once per person; their open page unlocks at once. **Stop voting** takes it back, and that takes effect on their next click.
- **Regulars skip the wait.** `Google:AutoApprove` in [appsettings.json](src/LunchSuggest.Web/appsettings.json) lists names that are allowed on their first sign-in. A name matches the Google display name, ignoring accents, case and word order: *Dao Dinh Luyen* and *Luyện Đào Đình* both match *Đào Đình Luyện*. This only happens on the first sign-in, so **Stop voting** on the Settings page sticks. Anyone can set any display name on their Google account, so check new names in **Who can vote** now and then. Edit the list and run `docker compose up -d --build web`; it doesn't approve accounts that have already signed in. The list is temporary: switch it **Active / Disabled** under **Who can vote** on the Settings page, with no redeploy. While it's disabled, everyone waits for approval; people already allowed keep voting.
- **Only allowed people can vote, add options, use 🎲 Random pick or create a poll.** The server checks this for every action, not just the disabled buttons.
- **The voter name is the Google display name** and can't be changed (no 🎲 New name). If someone else already has that name, it becomes "Name 2". E-mails are only shown on the Settings page, never on polls.
- Sign-in lasts 30 days on that browser. **Sign out** signs out of this site only, not out of Google.

To turn it on:

1. **HTTPS via ngrok.** Google doesn't accept a sign-in return address like `http://10.3.0.54:8686`, so the site needs HTTPS. The `ngrok` service in `docker-compose.yml` gives it a fixed public HTTPS address.
   - Sign up at ngrok.com (the free plan is enough).
   - Copy **Your Authtoken**, and claim your free **static domain** under Domains, e.g. `lunch-xyz.ngrok-free.app`.
2. **Google OAuth client.** In Google Cloud Console (any Google account works):
   1. Create a project.
   2. Set up the **OAuth consent screen**: External, an app name, your e-mail.
   3. Click **Publish app** so it's *In production*. In *Testing* mode, only test users you list can sign in. The app only asks for name and e-mail, so Google doesn't need to verify it.
   4. Under **Credentials**, create an OAuth client ID of type *Web application* with the authorized redirect URI `https://<your ngrok domain>/signin-oidc`.
3. **Fill in `.env`.** Copy [.env.example](.env.example) to `.env` (git ignores it) and fill in the ngrok and Google values. Then run `docker compose up -d`; `COMPOSE_PROFILES=ngrok` in `.env` starts the tunnel with the site.
4. **On the Settings page** (http://localhost:8690/settings):
   - Set **Share link address** to `https://<your ngrok domain>`. Everyone must use that address: signing in from `http://10.3.0.54:8686` fails, because Google only sends people back to the registered address.
   - Sign in yourself on the poll page, then allow yourself under **Who can vote**.

If `ClientId` is set but `ClientSecret` is missing, the app refuses to start. For `dotnet run`, set the environment variables `Google__ClientId` and `Google__ClientSecret`.

About ngrok:
- **Don't start the tunnel before the Google values are in `.env`.** Without sign-in, anyone on the internet could vote.
- **The poll becomes reachable from the internet.** Anyone with the link can view it, but only people you allow can vote.
- **Settings stays on this computer only.** It runs on port 8081, which the tunnel doesn't forward.
- **Free ngrok shows a "You are about to visit…" page** the first time each browser opens the link; click **Visit Site**.
- **Google returns people to the site with a plain GET**, not a form POST, so that page can't interrupt sign-in.
- **The app trusts the tunnel's `X-Forwarded-Proto` header** (only the scheme), so it knows it's being served over HTTPS.
- **The free plan has monthly traffic limits.** Map tiles don't go through the tunnel.

Votes cast anonymously before sign-in was turned on keep their random names. Code: [GoogleSignIn.cs](src/LunchSuggest.Web/GoogleSignIn.cs); approvals are the `Approved`/`Email` columns of `voting."Voters"`.

## Settings

Edit everything on the **Settings page**. Saving takes effect at once; pages that are already open pick up changes after a reload.

| Section | Setting | Default |
|---|---|---|
| Lunch group | Group name, address, latitude / longitude, walking radius (100–5000 m), voting deadline, time zone | Sport Hotel, 300 m, 11:30, `Asia/Ho_Chi_Minh` |
| Display | **Site title**: top bar and browser tab | LunchSuggest |
| Display | **Landing button icon**: one emoji, or empty for a different food icon every day | empty (daily) |
| Display | **Share link address**: what the invite box and "Copy invite link" use, plus the page path. Needed because the app can't see the LAN IP from inside Docker. Empty = the viewer's own address | `http://10.3.0.54:8686` (from `docker-compose.yml`) |
| Poll | **Max options per poll** (2–20), including the app's picks | 8 |
| Poll | **Dishes the app adds to a new poll**, at least / at most (0–8; 0 = start empty). Each new dish can take ~10 s to look up | 3–5 |
| Poll | **Runner-up look-back days** (0 = off) | 5 |

- **Docker:** http://localhost:8690/settings, **only from this computer**. Port 8690 is published on the host's `127.0.0.1` only, so LAN machines can't connect to it, and `/settings` on the shared port 8686 answers 404 for everyone. A port is used rather than an IP check because Docker Desktop hides callers' real IPs from the container. `Admin:Port` (8081 inside the container) turns this on; see `docker-compose.yml`.
- **`dotnet run`:** http://localhost:5063/settings, only from loopback addresses (127.0.0.1 / ::1).
- The page is a plain server-rendered form with an antiforgery token (`[ExcludeFromInteractiveRouting]`), so every view and save is a real request that goes through the check. The rule lives in [AdminAccess.cs](src/LunchSuggest.Web/AdminAccess.cs) and is unit tested.

The `Lunch` section of [appsettings.json](src/LunchSuggest.Web/appsettings.json) only gives the **first** values, used when the group is created. Default: **Khách sạn Thể Thao (Sport Hotel), 15 Lê Văn Thiêm, Thanh Xuân, Hà Nội**, 300 m, `Asia/Ho_Chi_Minh`, deadline 11:30. After that the database is the source of truth, and editing the file doesn't override the Settings page.

The poll heading shows how full it is (*6/8*), and the add buttons are disabled once it reaches the max options. Lowering the max doesn't remove options from a poll that already has more.

**Reset a poll** (bottom of the Settings page) deletes today's or tomorrow's poll with all its options, votes and random pick, after a browser confirmation. Pages that have it open switch to "no poll yet", and anyone can create a fresh one. **Only open polls can be reset:** once the deadline has passed, the poll and its result stay in history (the Settings page shows 🔒 instead of the button), and older polls are history too, since runner-ups come from it. Every reset is logged as a warning with the day and how many options and votes were deleted (`docker compose logs web | grep reset`); the log starts over when the container is recreated.

| Key | Meaning |
|---|---|
| `Code` | Internal id of the group (keep it, or a new empty group starts) |
| `Title`, `Address` | Shown on the poll page |
| `Lat`, `Lng`, `RadiusMeters` | Meeting point and walking radius (100–5000 m) |
| `TimeZone` | IANA id; when the day (and the icon) changes |
| `Deadline` | Local time voting closes, e.g. `11:30`; remove it for no deadline |

The orchestration lives in the host ([TodayPicker.cs](src/LunchSuggest.Web/TodayPicker.cs)) because it combines three modules through their public interfaces.

## "What dish should we eat?"

**Poll history** (`/history`, the 📜 link on the poll page) lists every poll newest first: date (links to that day's poll), winner with an icon and why it won, runner-up, and who voted. Open polls show "Voting open". Opening it settles any unsettled past day first, like the poll page does, so both show the same winner.

**Food only.** Drink-only places (cafés, coffee and bubble-tea shops, juice and wine bars: `FoodCategory.IsDrinkOnly`) and drinks in dish lists (cà phê…, trà…, bia…, …coffee: `DishExtractor.IsDrink`) are left out, filtered on read so cached data obeys too. Restaurants with "Beer" or "Cafe" in their name that serve meals stay.

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
