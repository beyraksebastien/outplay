using System.Text.Json;
using System.Threading.RateLimiting;
using ExpertLapApi.Data;
using ExpertLapApi.Models;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration: connection string comes from config/env, never hardcoded. ---
// Deployment (e.g. Render.com): set the environment variable
//   ConnectionStrings__Default=Host=...;Port=5432;Database=...;Username=...;Password=...;SSL Mode=Require
// ASP.NET Core's configuration system maps ConnectionStrings__Default (double underscore) to
// configuration key "ConnectionStrings:Default" automatically — this is standard .NET config
// binding behavior (env vars use __ in place of the : section separator), not something specific
// to this project's setup.
var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:Default. Set the ConnectionStrings__Default environment " +
        "variable (or appsettings.json's ConnectionStrings:Default for local dev) to a Postgres " +
        "connection string before starting this service.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

// --- Abuse guardrail 1: request body size cap (application-level, in addition to any limit the
// hosting platform/reverse proxy applies). 2 MB comfortably covers a multi-minute lap trace at a
// coarse (few-Hz) sample rate encoded as JSON, while still bounding worst-case payload size for an
// endpoint with no auth to rate-limit misbehaving callers by identity.
const long MaxRequestBodyBytes = 2 * 1024 * 1024;
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = MaxRequestBodyBytes);

// --- Abuse guardrail 2: fixed-window rate limiting per client IP on the upload endpoint. This is
// the one-line-ish built-in ASP.NET Core rate limiter (Microsoft.AspNetCore.RateLimiting,
// in-box since .NET 7) — deliberately not a custom/external solution, per the "no accounts, keep
// v1 minimal" brief. Partitioned by remote IP so one abusive source is limited without penalizing
// everyone else; this is easy to spoof/share (NAT, proxies) and is NOT a real anti-abuse
// guarantee — see risk notes in the final report.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("upload", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});

var app = builder.Build();

app.UseRateLimiter();

// --- App-level trace size cap (separate from the raw byte cap above): reject an absurd sample
// count even if it fits under the byte limit (e.g. a pathological trace with tiny numeric values
// but an enormous sample array). A real lap at, say, 20 Hz for a 3-minute lap is ~3,600 samples;
// 20,000 gives generous headroom for longer laps/higher sample rates without allowing an
// effectively-unbounded array.
const int MaxTraceSamples = 20_000;
const double MinLapTimeSec = 20.0;
const double MaxLapTimeSec = 600.0;

app.MapPost("/api/laps", async (UploadLapRequest request, AppDbContext db) =>
{
    // --- Validation (no auth to lean on, so this endpoint has to be defensive on its own). ---
    if (string.IsNullOrWhiteSpace(request.Sim))
        return Results.BadRequest("Sim is required.");
    if (string.IsNullOrWhiteSpace(request.Track))
        return Results.BadRequest("Track is required.");
    if (string.IsNullOrWhiteSpace(request.Car))
        return Results.BadRequest("Car is required.");
    if (request.LapTimeSec is < MinLapTimeSec or > MaxLapTimeSec)
        return Results.BadRequest($"LapTimeSec must be between {MinLapTimeSec} and {MaxLapTimeSec} seconds.");
    if (request.TelemetryTrace is null || request.TelemetryTrace.Count == 0)
        return Results.BadRequest("TelemetryTrace must contain at least one sample.");
    if (request.TelemetryTrace.Count > MaxTraceSamples)
        return Results.BadRequest($"TelemetryTrace exceeds the maximum of {MaxTraceSamples} samples.");
    if (request.SubmitterLabel is { Length: > 64 })
        return Results.BadRequest("SubmitterLabel must be 64 characters or fewer.");

    var sim = request.Sim.Trim();
    var track = request.Track.Trim();
    var car = request.Car.Trim();

    var traceJson = JsonSerializer.Serialize(request.TelemetryTrace);

    // Storage decision (documented in Models/ExpertLap.cs): only the current fastest lap per
    // (Sim, Track, Car) combo is served as "best". Every upload is still persisted, but only
    // promoted to IsCurrentBest = true if it beats (or is the first lap for) that combo.
    var existingBest = await db.ExpertLaps
        .Where(l => l.Sim == sim && l.Track == track && l.Car == car && l.IsCurrentBest)
        .FirstOrDefaultAsync();

    var isNewBest = existingBest is null || request.LapTimeSec < existingBest.LapTimeSec;

    var lap = new ExpertLap
    {
        Id = Guid.NewGuid(),
        Sim = sim,
        Track = track,
        Car = car,
        LapTimeSec = request.LapTimeSec,
        SubmitterLabel = string.IsNullOrWhiteSpace(request.SubmitterLabel) ? null : request.SubmitterLabel.Trim(),
        TelemetryTraceJson = traceJson,
        IsCurrentBest = isNewBest,
    };

    if (isNewBest && existingBest is not null)
    {
        existingBest.IsCurrentBest = false;
    }

    db.ExpertLaps.Add(lap);
    await db.SaveChangesAsync();

    return Results.Created($"/api/laps/{lap.Id}", new UploadLapResponse(lap.Id, isNewBest));
})
.RequireRateLimiting("upload");

app.MapGet("/api/laps/best", async (string sim, string track, string car, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(sim) || string.IsNullOrWhiteSpace(track) || string.IsNullOrWhiteSpace(car))
        return Results.BadRequest("sim, track, and car query parameters are all required.");

    var lap = await db.ExpertLaps
        .Where(l => l.Sim == sim && l.Track == track && l.Car == car && l.IsCurrentBest)
        .FirstOrDefaultAsync();

    if (lap is null) return Results.NotFound();

    var trace = JsonSerializer.Deserialize<List<TelemetryTraceSample>>(lap.TelemetryTraceJson) ?? new();

    return Results.Ok(new BestLapResponse(
        lap.Id, lap.Sim, lap.Track, lap.Car, lap.LapTimeSec,
        lap.UploadedAtUtc, lap.SubmitterLabel, trace));
});

app.MapGet("/api/laps/tracks", async (string sim, AppDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(sim))
        return Results.BadRequest("sim query parameter is required.");

    var combos = await db.ExpertLaps
        .Where(l => l.Sim == sim && l.IsCurrentBest)
        .Select(l => new TrackCarCombo(l.Track, l.Car, l.LapTimeSec))
        .ToListAsync();

    return Results.Ok(combos);
});

app.Run();

// --- Request/response DTOs (kept in Program.cs since this is a small, single-file minimal API —
// splitting into separate files is a low-value reorg for a project this size). ---

/// <summary>POST /api/laps request body.</summary>
public sealed record UploadLapRequest(
    string Sim,
    string Track,
    string Car,
    double LapTimeSec,
    string? SubmitterLabel,
    List<TelemetryTraceSample> TelemetryTrace);

public sealed record UploadLapResponse(Guid Id, bool IsCurrentBest);

public sealed record BestLapResponse(
    Guid Id,
    string Sim,
    string Track,
    string Car,
    double LapTimeSec,
    DateTime UploadedAtUtc,
    string? SubmitterLabel,
    List<TelemetryTraceSample> TelemetryTrace);

public sealed record TrackCarCombo(string Track, string Car, double BestLapTimeSec);
