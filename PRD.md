# Outplay AI — Product Requirements Document
### The AI Race Engineer That Never Sleeps

**Version:** 2.0 (refined)
**Status:** Draft for scoping review
**Owner:** TBD

---

## 1. Vision

Outplay AI is an intelligent race engineer and driving coach that makes sim racers faster every session. Instead of dumping telemetry graphs on the driver, it explains *why* time was lost, *what* to change on the next lap, and builds a long-term plan to fix the underlying weakness.

Outplay does not aim to replace telemetry tools. It aims to replace the need for a human coach for the majority of drivers.

**Positioning:** an always-available race engineer, not another telemetry viewer.

> Instead of: "You braked too early."
> Outplay says: "You lost 0.18s because you released the brake 12m too soon, forcing an earlier throttle pickup. Here's the ideal input and a drill to fix it."

Every insight must be actionable — a number, a cause, and a next step.

---

## 2. North Star Metric

**Average lap-time improvement per driver over 30 days.**

Secondary metrics:
- Consistency improvement (stddev of lap time)
- Weekly practice-plan completion rate
- Session retention (D7/D30)
- iRating / Safety Rating trend
- Race finish position improvement
- Incident rate reduction

---

## 3. Target Users

| Segment | Core problem | Needs |
|---|---|---|
| Beginner | "I'm 4s off pace" | Racing line, braking points, confidence, basic racecraft |
| Intermediate | "I'm stuck at ~2k iRating" | Consistency, trail braking, tire management, race starts |
| Advanced | "I'm fighting for podiums" | Marginal telemetry gains, setup tuning, race strategy, mental game |
| Professional/Team | Structured performance ops | Team telemetry, driver comparison, race engineering, fuel/tire strategy, AI simulation |

---

## 4. Scope Note

The original concept list spans 17 feature areas — a multi-year product surface, not a v1. This PRD splits them into a **buildable core (v1)**, a **near-term expansion (v1.x/v2)**, and a **long-term vision (v3+)**, ordered by (a) dependency (later features need data/infra earlier features produce) and (b) proximity to the core value prop: *turn a lap into a specific, actionable fix*.

---

## 5. V1 — Core Loop (MVP)

The v1 goal: prove the north star metric moves. Everything here exists to support one loop — **drive → get a specific correction → drive again, better.**

### 5.1 Live Telemetry Ingestion
Read brake pressure, throttle, steering, speed, gear, delta, tire temps/wear, fuel, slip angle from the sim in real time.
- **Depends on:** per-sim telemetry API/UDP integration (see §9).
- **Acceptance:** telemetry sampled at ≥60Hz with <50ms latency, persisted per-lap.

### 5.2 Corner Intelligence™ (single-track, single-driver)
Segment each lap by corner; score braking, apex, throttle, steering, exit speed, and consistency per corner against the driver's own best lap and (later) an optimal reference.
- **Acceptance:** for any completed lap, driver can see which corner(s) cost the most time vs. their PB, in seconds.

### 5.3 AI Race Engineer — post-session text/voice feedback
Natural-language, corner-specific coaching generated after a session (not yet live in-car). Example: "You lost 0.18s in Turn 4 — brake release was 12m early."
- **Depends on:** 5.1, 5.2.
- **Acceptance:** every session produces at least 3 specific, quantified corrections tied to a corner and a metric delta.

### 5.4 Race Debrief
Post-session summary: biggest mistake, biggest improvement, best/worst corner, one concrete action item for the next session.
- **Depends on:** 5.2, 5.3.

### 5.5 AI Replay Studio (conversational, text-first)
Chat interface over session data: "Why was Lap 8 my fastest?", "Compare Lap 3 to Lap 11."
- **Depends on:** 5.1, 5.2.

### 5.6 Ghost Racing vs. Personal Best
Overlay current lap against the driver's own fastest lap (line, brake trace, throttle trace, steering). World record / AI-optimal ghosts are v1.x once enough cross-driver data exists.

### 5.7 Lap DNA™ (basic profile)
Aggregate braking/throttle/steering tendencies across sessions into a driver profile that biases future coaching (e.g., consistently late on trail-brake release → prioritize that drill).
- **Depends on:** 5.1–5.3 running across multiple sessions.

### 5.8 Sim Coverage (v1)
Launch with **iRacing** and **F1 25** — the pairing chosen to match trophi.ai's core coverage while keeping v1 to two integrations. iRacing has the more mature telemetry surface (shared memory, per-corner slip angle/tire data); F1 25's UDP telemetry broadcast is coarser (no slip angle, simplified tire model) and its packet format has shifted release-to-release, so budget time to re-validate against the live game before launch. ACC remains a strong v1.x add since it's also in trophi.ai's lineup and has a more complete telemetry feed than F1 25. See §9 and §13.2.

### V1 explicitly excludes
Live in-car voice coaching, setup AI, race strategist, spotter, mistake prediction, coach personalities, driver progression/leveling, career timeline, multiplayer/racecraft intelligence, team dashboards, mobile app. These require either live-audio infra, cross-session longitudinal data, or a critical mass of users that v1 doesn't have yet.

---

## 6. V1.x / V2 — Near-Term Expansion

Ordered roughly by build order, each depending on v1 infra:

1. **Live AI Race Engineer (voice, in-session)** — turns the post-session text coach (5.3) into real-time short voice callouts ("brake 5m deeper," "use more curb"). Requires low-latency voice synthesis and a much tighter false-positive bar than post-session text, since bad live callouts are actively distracting.
2. **Mistake Prediction** — predictive callouts ("you'll understeer here") built on the Corner Intelligence scoring engine plus the driver's Lap DNA history at that corner.
3. **Dynamic Coaching Modes** — coaching style adapts to context (learning / plateaued / qualifying / racing / wet). Reuses the same engine as #1 with different verbosity/threshold presets — not a new system.
4. **AI Practice Builder** — generates a daily practice plan from the weakest Corner Intelligence scores and Lap DNA gaps.
5. **Driver Progression System** — skill levels (trail braking, rotation, consistency, etc.) computed from the same per-corner scoring data already collected in v1; this is a UI/gamification layer, not new data infra.
6. **AI Career Timeline** — longitudinal trend reporting ("+1.8s at Spa since January") once ≥3 months of session history exists per driver.
7. **Ghost vs. World Record / AI-optimal lap** — needs a critical mass of cross-driver lap data per track to compute a meaningful "optimal" reference.
8. **Coach Personalities** — different tone/persona wrapping the same underlying coaching content (§6.1, 6.3). Low technical risk, deprioritized because it's polish, not lap-time impact.
9. **Setup AI** — driver describes a handling issue ("oversteers on exit"), AI recommends setup changes with rationale. Independent subsystem; can be built in parallel to the coaching track once car/setup schemas per sim are modeled.
10. **Sim expansion** — add Assetto Corsa EVO, Le Mans Ultimate, Automobilista 2, rFactor 2 as telemetry integrations mature (see §9).

---

## 7. V3+ — Long-Term Vision

Higher complexity, higher dependency on scale, live multiplayer data, or team infrastructure:

- **AI Race Strategist** — pre-race predictions (tire wear, fuel, pit windows, weather, SC probability) and continuous in-race strategy updates. Requires race-length telemetry, weather data feeds, and multi-car field awareness.
- **AI Spotter** — live positional awareness ("car inside," "three wide," "rain in 5 minutes"). Needs live multi-car proximity data and very low latency; safety-relevant, needs a high reliability bar before shipping.
- **Multiplayer Intelligence / Racecraft Profile** — overtakes, defending, incident history, first-lap survival, SR trends, built from race-session data at scale.
- **Team tier** — multi-driver dashboards, shared telemetry, league analytics, coach portal, team strategy. Depends on Pro-tier product being proven first.
- **Mobile companion app** — session review, AI debrief playback, telemetry animations, remote setup building.
- **Additional sims** — Gran Turismo, F1 series, RaceRoom, BeamNG, Assetto Corsa (legacy), gated on platform telemetry access.

---

## 8. Engineering Requirements

### 8.1 Data Model (v1 minimum)
- `Driver` — id, sim account links, skill profile (Lap DNA)
- `Session` — id, driver_id, sim, track, car, conditions, timestamp
- `Lap` — id, session_id, lap_time, valid/invalid, delta_to_pb
- `TelemetrySample` — lap_id, timestamp_offset, speed, throttle, brake, steering, gear, tire_temp[4], tire_wear[4], fuel, slip_angle
- `CornerScore` — lap_id, corner_id, braking_score, apex_score, throttle_score, steering_score, exit_speed, time_delta_to_best
- `Insight` — session_id or lap_id, text, corner_id (nullable), metric_delta, category (braking/throttle/line/consistency)
- `PracticePlan` (v1.x) — driver_id, generated_date, drills[]

### 8.2 Per-Sim Integration Requirements
Each sim integration must define:
- Telemetry transport (UDP/shared memory/REST) and sample rate
- Available channels (confirm slip angle, tire temp, and fuel are exposed — not all sims expose all channels)
- Track/corner reference data (corner boundaries per track layout, needed for Corner Intelligence segmentation)
- Auth/account linking method

iRacing and ACC both expose sufficient telemetry via documented SDKs/shared memory; this is why they're the v1 pick over the others (Le Mans Ultimate and AC EVO are newer with less mature/stable telemetry APIs as of this writing — verify before committing engineering time).

### 8.3 Latency & Reliability Targets
- Telemetry capture: <50ms end-to-end latency, no dropped samples above 1% per session
- Post-session insight generation: available within 30s of session end
- Live voice coaching (v1.x+): <300ms from trigger event to audio output, since anything slower arrives after the corner is gone

### 8.4 Corner Segmentation — open technical question
Corner boundaries must be defined per track layout before Corner Intelligence can score anything. Options: (a) manually curated per track (accurate, doesn't scale), (b) derived algorithmically from steering/speed traces (scales, needs validation against known tracks first). **Needs a decision before 5.2 can be built** — flag as a design spike, not a resourced feature.

### 8.5 "Optimal lap" / AI-generated ghost — open technical question
Referenced in v1 (Ghost Racing) and v1.x. Needs a definition: theoretical best (best segment times stitched together) vs. ML-modeled optimal given car/tire physics. Theoretical-best-segment approach is far simpler and should be the v1.x default; defer physics-modeled optimal to v3+.

---

## 9. Supported Sims

| Phase | Sims | Rationale |
|---|---|---|
| V1 | iRacing, F1 25 | Matches trophi.ai's core coverage; iRacing has the richest telemetry surface, F1 25 has the largest non-sim-purist audience |
| V1.x/V2 | Assetto Corsa Competizione, Le Mans Ultimate, Assetto Corsa EVO, Automobilista 2, rFactor 2 | ACC and LMU prioritized first — both already in trophi.ai's lineup, so parity matters; ACC has a more complete telemetry feed than F1 25 |
| V3+ | Gran Turismo, RaceRoom, BeamNG, Assetto Corsa (legacy) | Platform/API access gated |

---

## 10. Monetization

| Tier | Price | Includes |
|---|---|---|
| Free | $0 | 5 session analyses/month, basic telemetry, corner reports, AI chat |
| Pro | $14.99/mo | Unlimited analysis, live AI engineer, setup recommendations, AI strategist, voice coaching, cloud sync |
| Team | $39.99/mo | Multi-driver dashboards, shared telemetry, league analytics, coach portal, team strategy |

**Note:** the Pro tier as listed bundles v1.x/v3 features (live engineer, setup AI, strategist) that don't exist in v1. Either soft-launch Pro at a reduced feature set and price, or hold Pro pricing until those features ship — decide before go-to-market to avoid overpromising to early paying users.

---

## 11. Competitive Positioning — trophi.ai

Outplay is a direct trophi.ai replacement, so scope should be read against what trophi.ai already ships, not against a blank market.

### What trophi.ai already has (as of researching this doc, June 2026)
- **Live voice coaching** via a single named persona ("Mansell," modeled on a real Driver61 coach) — in-ear guidance during the lap, not just post-session, incl. audio brake tones for memorizing braking zones.
- **Live telemetry overlays** — brake/throttle/speed/gear/steering on-screen in real time.
- **Corner-by-corner post-session breakdown**, mistakes prioritized by lap-time impact.
- **Sim coverage**: iRacing, ACC, F1 23/24/25, Le Mans Ultimate — i.e., already covers the exact iRacing + F1 25 pairing planned for Outplay v1, plus two more sims.
- **Setup packs** (Premium+ tier): pre-built iRacing setups + reference laps, not AI-generated/explained setup recommendations.
- **Pricing**: Premium $7.50/mo (annual), Premium+Setups $16.66/mo, Team $20.83/mo (3 seats), Professional $58.33/mo (adds human 1:1 coaching with a real instructor).
- **No apparent**: AI race strategist, AI spotter, mistake *prediction* (vs. after-the-fact breakdown), driver progression/leveling system, long-term career timeline, multiplayer/racecraft profiling, multiple coach personalities, or AI-*generated* (vs. templated) setup reasoning.

### Where Outplay must at minimum match trophi.ai to be viable
Live voice coaching and live telemetry overlays are not a v1.x nice-to-have here — they're the category-defining feature trophi.ai already sells at $7.50/mo. Shipping Outplay v1 as post-session-only (as scoped in §5) risks looking like a step backward from the incumbent. **Recommend pulling live voice coaching (§6, item 1) into v1**, even in a narrow form (e.g. brake-point audio cues only), so the launch isn't strictly worse than the product being replaced.

### Where Outplay differentiates
- **Mistake *prediction*, not just detection** ("you'll understeer here" before it happens) — trophi.ai reports what happened; Outplay's §6 roadmap predicts what's about to.
- **AI-explained setup recommendations** vs. trophi.ai's static setup *packs* — Outplay explains the causal chain (driver-reported symptom → specific setup change → why), rather than handing over someone else's baseline setup.
- **AI race strategist + spotter** — not offered by trophi.ai at all; genuine expansion into race-day, not just practice/qualifying.
- **Long-term driver memory (Lap DNA™ + Career Timeline)** — trophi.ai's "multi-lap analysis" is session-level; Outplay's positioning is a coach that remembers you across months, not just across laps in one sitting.
- **Multiple coach personas** vs. trophi.ai's single fixed persona (Mansell) — lower priority, but a real differentiator for user preference/retention.

### Pricing implication
trophi.ai's Premium tier ($7.50/mo effective annual) already includes live coaching + telemetry + setups at a price below Outplay's currently planned Pro tier ($14.99/mo, §10). Either justify the premium with features trophi.ai doesn't have at launch (prediction, strategist, spotter) or reconsider entry pricing — undercutting or matching $7.50–17/mo is likely necessary to win switchers, since the switching cost for a sim racer (rebinding a coaching app) is low and price-sensitive.

## 12. What Makes Outplay Different

Competitors — trophi.ai included — answer "where did I lose time?" Outplay answers "why did I lose time, what do I change on the very next lap, and how do I permanently fix that weakness — and what's about to go wrong before it does?" The differentiation isn't live voice coaching (table stakes now) but predictive coaching, causally-explained setup changes, race strategy/spotting, and long-term driver memory that compounds across months.

---

## 13. Real-Time Stack (iRacing + F1 25)

### 13.1 Ingestion
- **iRacing**: shared-memory telemetry, up to 60Hz. Use `pyirsdk` for prototyping or a native binding (Rust/C++) for the production adapter — shared-memory reads are cheap regardless of language.
- **F1 25**: UDP telemetry broadcast (community-documented, same lineage as F1 22–24; EA/Codemasters can change the packet spec between releases, so re-validate each season). Any UDP socket listener works (Go/Rust/Node).
- Both adapters normalize into the common `TelemetrySample` schema from §8.1 — but see §13.2, since the two feeds are not channel-equivalent.

### 13.2 Cross-sim normalization gap
F1 25 does not expose slip angle or per-corner raw tire temperature the way iRacing does; its tire model is coarser (wear/damage-oriented, not thermal). Corner Intelligence scoring logic (§5.2) cannot assume channel parity across sims — either degrade gracefully (drop slip-angle-dependent scores for F1 25) or define sim-specific scoring weightings. This needs a decision before shared scoring code is written.

### 13.3 Pipeline
```
[Sim: iRacing shared-mem / F1 25 UDP]
        ↓
[Per-sim adapter] → normalizes to common schema (Rust/Go, latency-sensitive)
        ↓
[Stream buffer] (in-proc channel, or Redis Streams if durability is needed)
        ↓
[Scoring engine] → deterministic rule-based per-corner scoring (not an LLM call per sample)
        ↓
[Insight generator] → LLM-assisted phrasing, off the hot path
        ↓
[Output] → voice (TTS) + UI overlay + logged for debrief]
```
Rationale: don't put an LLM in the per-sample hot loop — score with deterministic thresholds in real time (target <50ms, §8.3), and only use an LLM to phrase the resulting insight. For live voice coaching specifically, a pre-generated phrase bank keyed to insight category avoids TTS/LLM round-trip latency mid-corner (relevant now that live coaching is pulled into v1, per §11).

### 13.4 Storage
Postgres for the relational model in §8.1; consider TimescaleDB (Postgres extension) once `TelemetrySample` volume grows past prototype scale — raw 60Hz samples across many drivers/sessions will outgrow a plain relational table quickly.

---

## 14. Open Questions Before Build

1. Corner segmentation method (§8.4) — manual vs. algorithmic.
2. "Optimal lap" definition (§8.5) — theoretical best-segment vs. modeled.
3. Pro tier pricing/feature alignment with actual v1 scope, now sharpened by trophi.ai's $7.50/mo Premium tier already including live coaching (§10, §11).
4. Voice coaching latency budget and TTS/STT vendor — not yet selected; now a v1 requirement, not v1.x (§11, §13.3).
5. Cross-sim scoring normalization between iRacing and F1 25 given non-equivalent telemetry channels (§13.2).
6. F1 25 UDP packet spec validation against the live current build before committing adapter code — third-party docs may lag the shipped game.
