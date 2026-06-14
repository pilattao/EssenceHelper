# EssenceHelper — Price Source Fix (Design)

Date: 2026-06-14
Repo: `pilattao/EssenceHelper` (fork of `zelekharibo/EssenceHelper`)

## Problem

EssenceHelper loads fine against the current ExileCore2 core (log: `EssenceHelper -> Initialised`),
but **both of its price sources are broken**, so it shows no prices:

1. **Direct API → 404.** The plugin calls `https://poe2scout.com/api/items/currency/essences`.
   That poe2scout endpoint no longer exists (HTTP 404).
2. **Local NinjaPricer data → format changed.** The plugin's `NinjaPricerEssenceItem` model
   expects a flat JSON array `[{ text, priceLogs }]`. The current files are a nested object
   `{ core, lines, items, LinesByName }` (poe.ninja "exchange overview" format), so
   deserialization throws (`JSON value could not be converted to List... BytePositionInLine: 1`).
3. **League is hardcoded and stale.** Default setting is `"Rise of the Abyssal"`; the actual
   current league (and on-disk data folder) is `"Runes of Aldur"`, so the local path never resolves.
4. **Minor.** `Settings.LastApiUpdateTime` is a plain `string` (not a settings Node), so the core
   logs `not a supported settings element` on every load.

### Key discovery

The on-disk folder is named `poescoutdata`, but NinjaPricer actually downloads from **poe.ninja**,
not poe2scout:

```
https://poe.ninja/poe2/api/economy/exchange/current/overview?league={league}&type=Essences
```

So the local files and the "direct API" are **the same poe.ninja `ExchangeOverview` format**.
This collapses both sources onto one model and one parser; only the byte source differs
(local file vs HTTP GET). The dead poe2scout API and its models are removed.

## Goals

- Restore essence price display (total on monolith + per-essence) and the AutoCorrupt feature.
- Pull prices from NinjaPricer's data when NinjaPricer is loaded; otherwise fetch from poe.ninja directly.
- Make the active source visible in the settings UI.
- Auto-detect the current league; stop hardcoding it.

## Non-goals

- No change to the on-screen rendering style of prices (keep current look, exalts).
- No change to AutoCorrupt's input logic (only verify it reads correct prices).
- No Windows build/run here — final build and in-game verification happen on Windows.

## Architecture

### One model, one parser

Port the poe.ninja exchange model from
`NinjaPricer/API/PoeNinja/Models/PoeNinjaModels.cs` into EssenceHelper, using **System.Text.Json**
(already referenced; no new dependency):

- `ExchangeOverview` { `core: CoreData`, `lines: List<ExchangeLine>`, `items: List<ExchangeItem>` }
- `CoreData` { `rates: Rates`, `primary: string`, `secondary: string` }
- `Rates` { `exalted: double?`, `chaos: double?` }
- `ExchangeLine` { `id`, `primaryValue`, ... }
- `ExchangeItem` { `id`, `name`, ... }

**Price lookup by name** (`PriceService`):

- Build `LinesByName` by joining `items` (name↔id) with `lines` (id↔primaryValue) on `id`.
  Do NOT rely on a serialized `LinesByName` field — raw poe.ninja API responses don't include it.
- Exalt value = `line.primaryValue * (core.primary == "exalted" ? 1 : core.rates.exalted)`.
- Name matching: exact first, then the existing contains-both fallback (kept from current plugin).

### Two loaders behind one result

Both produce an `ExchangeOverview` (or null):

- `LocalNinjaSource` — reads `Plugins/Temp/NinjaPricer/poescoutdata/{league}/Essences.json`.
- `PoeNinjaApiSource` — `GET https://poe.ninja/poe2/api/economy/exchange/current/overview?league={league}&type=Essences`
  (simple `HttpClient.GetStringAsync`, no special headers, matching NinjaPricer's `DownloadFromUrl`).

The old `PoE2ScoutApiService` and models (`PoE2ScoutItem`, `PoE2ScoutApiResponse`,
`NinjaPricerEssenceItem`, `NinjaPricerPriceLog`) are deleted.

### NinjaPricer detection (PluginBridge)

NinjaPricer is considered "loaded" when its bridge method is registered:

```csharp
GameController.PluginBridge.GetMethod<Func<Entity, double>>("NinjaPrice.GetValue") != null
```

Detection is **lazy/periodic**, not one-shot in `Initialise()` — plugin load order is not guaranteed,
so a single early check can yield a false negative. Re-check on the price-refresh cadence.

Note: the bridge methods themselves (`GetValue(Entity)` / `GetBaseItemTypeValue(BaseItemType)`) are
NOT used for pricing. Essences on a monolith are text labels, not pickable entities, and the bridge
returns chaos. We use the bridge only as a presence signal; prices come from the files NinjaPricer
downloaded (by essence name), which is the natural fit for name-based lookup.

## Source-selection behavior

A toggle exists: **NinjaPricer ↔ API**. Effective source = currently selected one.

- **On startup:** run detection.
  - NinjaPricer found → select NinjaPricer.
  - Not found → select API.
- **User switches to API:** always allowed (no check).
- **User switches to NinjaPricer:** re-run the same detection check.
  - Found → switch to NinjaPricer.
  - Not found → **do not switch** (stay on API); show "NinjaPricer plugin not detected".

Implementation: a `ToggleNode UseNinjaPricer` with an `OnValueChanged` hook that, when set true,
validates presence and reverts to false (+ status message) if absent. Startup sets it from detection.

### Graceful degradation

In NinjaPricer mode, if the local file is missing/empty/unparseable for the active league,
log it and surface a status note. (Soft one-shot API fallback is acceptable but optional;
the displayed selected source stays as chosen.)

## League handling

- Auto-detect the current league from the game (same source NinjaPricer's `SyncCurrentLeague` uses),
  with a manual override `TextNode` (kept from current `LeagueName`).
- Local-file path: if the configured league folder is absent but exactly one league folder exists
  under `poescoutdata`, use it (and note the substitution).

## Settings changes

- Remove persistent `LastApiUpdateTime` string (fixes the core warning); keep update throttling in memory.
- Replace `UseNinjaPricerData` with the gated `UseNinjaPricer` toggle described above.
- Keep `LeagueName`, `ApiUpdateInterval`, `AutoCorrupt`, `MaximumPriceToAutoCorrupt`,
  `MinimumDistanceToEssenceToAutoCorrupt`, `RestoreMouseToOriginalPosition`.

## UI

In `DrawSettings`, show an explicit source/detection status line:

- 🟢 `NinjaPricer detected — using its price data (league: <league>)`
- 🟡 `NinjaPricer not found — fetching from poe.ninja API (league: <league>)`
- On a rejected switch: `NinjaPricer plugin not detected — staying on API`

Keep the existing "Update Essence Prices Now" button, last-updated text, cached-count, and price table.

## Housekeeping

- Rename `RitualHelper.csproj` → `EssenceHelper.csproj` (leftover from the fork lineage);
  ensure assembly/output name is `EssenceHelper`.
- Update `README.md` to describe the auto-detect + poe.ninja behavior.

## Testing

A standalone **`net8.0`** (NOT `net8.0-windows`) test project, runnable on Linux/WSL, covering the
platform-independent core logic:

- Parse the real sample `Runes of Aldur/Essences.json` into `ExchangeOverview`.
- Build `LinesByName` and assert known essences resolve (e.g. `Essence of Delirium`).
- Assert exalt conversion: `primaryValue (0.04495) * rates.exalted (141.8) ≈ 6.37`.
- Name-matching fallback behaves as expected.

This proves the parsing/conversion without Windows. The plugin assembly itself still requires a
Windows build + in-game check for the UI/AutoCorrupt paths.

## Constraints / risks

- `net8.0-windows` + WinForms → no build here; Windows needed for the final artifact.
- poe.ninja may rate-limit; keep the existing throttle/interval.
- League auto-detection depends on the core exposing current league; verify the exact accessor
  during implementation (cross-check NinjaPricer's `SyncCurrentLeague`).
