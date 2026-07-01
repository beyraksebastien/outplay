# Deploying ExpertLapApi — Render (API) + Neon (database)

This is the fully-free stack: Render's free web-service tier (750 instance-hours/month, never
expires) plus Neon's free Postgres tier (never expires, no credit card, scale-to-zero when idle).
This replaces Render's own free Postgres, which auto-deletes after 90 days.

Both services spin down when idle and wake up automatically on the next request (a few seconds of
delay on the first request after a quiet period). Fine for a low-traffic v1 feature.

## 1. Create a Neon account and database

1. Go to https://neon.tech and sign up (GitHub login works, no credit card needed).
2. Create a new project — name it something like `outplay-expert-laps`.
3. Neon creates a default database and gives you a **connection string** immediately on the
   project dashboard, looking like:
   ```
   postgresql://<user>:<password>@<host>.neon.tech/<dbname>?sslmode=require
   ```
4. Keep this tab open — you'll need to convert this into Npgsql's keyword format in step 4 below.

## 2. Create a Render account and connect the repo

1. Go to https://render.com and sign up (GitHub login works, no credit card needed for the free
   tier).
2. **New +** → **Web Service** → connect your GitHub account → select the `outplay` repo.
3. Settings:
   - **Root Directory**: `services/ExpertLapApi`
   - **Environment**: `Docker` (Render should auto-detect the `Dockerfile` in that directory)
   - **Instance Type**: `Free`
4. Don't click "Create Web Service" yet — add the environment variable first (step 4).

## 3. Convert the Neon connection string to Npgsql format

Neon gives you a URL-style string:
```
postgresql://alex:AbC123xyz@ep-cool-name-12345.us-east-2.aws.neon.tech/outplay?sslmode=require
```

Npgsql (the .NET Postgres driver this API uses) wants semicolon-separated keywords instead:
```
Host=ep-cool-name-12345.us-east-2.aws.neon.tech;Port=5432;Database=outplay;Username=alex;Password=AbC123xyz;SSL Mode=Require;Trust Server Certificate=true
```

Mapping: `postgresql://USERNAME:PASSWORD@HOST/DBNAME?sslmode=require` becomes
`Host=HOST;Port=5432;Database=DBNAME;Username=USERNAME;Password=PASSWORD;SSL Mode=Require;Trust Server Certificate=true`

## 4. Set the connection string on Render

Back in the Render web service setup (or **Environment** tab if you already created it):

- Key: `ConnectionStrings__Default` (two underscores — this is how ASP.NET Core maps environment
  variables to nested config keys)
- Value: the Npgsql-format string from step 3.

Click **Create Web Service** (or **Save Changes** if it already exists). Render will build the
Dockerfile and deploy.

## 5. Generate and apply the EF Core migration (one-time, before first real use)

No migration has been generated yet — this repo only has a hand-written DDL fallback in
`Migrations/README.md` for review purposes. On your Windows machine (or any machine with the
.NET 8 SDK):

```powershell
cd services/ExpertLapApi
dotnet tool install --global dotnet-ef   # skip if already installed
dotnet restore
dotnet ef migrations add InitialCreate
```

Then apply it directly to your Neon database using the same connection string from step 3:

```powershell
dotnet ef database update --connection "Host=ep-cool-name-12345.us-east-2.aws.neon.tech;Port=5432;Database=outplay;Username=alex;Password=AbC123xyz;SSL Mode=Require;Trust Server Certificate=true"
```

Commit the generated `Migrations/` folder afterward so it's not regenerated on every machine.

## 6. Verify it's live

Render gives your service a URL like `https://outplay-expert-lap-api.onrender.com`. Test it:

```bash
curl https://outplay-expert-lap-api.onrender.com/api/laps/tracks?sim=iRacing
```

Should return `[]` (empty array) once the migration has been applied and no laps have been
uploaded yet — a `500` error at this point most likely means the migration wasn't applied, or the
connection string is wrong.

## Ongoing costs

- **Neon free tier**: 0.5 GB storage, never expires, no credit card. Scale-to-zero when idle.
- **Render free tier**: 750 instance-hours/month (enough for one service running continuously —
  a month has ~730 hours), never expires. Spins down after 15 min idle, cold-starts on next request.
- **$0/month** as long as usage stays within these limits, which it will for a small side-project
  feature like this.
