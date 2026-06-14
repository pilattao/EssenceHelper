# EssenceHelper Price Source Fix — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore EssenceHelper's essence pricing by replacing both dead price sources (poe2scout API + old NinjaPricer file format) with the current poe.ninja `ExchangeOverview` format, auto-selecting NinjaPricer-vs-API by PluginBridge detection.

**Architecture:** One poe.ninja model (`ExchangeOverview`) + one pure price table feed two loaders (local NinjaPricer file / poe.ninja HTTP). NinjaPricer presence is detected via `PluginBridge`; a gated toggle picks the source. Pure parsing/pricing logic lives in ExileCore-free files so a `net8.0` unit-test project can exercise it on Linux.

**Tech Stack:** C# / .NET 8 (`net8.0-windows` plugin, `net8.0` tests), System.Text.Json, ImGui.NET, xUnit.

---

## Environment

- `dotnet` 8.0.422 is on PATH (`~/.dotnet`). The **test project builds and runs here**.
- The **plugin assembly** targets `net8.0-windows` + WinForms and **cannot build on Linux** — it is built on Windows against ExileCore2/GameOffsets2. Do NOT attempt `dotnet build` of the plugin csproj here; only the test project.
- Pure files (`Models/`, `Pricing/EssencePriceTable.cs`, `Sources/LocalNinjaSource.cs`, `Sources/PoeNinjaApiSource.cs`) must NOT reference ExileCore2/GameOffsets2/WinForms/ImGui, so the test project can compile them directly.
- Repo: `~/Work/My/EssenceHelper` (origin = `pilattao/EssenceHelper`, upstream = `zelekharibo/EssenceHelper`).

## File Structure

**Create (pure, ExileCore-free):**
- `Models/ExchangeOverview.cs` — poe.ninja exchange-overview POCOs (`ExchangeOverview`, `CoreData`, `Rates`, `ExchangeLine`, `ExchangeItem`).
- `Pricing/EssencePriceTable.cs` — builds name→exalt map from an `ExchangeOverview`; exposes `GetExaltedValue(name)`.
- `Sources/LocalNinjaSource.cs` — `Task<ExchangeOverview?> LoadAsync(string filePath)`.
- `Sources/PoeNinjaApiSource.cs` — `Task<ExchangeOverview?> LoadAsync(string league, HttpClient client)`.

**Create (ExileCore-dependent):**
- `Pricing/NinjaPricerDetector.cs` — `bool IsLoaded(GameController gc)` via PluginBridge.

**Create (tests, `net8.0`):**
- `tests/EssenceHelper.Tests/EssenceHelper.Tests.csproj`
- `tests/EssenceHelper.Tests/PricingTests.cs`
- `tests/EssenceHelper.Tests/data/Essences.json` (copied real sample)

**Modify:**
- `Settings.cs` — replace `UseNinjaPricerData`→gated `UseNinjaPricer`; drop `LastApiUpdateTime`; default `LeagueName` to `""` (= auto).
- `EssenceHelper.cs` — orchestration: detection, source selection, league resolve, refresh, status text.
- `RitualHelper.csproj` → rename to `EssenceHelper.csproj`; add `<Compile Remove="tests/**" />`.
- `README.md` — describe new behavior.

**Delete:**
- `PoE2ScoutApiService.cs`, `CurrencyData.cs` (obsolete poe2scout models).

---

## Task 1: Test project + first failing test

**Files:**
- Create: `tests/EssenceHelper.Tests/EssenceHelper.Tests.csproj`
- Create: `tests/EssenceHelper.Tests/PricingTests.cs`
- Create: `tests/EssenceHelper.Tests/data/Essences.json`

- [ ] **Step 1: Copy the real sample data into the test project**

```bash
mkdir -p "$HOME/Work/My/EssenceHelper/tests/EssenceHelper.Tests/data"
cp "/home/daleksandrov/Work/My/ExileCore2_05/Plugins/Temp/NinjaPricer/poescoutdata/Runes of Aldur/Essences.json" \
   "$HOME/Work/My/EssenceHelper/tests/EssenceHelper.Tests/data/Essences.json"
```

- [ ] **Step 2: Create the test csproj**

Create `tests/EssenceHelper.Tests/EssenceHelper.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>

  <ItemGroup>
    <!-- Link the pure (ExileCore-free) production files directly -->
    <Compile Include="..\..\Models\ExchangeOverview.cs" />
    <Compile Include="..\..\Pricing\EssencePriceTable.cs" />
    <Compile Include="..\..\Sources\LocalNinjaSource.cs" />
    <!-- Test files -->
    <Compile Include="PricingTests.cs" />
  </ItemGroup>

  <ItemGroup>
    <None Include="data\Essences.json" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write the failing tests**

Create `tests/EssenceHelper.Tests/PricingTests.cs`:

```csharp
using System.IO;
using System.Threading.Tasks;
using EssenceHelper.Models;
using EssenceHelper.Pricing;
using EssenceHelper.Sources;
using Xunit;

namespace EssenceHelper.Tests;

public class PricingTests
{
    private static string SamplePath =>
        Path.Combine(AppContext.BaseDirectory, "data", "Essences.json");

    [Fact]
    public async Task LocalSource_parses_sample_file()
    {
        var overview = await LocalNinjaSource.LoadAsync(SamplePath);
        Assert.NotNull(overview);
        Assert.NotNull(overview!.Lines);
        Assert.NotNull(overview.Items);
        Assert.NotEmpty(overview.Lines!);
        Assert.NotEmpty(overview.Items!);
        Assert.Equal("divine", overview.Core!.Primary);
    }

    [Fact]
    public async Task PriceTable_resolves_known_essence_in_exalts()
    {
        var overview = await LocalNinjaSource.LoadAsync(SamplePath);
        var table = new EssencePriceTable(overview!);

        // Essence of Delirium: primaryValue 0.04495 * rates.exalted 141.8 ~= 6.37 ex
        var value = table.GetExaltedValue("Essence of Delirium");
        Assert.InRange(value, 6.0, 6.8);
    }

    [Fact]
    public void PriceTable_unknown_name_returns_zero()
    {
        var table = new EssencePriceTable(new ExchangeOverview());
        Assert.Equal(0.0, table.GetExaltedValue("Not A Real Essence"));
    }

    [Fact]
    public void PriceTable_uses_multiplier_one_when_primary_is_exalted()
    {
        var overview = new ExchangeOverview
        {
            Core = new CoreData { Primary = "exalted", Rates = new Rates { Exalted = 999 } },
            Items = new() { new ExchangeItem { Id = "x", Name = "Test Essence" } },
            Lines = new() { new ExchangeLine { Id = "x", PrimaryValue = 12.5 } },
        };
        var table = new EssencePriceTable(overview);
        Assert.Equal(12.5, table.GetExaltedValue("Test Essence"));
    }
}
```

- [ ] **Step 4: Run tests to verify they fail (types don't exist yet)**

Run: `cd ~/Work/My/EssenceHelper && dotnet test tests/EssenceHelper.Tests`
Expected: BUILD FAILURE — the linked files don't exist yet (`Source file '..\..\Models\ExchangeOverview.cs' could not be found`, same for `EssencePriceTable.cs` / `LocalNinjaSource.cs`). This is the intended red.

- [ ] **Step 5: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add tests/
git commit -m "test: add pricing test project with poe.ninja sample data"
```

---

## Task 2: poe.ninja model (`ExchangeOverview`)

**Files:**
- Create: `Models/ExchangeOverview.cs`

- [ ] **Step 1: Create the model**

Create `Models/ExchangeOverview.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace EssenceHelper.Models;

// poe.ninja /poe2/api/economy/exchange/current/overview response shape.
public sealed class ExchangeOverview
{
    [JsonPropertyName("core")] public CoreData? Core { get; set; }
    [JsonPropertyName("lines")] public List<ExchangeLine>? Lines { get; set; }
    [JsonPropertyName("items")] public List<ExchangeItem>? Items { get; set; }
}

public sealed class CoreData
{
    [JsonPropertyName("rates")] public Rates? Rates { get; set; }
    [JsonPropertyName("primary")] public string? Primary { get; set; }
    [JsonPropertyName("secondary")] public string? Secondary { get; set; }
}

public sealed class Rates
{
    [JsonPropertyName("exalted")] public double? Exalted { get; set; }
    [JsonPropertyName("chaos")] public double? Chaos { get; set; }
}

public sealed class ExchangeLine
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("primaryValue")] public double PrimaryValue { get; set; }
}

public sealed class ExchangeItem
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}
```

- [ ] **Step 2: Build the test project (still red — `EssencePriceTable.cs`/`LocalNinjaSource.cs` not created yet)**

Run: `cd ~/Work/My/EssenceHelper && dotnet build tests/EssenceHelper.Tests`
Expected: BUILD FAILURE — `Source file '..\..\Pricing\EssencePriceTable.cs' could not be found` (and `LocalNinjaSource.cs`). `ExchangeOverview.cs` now resolves.

- [ ] **Step 3: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add Models/ExchangeOverview.cs
git commit -m "feat: add poe.ninja ExchangeOverview model"
```

---

## Task 3: Pure price table (`EssencePriceTable`)

**Files:**
- Create: `Pricing/EssencePriceTable.cs`

- [ ] **Step 1: Implement the price table**

Create `Pricing/EssencePriceTable.cs`:

```csharp
using System;
using System.Collections.Generic;
using EssenceHelper.Models;

namespace EssenceHelper.Pricing;

// Builds a name -> exalted-value lookup from a poe.ninja ExchangeOverview.
public sealed class EssencePriceTable
{
    private readonly Dictionary<string, double> _exaltByName =
        new(StringComparer.OrdinalIgnoreCase);

    public EssencePriceTable(ExchangeOverview overview)
    {
        if (overview?.Lines == null || overview.Items == null)
            return;

        var multiplier = overview.Core?.Primary == "exalted"
            ? 1.0
            : overview.Core?.Rates?.Exalted ?? 0.0;

        var lineById = new Dictionary<string, ExchangeLine>(StringComparer.Ordinal);
        foreach (var line in overview.Lines)
            if (line?.Id != null)
                lineById[line.Id] = line;

        foreach (var item in overview.Items)
        {
            if (item?.Id == null || item.Name == null)
                continue;
            if (lineById.TryGetValue(item.Id, out var line))
                _exaltByName[item.Name] = line.PrimaryValue * multiplier;
        }
    }

    public int Count => _exaltByName.Count;

    public IReadOnlyDictionary<string, double> ExaltByName => _exaltByName;

    // Exact match first, then the lenient contains-both fallback the plugin used before.
    public double GetExaltedValue(string name)
    {
        if (string.IsNullOrEmpty(name))
            return 0;
        if (_exaltByName.TryGetValue(name, out var exact))
            return exact;
        foreach (var kvp in _exaltByName)
            if (kvp.Key.Contains(name, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
                return kvp.Value;
        return 0;
    }
}
```

- [ ] **Step 2: Build (still red — `LocalNinjaSource.cs` not created yet)**

Run: `cd ~/Work/My/EssenceHelper && dotnet build tests/EssenceHelper.Tests`
Expected: BUILD FAILURE — `Source file '..\..\Sources\LocalNinjaSource.cs' could not be found`.

- [ ] **Step 3: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add Pricing/EssencePriceTable.cs
git commit -m "feat: add pure EssencePriceTable (name -> exalt)"
```

---

## Task 4: Local file source (`LocalNinjaSource`) — tests go green

**Files:**
- Create: `Sources/LocalNinjaSource.cs`

- [ ] **Step 1: Implement the local source**

Create `Sources/LocalNinjaSource.cs`:

```csharp
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using EssenceHelper.Models;

namespace EssenceHelper.Sources;

// Reads a NinjaPricer-downloaded exchange overview file (poe.ninja format) from disk.
public static class LocalNinjaSource
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<ExchangeOverview?> LoadAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return null;

        var json = await File.ReadAllTextAsync(filePath);
        if (string.IsNullOrEmpty(json))
            return null;

        return JsonSerializer.Deserialize<ExchangeOverview>(json, Options);
    }
}
```

- [ ] **Step 2: Run tests — expect GREEN**

Run: `cd ~/Work/My/EssenceHelper && dotnet test tests/EssenceHelper.Tests`
Expected: PASS — 4 passed.

- [ ] **Step 3: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add Sources/LocalNinjaSource.cs
git commit -m "feat: add LocalNinjaSource file loader; pricing tests pass"
```

---

## Task 5: poe.ninja HTTP source + NinjaPricer detector

**Files:**
- Create: `Sources/PoeNinjaApiSource.cs`
- Create: `Pricing/NinjaPricerDetector.cs`

These are not unit-tested (network / ExileCore dependency); they are small and verified at runtime on Windows.

- [ ] **Step 1: Implement the API source**

Create `Sources/PoeNinjaApiSource.cs`:

```csharp
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EssenceHelper.Models;

namespace EssenceHelper.Sources;

// Downloads the essence exchange overview directly from poe.ninja (same format as local files).
public static class PoeNinjaApiSource
{
    private const string UrlTemplate =
        "https://poe.ninja/poe2/api/economy/exchange/current/overview?league={0}&type=Essences";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<ExchangeOverview?> LoadAsync(string league, HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(league) || client == null)
            return null;

        var url = string.Format(UrlTemplate, Uri.EscapeDataString(league));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var json = await client.GetStringAsync(url, cts.Token);
        if (string.IsNullOrEmpty(json))
            return null;

        return JsonSerializer.Deserialize<ExchangeOverview>(json, Options);
    }
}
```

- [ ] **Step 2: Implement the detector**

Create `Pricing/NinjaPricerDetector.cs`:

```csharp
using System;
using ExileCore2;
using ExileCore2.PoEMemory.MemoryObjects;

namespace EssenceHelper.Pricing;

// NinjaPricer is "loaded" iff it has registered its PluginBridge price method.
public static class NinjaPricerDetector
{
    public static bool IsLoaded(GameController gameController)
    {
        try
        {
            return gameController?.PluginBridge?
                .GetMethod<Func<Entity, double>>("NinjaPrice.GetValue") != null;
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 3: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add Sources/PoeNinjaApiSource.cs Pricing/NinjaPricerDetector.cs
git commit -m "feat: add poe.ninja HTTP source and PluginBridge NinjaPricer detector"
```

---

## Task 6: Settings — gated toggle, drop stale string, auto league default

**Files:**
- Modify: `Settings.cs`

- [ ] **Step 1: Replace the settings body**

Replace the whole `class Settings` body in `Settings.cs` with:

```csharp
using ExileCore2.Shared.Attributes;
using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace EssenceHelper
{
    public class Settings : ISettings
    {
        public ToggleNode Enable { get; set; } = new(true);

        [Menu("League Name", "Leave EMPTY to auto-detect the current league. Override only if needed.")]
        public TextNode LeagueName { get; set; } = new("");

        [Menu("Use NinjaPricer data", "ON = use NinjaPricer's downloaded prices (only allowed if NinjaPricer is loaded). OFF = fetch from poe.ninja directly. Auto-set on startup.")]
        public ToggleNode UseNinjaPricer { get; set; } = new(false);

        [Menu("Auto-Update Interval (minutes)", "How often to refresh essence prices")]
        public RangeNode<int> ApiUpdateInterval { get; set; } = new(30, 5, 180);

        [Menu("Auto corrupt", "Use Vaal Orb on the Essence")]
        public ToggleNode AutoCorrupt { get; set; } = new(false);

        [Menu("Maximum price in exalts to auto corrupt")]
        public RangeNode<int> MaximumPriceToAutoCorrupt { get; set; } = new(1, 5, 1000);

        [Menu("Minimum distance to essence to auto corrupt")]
        public RangeNode<int> MinimumDistanceToEssenceToAutoCorrupt { get; set; } = new(0, 50, 1000);

        [Menu("Restore mouse to original position", "Restore mouse to original position after auto corrupt")]
        public ToggleNode RestoreMouseToOriginalPosition { get; set; } = new(true);
    }
}
```

Notes: `LastApiUpdateTime` (plain string, caused the core warning) is removed. `UseNinjaPricerData` is replaced by `UseNinjaPricer`. `LeagueName` default is now `""` (auto).

- [ ] **Step 2: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add Settings.cs
git commit -m "feat: gated UseNinjaPricer toggle, auto-league default, drop stale LastApiUpdateTime"
```

---

## Task 7: EssenceHelper orchestration rewrite

**Files:**
- Modify: `EssenceHelper.cs`

This wires the model/sources/detector into the plugin: startup auto-select, gated toggle hook, league resolution, NinjaPricer file path resolution, refresh, and status text. Methods that draw essence prices on the monolith (`DetectEssencesOnGround`, `DisplayEssencePrices`, `DrawTotalPrice`, `DrawIndividualPrices`, `DrawPriceForElement`, `AutoCorruptEssence`, `RecursiveFindChildWithTextureName`, `ShouldAutoCorruptEssence`) are UNCHANGED — keep them as-is.

- [ ] **Step 1: Replace the using-block and field block**

Replace lines 1–36 (top of `EssenceHelper.cs`, through the reusable-list fields) with:

```csharp
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
using System.Windows.Forms;
using ExileCore2;
using ExileCore2.Shared;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Components;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.Elements.InventoryElements;
using ExileCore2.PoEMemory.MemoryObjects;
using ExileCore2.Shared.Enums;
using ImGuiNET;
using EssenceHelper.Models;
using EssenceHelper.Pricing;
using EssenceHelper.Sources;
using EssenceHelper.Utils;

namespace EssenceHelper
{
    public class EssenceHelper : BaseSettingsPlugin<Settings>
    {
        private readonly ConcurrentDictionary<RectangleF, bool?> _mouseStateForRect = new();
        private readonly ConcurrentDictionary<string, decimal> _essencePriceCache = new();
        private readonly HttpClient _httpClient = new();
        private DateTime _lastEssenceCacheUpdate = DateTime.MinValue;
        private volatile bool _isUpdatingEssencePrices = false;
        private DateTime _lastAutoCorruptTime = DateTime.MinValue;
        private bool _startupSelectionDone = false;
        private string _sourceStatus = "Initializing...";
        private readonly StringComparison _essenceComparison = StringComparison.OrdinalIgnoreCase;

        private readonly List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> _reusableEssencesList = new();
        private readonly HashSet<string> _reusableEssenceNames = new();
        private readonly List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position, decimal price)> _reusableEssencesWithPrices = new();
```

- [ ] **Step 2: Replace `Initialise()` (was lines ~38-63)**

```csharp
        public override bool Initialise()
        {
            _lastEssenceCacheUpdate = DateTime.MinValue;

            // Re-validate when the user tries to turn NinjaPricer ON: only allow if it is loaded.
            // Read .Value (not the lambda arg) — node OnValueChanged arg types vary across node kinds.
            Settings.UseNinjaPricer.OnValueChanged += (_, _) =>
            {
                if (!_startupSelectionDone) return; // startup sets this itself
                if (Settings.UseNinjaPricer.Value && !NinjaPricerDetector.IsLoaded(GameController))
                {
                    Settings.UseNinjaPricer.Value = false; // reject the switch
                    _sourceStatus = "NinjaPricer plugin not detected — staying on poe.ninja API";
                    LogMessage(_sourceStatus);
                }
                else
                {
                    UpdateSourceStatus();
                    _ = Task.Run(UpdateEssencePrices);
                }
            };

            return true;
        }
```

- [ ] **Step 3: Replace `DrawSettings()` (was lines ~65-90)**

```csharp
        public override void DrawSettings()
        {
            base.DrawSettings();

            ImGui.Separator();
            ImGui.Text(_sourceStatus);

            if (ImGui.Button("Update Essence Prices Now"))
            {
                _ = Task.Run(UpdateEssencePrices);
            }

            ImGui.SameLine();
            var lastUpdateText = _lastEssenceCacheUpdate == DateTime.MinValue
                ? "Never updated"
                : $"Last updated: {_lastEssenceCacheUpdate:HH:mm:ss}";
            ImGui.Text(lastUpdateText);

            if (_essencePriceCache.Count > 0)
            {
                ImGui.Text($"Cached essences: {_essencePriceCache.Count}");
                ImGui.Separator();
                DrawEssencePriceList();
            }
        }
```

(`DrawEssencePriceList()` is unchanged — keep it.)

- [ ] **Step 4: Replace `Render()` (was lines ~147-168) and add startup auto-select**

```csharp
        public override void Render()
        {
            if (!Settings.Enable) return;

            if (!_startupSelectionDone)
            {
                RunStartupSourceSelection();
            }

            if (ShouldUpdateEssencePrices())
            {
                _ = Task.Run(UpdateEssencePrices);
            }

            var essencesOnGround = GetEssencesOnGround();
            if (essencesOnGround.Any())
            {
                DisplayEssencePrices(essencesOnGround);
            }

            if (ShouldAutoCorruptEssence())
            {
                AutoCorruptEssence();
            }
        }

        private void RunStartupSourceSelection()
        {
            // Wait until we are in game so league/bridge are readable.
            if (GameController?.InGame != true) return;

            var detected = NinjaPricerDetector.IsLoaded(GameController);
            Settings.UseNinjaPricer.Value = detected;
            _startupSelectionDone = true;
            UpdateSourceStatus();
            _ = Task.Run(UpdateEssencePrices);
        }

        private void UpdateSourceStatus()
        {
            var league = ResolveLeague();
            _sourceStatus = Settings.UseNinjaPricer.Value
                ? $"NinjaPricer detected — using its price data (league: {league})"
                : $"NinjaPricer not used — fetching from poe.ninja API (league: {league})";
        }
```

- [ ] **Step 5: Replace the throttle/update helpers (was lines ~170-275: `ShouldUpdateFromApi`, `GetLastApiUpdateFromSettings`, `SaveLastApiUpdateTime`, `ValidateApiSettings`, `ShouldUpdateEssencePrices`, `UpdateEssencePrices`, `UpdateEssencePricesFromApi`)**

Delete all of those and replace with:

```csharp
        private bool ShouldUpdateEssencePrices()
        {
            if (_isUpdatingEssencePrices) return false;
            if (!_startupSelectionDone) return false;
            var interval = TimeSpan.FromMinutes(Settings.ApiUpdateInterval.Value);
            return DateTime.Now - _lastEssenceCacheUpdate >= interval;
        }

        private string ResolveLeague()
        {
            var manual = Settings.LeagueName?.Value;
            if (!string.IsNullOrWhiteSpace(manual))
                return manual.Trim();

            var raw = GameController?.IngameState?.ServerData?.League;
            return NormalizeLeague(raw);
        }

        private static string NormalizeLeague(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "Standard";
            if (raw.StartsWith("HC SSF "))
                return $"HC {raw["HC SSF ".Length..]}";
            if (raw.StartsWith("SSF "))
                return raw["SSF ".Length..];
            return raw;
        }

        private async Task UpdateEssencePrices()
        {
            if (_isUpdatingEssencePrices) return;
            _isUpdatingEssencePrices = true;
            try
            {
                var league = ResolveLeague();
                ExchangeOverview overview = null;

                if (Settings.UseNinjaPricer.Value)
                {
                    var path = GetNinjaPricerEssencesPath(league);
                    overview = await LocalNinjaSource.LoadAsync(path);
                    if (overview?.Lines == null || overview.Lines.Count == 0)
                    {
                        LogMessage("NinjaPricer file missing/empty — falling back to poe.ninja API");
                        overview = await PoeNinjaApiSource.LoadAsync(league, _httpClient);
                    }
                }
                else
                {
                    overview = await PoeNinjaApiSource.LoadAsync(league, _httpClient);
                }

                if (overview == null)
                {
                    LogError($"Failed to load essence prices for league '{league}'");
                    return;
                }

                var table = new EssencePriceTable(overview);
                _essencePriceCache.Clear();
                foreach (var kvp in table.ExaltByName)
                    _essencePriceCache[kvp.Key] = (decimal)kvp.Value;

                _lastEssenceCacheUpdate = DateTime.Now;
                LogMessage($"Updated essence prices: {_essencePriceCache.Count} essences cached ({(Settings.UseNinjaPricer.Value ? "NinjaPricer" : "poe.ninja API")})");
            }
            catch (Exception ex)
            {
                LogError($"Failed to update essence prices: {ex.Message}");
            }
            finally
            {
                _isUpdatingEssencePrices = false;
            }
        }

        private string GetNinjaPricerEssencesPath(string league)
        {
            try
            {
                var pluginsRoot = FindPluginsRoot(DirectoryFullName);
                if (pluginsRoot == null) return null;

                var baseDir = Path.Combine(pluginsRoot, "Temp", "NinjaPricer", "poescoutdata");
                var path = Path.Combine(baseDir, league, "Essences.json");
                if (File.Exists(path)) return path;

                // Fallback: if exactly one league folder exists, use it.
                if (Directory.Exists(baseDir))
                {
                    var dirs = Directory.GetDirectories(baseDir);
                    if (dirs.Length == 1)
                    {
                        var alt = Path.Combine(dirs[0], "Essences.json");
                        if (File.Exists(alt)) return alt;
                    }
                }
                return path;
            }
            catch (Exception ex)
            {
                LogError($"Error building NinjaPricer data path: {ex.Message}");
                return null;
            }
        }

        private static string FindPluginsRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (string.Equals(dir.Name, "Plugins", StringComparison.OrdinalIgnoreCase))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }
```

- [ ] **Step 6: Delete now-dead helpers**

Delete these methods if still present (replaced above / no longer referenced):
`UpdateEssencePricesFromNinjaPricer`, `GetNinjaPricerDataPath` (old multi-category version), `UpdateDeferListFromApi`. Keep `GetEssencePrice`, `CalculateTotalPrice`, all draw/detect/autocorrupt methods, and `DrawEssencePriceList`.

- [ ] **Step 7: Sanity-check references**

Run: `cd ~/Work/My/EssenceHelper && grep -nE "PoE2ScoutApiService|NinjaPricerEssenceItem|LastApiUpdateTime|UseNinjaPricerData|GetNinjaPricerDataPath\(" EssenceHelper.cs`
Expected: no matches (all old symbols gone).

- [ ] **Step 8: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add EssenceHelper.cs
git commit -m "feat: orchestrate poe.ninja sources with auto NinjaPricer detection"
```

---

## Task 8: Delete obsolete files, rename csproj, exclude tests, update README

**Files:**
- Delete: `PoE2ScoutApiService.cs`, `CurrencyData.cs`
- Rename: `RitualHelper.csproj` → `EssenceHelper.csproj`
- Modify: `README.md`

- [ ] **Step 1: Delete obsolete poe2scout files**

```bash
cd ~/Work/My/EssenceHelper
git rm PoE2ScoutApiService.cs CurrencyData.cs
```

- [ ] **Step 2: Rename the csproj and add test exclusion**

```bash
cd ~/Work/My/EssenceHelper
git mv RitualHelper.csproj EssenceHelper.csproj
```

Then edit `EssenceHelper.csproj` to (a) ensure the assembly name is `EssenceHelper` and (b) keep the SDK glob from swallowing the test project. Add inside the first `<PropertyGroup>`:

```xml
    <AssemblyName>EssenceHelper</AssemblyName>
    <RootNamespace>EssenceHelper</RootNamespace>
```

And add a new `<ItemGroup>`:

```xml
  <ItemGroup>
    <Compile Remove="tests\**\*.cs" />
    <Content Remove="tests\**\*" />
    <None Remove="tests\**\*" />
  </ItemGroup>
```

- [ ] **Step 3: Confirm the test project still builds in isolation**

Run: `cd ~/Work/My/EssenceHelper && dotnet test tests/EssenceHelper.Tests`
Expected: PASS — 4 passed. (The test csproj uses explicit `<Compile Include>` and is unaffected by the plugin csproj.)

- [ ] **Step 4: Update README**

Replace `README.md` contents with:

```markdown
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
```

- [ ] **Step 5: Commit**

```bash
cd ~/Work/My/EssenceHelper
git add -A
git commit -m "chore: remove poe2scout code, rename csproj to EssenceHelper, update README"
```

---

## Task 9: Final verification

- [ ] **Step 1: Full test run**

Run: `cd ~/Work/My/EssenceHelper && dotnet test tests/EssenceHelper.Tests`
Expected: PASS — 4 passed.

- [ ] **Step 2: Confirm no stale symbols remain anywhere**

Run:
```bash
cd ~/Work/My/EssenceHelper
grep -rnE "poe2scout|PoE2Scout|NinjaPricerEssenceItem|LastApiUpdateTime|UseNinjaPricerData" --include=*.cs .
```
Expected: no matches.

- [ ] **Step 3: Confirm pure files have no ExileCore/WinForms references (so tests stay buildable)**

Run:
```bash
cd ~/Work/My/EssenceHelper
grep -lE "ExileCore2|System.Windows.Forms|ImGuiNET" Models/ExchangeOverview.cs Pricing/EssencePriceTable.cs Sources/LocalNinjaSource.cs Sources/PoeNinjaApiSource.cs
```
Expected: no matches (these files are ExileCore-free).

- [ ] **Step 4: Push the branch**

```bash
cd ~/Work/My/EssenceHelper
git push -u origin main
```

(Windows-side: build the plugin against ExileCore2, drop into the overlay, verify essence prices show on a monolith and the settings status line is correct.)
