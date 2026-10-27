# AIPMS.Net

ASP.NET Core 10 (Minimal API) conversion of AIPMS. The vanilla-JavaScript SPA in `wwwroot/` talks to the `/api/...` endpoints in `Program.cs`.

## Run (Windows PowerShell)

```powershell
cd path\to\AIPMS.Net-main
dotnet run
```

Open http://localhost:5180 (see `Properties/launchSettings.json`).

The SQLite database is created at `App_Data\aipms.db` on first run and seeded with demo data. Resume uploads go to `App_Data\uploads`.

### Upgrading from an earlier build
Earlier builds passed the database path to SQLite in the wrong encoding, so the data lived in files named after the drive letter (`C`, `D`, plus `-wal`/`-shm`) in the project folder instead of `App_Data\aipms.db`. On the first start without `App_Data\aipms.db`, the app **imports the newest of those files automatically** (the log prints `Imported existing data from legacy database file ...`). The old files are never modified or deleted; once you have confirmed your data, you can archive them yourself.

## Architecture
- `Program.cs` - routes, request validation, central JSON error handler.
- `Services.cs` - `AipmsStore` (see below), password hashing (PBKDF2), HS256 tokens, resume scoring, PDF/DOCX text extraction, seed data.
- `Models.cs` - domain models and request DTOs.
- `wwwroot/` - SPA (`js/api.js` is the fetch wrapper; role screens in `student.js`, `recruiter.js`, `tpo.js`, `admin.js`).

### How writes work (`AipmsStore.Write`)
Each write runs on a private copy of the state. Only if it finishes without an exception **and** without returning an HTTP error result is the copy committed to SQLite in a single transaction and then published. A failed request therefore changes nothing, in memory or on disk, so what the API reports always matches what is stored.

## Tests
```powershell
# 1. Store/auth unit tests (no packages needed)
dotnet run --project tests\AIPMS.Net.Tests

# 2. End-to-end API tests. Start the app on a THROW-AWAY copy first (they create records):
#    set ASPNETCORE_CONTENTROOT to an empty folder, or run from a fresh clone.
python tests\api_tests.py http://localhost:5180 path\to\that\App_Data\aipms.db

# 3. Headless UI smoke test
cd tests; npm install jsdom; cd ..
node tests\ui_smoke.js http://localhost:5180
```

## Demo credentials
- Admin: `admin@aipms.local` / `Admin@123`
- TPO: `tpo@aipms.local` / `Tpo@1234`
- Recruiter: `hr@technova.com` / `Recruit@123`
- Student: `aditi.rao@student.aipms.local` / `Student@123`

Change these and set the `AIPMS_TOKEN_KEY` environment variable to a long random secret before deploying (the app logs a warning while the built-in development key is in use).