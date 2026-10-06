# Gym

A small web app for tracking family gym sessions. It is built for two or more people training side by side, logging every set from one phone or several.

- **Rotation schedules, not calendars.** Days run in order (Day 1, Day 2, Day 3 "Leg Day"...). Finishing a session moves the schedule to the next day. The next day then waits until someone taps **Start session**.
- **Group sessions.** Everyone in a group shares the rotation and the exercises. Each person keeps their own sets, reps, weights and stats. A person can also keep personal schedules for solo workouts.
- **Side-by-side workout screen.** Each exercise shows one column per person, so you can switch who you're logging for at a glance. A set can be logged directly (**✓ Log set**) or timed (**▶ Start** / **■ End set**). After each set, that person's rest timer runs, and the phone vibrates when the rest goal is reached.
- **People without an account.** Kids or guests can be added to a group by name. Each gets a permanent GUID ID and works like anyone else: they can join sessions and get their own history and suggestions. When they want their own login, a group member creates a one-time invite link (valid 14 days). Registering from that link turns the same record into a full account, so their history comes with it. The link skips the email allowlist.
- **Live sync.** Every device viewing a session updates instantly. Any signed-in group member's device can log for everyone in the session.
- **History, reports and trends.** For each exercise you get first time, last time, session count, weight range, estimated 1-rep max, a target streak, a trend chart and full set history.
- **Group progress.** Reports → **Group** shows the whole group together. You get group session counts, everyone's trend line for any exercise on one chart (top weight or estimated 1RM), and a side-by-side table of current weights and suggestions. Each exercise page also has an **Everyone in …** option. Each person keeps the same color everywhere: workout screen, summaries and charts.
- **Reps.** Each exercise page has a **Reps per set** bar chart: one cluster per session with a bar per set, a dashed target line, hollow bars for sets that fell short, faded bars for lighter back-off sets, and the top weight under each session. A badge says whether reps at the current weight are climbing, dropping or steady. Charts and Reports also offer **Avg reps** per working set, including a line per person for the group.
- **Sample data.** The Group page can add 4, 8 or 12 weeks of made-up workouts (4 sessions a week) for everyone in the group and every exercise in its rotation, to try out the charts. Sample sessions are labeled, and one tap removes them all without touching real workouts.
- **"Time to move up?"** A rules engine suggests the next weight per person and exercise. You can tune it in Settings.
- **Exercise help.** Each exercise has a description, muscles and an image. These can be pulled from the free [wger.de](https://wger.de) exercise database. You can embed YouTube videos, and there is always a YouTube search link with the right keywords. With an API key, you can search and save videos inside the app. Any other web page can be saved as a link with an optional title; links show on the exercise page and in the workout screen's help panel.

## Stack

| | |
|---|---|
| App | ASP.NET Core on .NET 10, Blazor Web App (interactive server rendering) |
| Auth | ASP.NET Core Identity (email + password, optional passkeys/2FA from the template) |
| Data | Azure SQL / SQL Server via EF Core (migrations applied on startup) |
| Secrets | Azure Key Vault, read with the App Service managed identity |
| Hosting | Azure App Service (Linux) |
| CI/CD | GitHub Actions, deploying with an App Service publish profile (`.github/workflows/deploy.yml`) |

Live sync uses Blazor Server's SignalR connection and an in-process notifier (`Services/LiveUpdates.cs`). That is right for a single App Service instance. Scaling out to several instances would need Azure SignalR Service.

## One-time Azure setup

The resources already exist. The app needs a few things wired up between them.

### 1. Key Vault secrets (`https://basallo.vault.azure.net/`)

| Secret | Required | Value |
|---|---|---|
| `DbConnectionString` | yes | SQL connection string, e.g. `Server=tcp:<server>.database.windows.net,1433;Database=<db>;User ID=...;Password=...;Encrypt=True;` (or `Authentication=Active Directory Default;` to use the managed identity) |
| `GymAllowedEmails` | recommended | Comma-separated emails allowed to register, e.g. `you@example.com,wife@example.com` |
| `YouTubeApiKey` | optional | A YouTube Data API v3 key. It enables **Find videos here** on exercise pages. Without it, the YouTube search link and pasted links still work. |

The app reads only these named secrets, so it needs **Get** permission on secrets and nothing else.

**About `GymAllowedEmails`:** if it is not set, only the very first account can register, and after that registration closes. This keeps strangers out of a public URL.

### 2. Give the web app access to the vault

1. On the App Service, go to **Identity** and turn **System assigned** on.
2. On the Key Vault, grant that identity the **Key Vault Secrets User** role. With access policies, grant secret **Get** instead.

The vault URL is set in `src/Gym.Web/appsettings.json` (`KeyVault:Uri`). You can override it with an app setting named `KeyVault__Uri`. If you use a user-assigned identity, also set `KeyVault__ManagedIdentityClientId`.

### 3. Database

Allow the App Service to reach the SQL server, either through the SQL firewall ("Allow Azure services") or a VNet. On first start the app creates its tables and seeds a starter exercise library. No manual SQL is needed.

### 4. App Service settings (once)

Blazor Server keeps a live connection open, so in the App Service go to **Configuration → General settings** and set:

- **Stack:** .NET, version **.NET 10**
- **Web sockets:** On
- **Session affinity (ARR affinity):** On
- **Always on:** On
- **HTTPS only:** On (under **Settings → Configuration** or **TLS/SSL settings**)
- Optional: **Health check** path `/healthz`

### 5. GitHub deployment (publish profile)

1. App Service → **Configuration → General settings**: turn **SCM Basic Auth Publishing Credentials** on. Publish profiles don't work without it, and it is off by default on new apps.
2. App Service → **Overview** → **Download publish profile**.
3. GitHub repo → **Settings → Secrets and variables → Actions**:
   - **Secret** `AZURE_WEBAPP_PUBLISH_PROFILE`: paste the entire contents of the downloaded `.PublishSettings` file.
   - **Variable** `AZURE_WEBAPP_NAME`: the App Service name.
4. Optional: create an environment named `production` (**Settings → Environments**) to add required reviewers before each deploy. The secret can live there instead of at repository level.

The workflow (`.github/workflows/deploy.yml`):

| Trigger | Build + test | Deploy |
|---|---|---|
| Pull request to `main` | yes | no |
| Push to `main` | yes | yes, if `AZURE_WEBAPP_NAME` is set |
| Manual run (**Actions → Build and deploy → Run workflow**) on `main` | yes | yes, if "deploy" is ticked and `AZURE_WEBAPP_NAME` is set |

If the variable is missing, the deploy job is skipped. If the variable is set but the secret is missing, the deploy job fails with a message saying what to add. If you reset the publish profile in Azure, download it again and update the secret.

## Running locally

Requires the .NET 10 SDK.

```bash
cd src/Gym.Web
dotnet run
```

Development mode uses a local SQLite file (`gym-dev.db`) and skips Key Vault, so no setup is needed. Open the printed URL and register; the first account is always allowed. To register more local accounts, list them first:

```bash
dotnet user-secrets set "Registration:AllowedEmails" "you@example.com,wife@example.com"
```

Optional:

```bash
dotnet user-secrets set "YouTube:ApiKey" "<key>"
```

To run against SQL Server locally instead, set `Database:Provider` to `SqlServer` and `ConnectionStrings:Default`, either in user secrets or as environment variables.

Tests:

```bash
dotnet test
```

## Changing the database model

Edit the entities in `src/Gym.Web/Data`, then create a SQL Server migration:

```bash
./scripts/add-migration.sh AddSomething
```

Tables are never schema-qualified: no `dbo.`, no default schema. SQL Server creates them in the default schema of the database user in `DbConnectionString`, and that user needs permission to create tables there. `SchemaTests` fails the build if a schema sneaks into the model or the migrations.

Commit the generated files. The deployed app applies pending migrations when it starts. To manage migrations yourself, set `Database:InitializeOnStartup=false`.

## How the progression rules work

For each person and exercise, the engine looks at completed sessions, newest first. A session **hit targets** when every target set reached the target reps at that session's top weight. The rules run in order, and the first one that applies wins:

1. **First time.** There is no history yet, so find a working weight.
2. **Layoff.** If it's been at least *Layoff days* since the exercise was last done, suggest *Deload %* lighter.
3. **Move up.** If targets were hit in *Sessions to move up* sessions in a row at the same weight, add the increment. Upper body, lower body and core each have their own increment, and any single exercise can override it.
4. **Deload.** If targets were missed in *Misses before deload* sessions in a row at the same weight, drop *Deload %*.
5. **Hold.** Otherwise, stay at the same weight.

Each person tunes the numbers in **Settings**. Suggestions are rounded to the *Round to* step, which should match the smallest plate or dumbbell jump available.

Rules live in `src/Gym.Web/Services/Progression.cs`. Each rule implements `IProgressionRule`, so adding a new one, such as rep-range progression, means one class plus a line in `ProgressionEngine.Default`.

## Project layout

```
src/Gym.Web/
  Data/           EF Core entities, DbContext, seed exercises, migrations
  Services/       Groups, schedules, sessions, stats and the progression engine, wger + YouTube clients
  Components/
    Pages/        Today, Session (workout screen), Schedules, Exercises, Reports, Group, Settings
    Shared/       Person column, trend chart, video embed, exercise picker, session summary
    Account/      ASP.NET Core Identity UI (from the template)
tests/Gym.Tests/  Progression rules, stats, helpers, and session flows on in-memory SQLite
```

## Ideas for later

- Import past workouts from a spreadsheet (CSV: date, person, exercise, set, reps, weight).
- Rep-range ("double progression") rules and per-exercise rule overrides.
- Bodyweight and cardio exercise types.
- Offline logging with background sync.
