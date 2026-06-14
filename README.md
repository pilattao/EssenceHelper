# EssenceHelper

ExileCore2 plugin for Path of Exile 2 that shows the value of essences on a monolith
(per-essence and total, in exalts) and can optionally auto-corrupt the closest monolith.

## Price source

Prices use the poe.ninja exchange-overview data.

- If the **NinjaPricer** plugin is loaded, EssenceHelper reuses its already-downloaded data
  (`Plugins/Temp/NinjaPricer/poescoutdata/<league>/Essences.json`). The settings panel shows
  `NinjaPricer detected — using its price data`.
- If NinjaPricer is not loaded, EssenceHelper fetches directly from poe.ninja.

The source is auto-selected on startup. The `Use NinjaPricer data` toggle lets you switch manually;
turning it ON is only allowed when NinjaPricer is actually loaded.

## League

Leave `League Name` empty to auto-detect the current league from the game; set it to override.

## Build

Targets `net8.0-windows`; build on Windows against your ExileCore2 package
(`exileCore2Package` MSBuild property). Unit tests live in `tests/EssenceHelper.Tests`
(`net8.0`) and run with `dotnet test`.
