using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Threading.Tasks;
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
        // Swappable snapshot (build-and-swap) so readers never see a half-cleared map.
        private volatile IReadOnlyDictionary<string, decimal> _essencePriceCache =
            new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        private readonly HttpClient _httpClient = new();
        private DateTime _lastEssenceCacheUpdate = DateTime.MinValue;
        private volatile bool _isUpdatingEssencePrices = false;
        private DateTime _lastAutoCorruptTime = DateTime.MinValue;
        private bool _autoMode = true;                // auto-pick source until the user toggles manually
        private bool _suppressToggleHandler = false;  // guard OnValueChanged re-entrancy on programmatic changes
        private string _sourceStatus = "Initializing...";
        private volatile string _currentLeague = "Standard"; // resolved on the main thread, read by the worker
        private readonly StringComparison _essenceComparison = StringComparison.OrdinalIgnoreCase;

        private readonly List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> _reusableEssencesList = new();
        private readonly HashSet<string> _reusableEssenceNames = new();
        private readonly List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position, decimal price)> _reusableEssencesWithPrices = new();

        public override bool Initialise()
        {
            _lastEssenceCacheUpdate = DateTime.MinValue;

            // User interacting with the toggle = manual control. Turning NinjaPricer ON is only
            // allowed when it is actually loaded; otherwise we reject and stay on the API.
            // Read .Value (not the lambda arg) — node OnValueChanged arg types vary across node kinds.
            Settings.UseNinjaPricer.OnValueChanged += (_, _) =>
            {
                if (_suppressToggleHandler) return; // ignore our own programmatic changes
                _autoMode = false;                  // user took manual control

                if (Settings.UseNinjaPricer.Value && !NinjaPricerDetector.IsLoaded(GameController))
                {
                    _suppressToggleHandler = true;
                    Settings.UseNinjaPricer.Value = false; // reject the switch
                    _suppressToggleHandler = false;
                    _sourceStatus = "NinjaPricer plugin not detected — staying on poe.ninja API";
                    LogMessage(_sourceStatus);
                    return;
                }

                UpdateSourceStatus();
                _ = Task.Run(UpdateEssencePrices);
            };

            return true;
        }

        public override void DrawSettings()
        {
            base.DrawSettings();

            ImGui.Separator();
            ImGui.Text(_sourceStatus);

            if (ImGui.Button("Update Essence Prices Now"))
            {
                MaybeAutoDetectSource(); // main thread: refresh detection (auto mode)
                UpdateSourceStatus();    // main thread: refresh league/status before the worker reads _currentLeague
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

        private void DrawEssencePriceList()
        {
            if (ImGui.CollapsingHeader("Essence Prices", ImGuiTreeNodeFlags.DefaultOpen))
            {
                // create a copy of the essence prices to avoid concurrent modification issues
                var essencesCopy = _essencePriceCache?.ToList() ?? new List<KeyValuePair<string, decimal>>();

                if (essencesCopy.Count == 0)
                {
                    return;
                }

                // sort by price descending for better usability
                essencesCopy.Sort((x, y) => y.Value.CompareTo(x.Value));

                // table headers
                if (ImGui.BeginTable("EssencePricesTable", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY))
                {
                    ImGui.TableSetupColumn("Essence Name", ImGuiTableColumnFlags.WidthStretch);
                    ImGui.TableSetupColumn("Price (exalts)", ImGuiTableColumnFlags.WidthFixed, 100);
                    ImGui.TableHeadersRow();

                    for (var i = 0; i < essencesCopy.Count; i++)
                    {
                        ImGui.PushID($"EssencePrice{i}");

                        try
                        {
                            ImGui.TableNextRow();

                            // essence name column
                            ImGui.TableNextColumn();
                            ImGui.Text(essencesCopy[i].Key);

                            // price column
                            ImGui.TableNextColumn();
                            ImGui.Text($"{essencesCopy[i].Value:F3}");
                        }
                        finally
                        {
                            ImGui.PopID();
                        }
                    }

                    ImGui.EndTable();
                }
            }
        }

        public override void Render()
        {
            if (!Settings.Enable) return;

            if (ShouldUpdateEssencePrices())
            {
                MaybeAutoDetectSource(); // main thread: detect NinjaPricer + resolve league + status
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

        // While in auto mode, keep the selected source in sync with NinjaPricer's presence.
        // This handles NinjaPricer registering its PluginBridge method AFTER EssenceHelper loads.
        // Once the user toggles the source manually (_autoMode = false), we stop overriding it.
        private void MaybeAutoDetectSource()
        {
            if (!_autoMode) return;
            var detected = NinjaPricerDetector.IsLoaded(GameController);
            if (Settings.UseNinjaPricer.Value != detected)
            {
                _suppressToggleHandler = true;
                Settings.UseNinjaPricer.Value = detected;
                _suppressToggleHandler = false;
            }
            UpdateSourceStatus();
        }

        // Always called on the main thread (Render / DrawSettings) — safe to read game memory here.
        private void UpdateSourceStatus()
        {
            _currentLeague = ResolveLeague();
            _sourceStatus = Settings.UseNinjaPricer.Value
                ? $"NinjaPricer detected — using its price data (league: {_currentLeague})"
                : $"NinjaPricer not used — fetching from poe.ninja API (league: {_currentLeague})";
        }

        private bool ShouldUpdateEssencePrices()
        {
            if (_isUpdatingEssencePrices) return false;
            if (GameController?.InGame != true) return false; // need league/bridge readable
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
                // Source + league were resolved on the main thread before this Task was queued.
                var league = _currentLeague;
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
                // Build a fresh map and swap the reference atomically (no half-cleared reads).
                var newCache = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in table.ExaltByName)
                    newCache[kvp.Key] = (decimal)kvp.Value;
                _essencePriceCache = newCache;

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
                        if (File.Exists(alt))
                        {
                            LogMessage($"League '{league}' folder not found; using the only available league folder '{Path.GetFileName(dirs[0])}'");
                            return alt;
                        }
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

        private List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> GetEssencesOnGround()
        {
            return DetectEssencesOnGround();
        }

        private List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> DetectEssencesOnGround()
        {
            // reuse collections for better performance
            _reusableEssencesList.Clear();
            _reusableEssenceNames.Clear();

            try
            {
                var itemLabels = GameController.IngameState.IngameUi.ItemsOnGroundLabelElement;
                var labelsVisible = itemLabels?.LabelsOnGroundVisible;
                if (labelsVisible?.Any() != true)
                {
                    return _reusableEssencesList;
                }
                foreach (var label in labelsVisible)
                {
                    try
                    {
                        var itemOnGround = label?.ItemOnGround;
                        var metadata = itemOnGround?.Metadata;
                        if (metadata?.Contains("Monolith") != true) continue;

                        var labelElement = label?.Label;
                        if (labelElement == null) continue;

                        // cache rect calculation for reuse
                        var labelRect = labelElement.GetClientRectCache;
                        var position = new Vector2(labelRect.Center.X, labelRect.Center.Y);

                        // check if label itself has essence text
                        var labelText = labelElement.Text;
                        if (!string.IsNullOrEmpty(labelText) &&
                            labelText.Contains("Essence", _essenceComparison) &&
                            _reusableEssenceNames.Add(labelText))
                        {
                            _reusableEssencesList.Add((itemOnGround, label, labelText, position));
                        }

                        // check label's children for essence text - find ALL essences in this monolith
                        var children = labelElement.Children;
                        if (children != null && children.Count > 0)
                        {
                            foreach (var child in children)
                            {
                                try
                                {
                                    var childText = child?.Text;
                                    if (!string.IsNullOrEmpty(childText) &&
                                        childText.Contains("Essence", _essenceComparison) &&
                                        _reusableEssenceNames.Add(childText))
                                    {
                                        _reusableEssencesList.Add((itemOnGround, label, childText, position));
                                    }

                                    // check grandchildren too
                                    var grandChildren = child.Children;
                                    if (grandChildren != null && grandChildren.Count > 0)
                                    {
                                        foreach (var grandChild in grandChildren)
                                        {
                                            try
                                            {
                                                var grandChildText = grandChild?.Text;
                                                if (!string.IsNullOrEmpty(grandChildText) &&
                                                    grandChildText.Contains("Essence", _essenceComparison) &&
                                                    _reusableEssenceNames.Add(grandChildText))
                                                {
                                                    _reusableEssencesList.Add((itemOnGround, label, grandChildText, position));
                                                }
                                            }
                                            catch
                                            {
                                                continue;
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                    continue;
                                }
                            }
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error detecting essences: {ex.Message}");
            }

            return _reusableEssencesList;
        }

        private void DisplayEssencePrices(List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> essences)
        {
            if (essences.Count == 0)
                return;

            try
            {
                var itemLabels = GameController.IngameState.IngameUi.ItemsOnGroundLabelElement;
                var labelsVisible = itemLabels?.LabelsOnGroundVisible;

                if (labelsVisible?.Any() != true)
                    return;

                var totalPrice = CalculateTotalPrice(essences);
                if (totalPrice <= 0)
                    return;

                // find and draw on the relevant monolith
                foreach (var label in labelsVisible)
                {
                    try
                    {
                        if (!IsValidMonolithForEssences(label, essences))
                            continue;

                        var labelElement = label?.Label;
                        if (labelElement == null)
                            continue;

                        DrawTotalPrice(labelElement, totalPrice);
                        DrawIndividualPrices(labelElement, _reusableEssencesWithPrices);
                        break;
                    }
                    catch
                    {
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                LogError($"Error displaying essence prices: {ex.Message}");
            }
        }

        private decimal CalculateTotalPrice(List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> essences)
        {
            // reuse collection for better performance
            _reusableEssencesWithPrices.Clear();
            decimal totalPrice = 0;

            // calculate total price and prepare essence data
            for (int i = 0; i < essences.Count; i++)
            {
                var (_, _, essenceName, _) = essences[i];
                var price = GetEssencePrice(essenceName);
                if (price > 0)
                {
                    totalPrice += price;
                    _reusableEssencesWithPrices.Add((essences[i].entity, essences[i].label, essenceName, essences[i].Position, price));
                }
            }

            return totalPrice;
        }

        private bool IsValidMonolithForEssences(dynamic label, List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> essences)
        {
            var itemOnGround = label?.ItemOnGround;
            var metadata = itemOnGround?.Metadata;

            return metadata?.Contains("Monolith") == true && IsMonolithWithEssences(label, essences);
        }

        private bool IsMonolithWithEssences(dynamic label, List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> essences)
        {
            var itemOnGround = label?.ItemOnGround;
            if (itemOnGround?.Metadata?.Contains("Monolith") != true) return false;

            var labelElement = label?.Label;
            var children = labelElement?.Children;
            if (children == null || children.Count == 0) return false;

            foreach (var child in children)
            {
                if (HasMatchingEssence(child, essences)) return true;

                var grandChildren = child.Children;
                if (grandChildren != null && grandChildren.Count > 0)
                {
                    foreach (var grandChild in grandChildren)
                    {
                        if (HasMatchingEssence(grandChild, essences)) return true;
                    }
                }
            }
            return false;
        }

        private bool HasMatchingEssence(dynamic element, List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position)> essences)
        {
            var text = element?.Text as string;
            if (string.IsNullOrEmpty(text)) return false;

            // optimized loop instead of LINQ for better performance
            for (int i = 0; i < essences.Count; i++)
            {
                if (essences[i].EssenceName.Equals(text, _essenceComparison))
                    return true;
            }
            return false;
        }

        private void DrawTotalPrice(dynamic labelElement, decimal totalPrice)
        {
            try
            {
                var mainLabelRect = labelElement.GetClientRectCache;
                var totalText = $"Total: {totalPrice:F2} exalts";
                var totalTextSize = Graphics.MeasureText(totalText);

                var totalPos = new Vector2(
                    mainLabelRect.Center.X - totalTextSize.X / 2,
                    mainLabelRect.Top - totalTextSize.Y - 5
                );

                var totalRect = new RectangleF(
                    totalPos.X - 5, totalPos.Y - 2,
                    totalTextSize.X + 10, totalTextSize.Y + 4
                );

                Graphics.DrawBox(totalRect, System.Drawing.Color.FromArgb(200, 0, 0, 0));
                Graphics.DrawText(totalText, totalPos, System.Drawing.Color.Gold);
            }
            catch (Exception ex)
            {
                LogError($"Error drawing total price: {ex.Message}");
            }
        }

        private void DrawIndividualPrices(dynamic labelElement, List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position, decimal price)> essencesWithPrices)
        {
            var children = labelElement.Children;
            if (children == null || children.Count == 0) return;

            foreach (var child in children)
            {
                try
                {
                    DrawPriceForElement(child, essencesWithPrices);

                    var grandChildren = child.Children;
                    if (grandChildren != null && grandChildren.Count > 0)
                    {
                        foreach (var grandChild in grandChildren)
                        {
                            DrawPriceForElement(grandChild, essencesWithPrices);
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }

        private void DrawPriceForElement(dynamic element, List<(Entity entity, LabelOnGround label, string EssenceName, Vector2 Position, decimal price)> essencesWithPrices)
        {
            var text = element?.Text as string;
            if (string.IsNullOrEmpty(text)) return;

            // optimized search instead of LINQ for better performance
            (Entity entity, LabelOnGround label, string EssenceName, Vector2 Position, decimal price) matchingEssence = default;
            bool found = false;

            for (int i = 0; i < essencesWithPrices.Count; i++)
            {
                if (essencesWithPrices[i].EssenceName.Equals(text, _essenceComparison))
                {
                    matchingEssence = essencesWithPrices[i];
                    found = true;
                    break;
                }
            }

            if (!found) return;

            var elementRect = element.GetClientRectCache;
            var priceText = $"{matchingEssence.price:F2}ex";
            var priceTextSize = Graphics.MeasureText(priceText);

            var pricePos = new Vector2(
                elementRect.Right + 5,
                elementRect.Center.Y - priceTextSize.Y / 2
            );

            var priceRect = new RectangleF(
                pricePos.X - 3, pricePos.Y - 2,
                priceTextSize.X + 6, priceTextSize.Y + 4
            );

            Graphics.DrawBox(priceRect, System.Drawing.Color.FromArgb(180, 0, 0, 0));
            Graphics.DrawText(priceText, pricePos, System.Drawing.Color.Lime);
        }

        private decimal GetEssencePrice(string essenceName)
        {
            if (string.IsNullOrEmpty(essenceName)) return 0;

            // exact match - fastest path
            if (_essencePriceCache.TryGetValue(essenceName, out var exactPrice))
            {
                return exactPrice;
            }

            // optimized partial match - avoid LINQ allocation
            foreach (var kvp in _essencePriceCache)
            {
                if (kvp.Key.Contains(essenceName, _essenceComparison) ||
                    essenceName.Contains(kvp.Key, _essenceComparison))
                {
                    return kvp.Value;
                }
            }

            return 0;
        }

        private Element RecursiveFindChildWithTextureName(Element element, string textureName)
        {
            if (element.TextureName == textureName) return element;
            if (element.Children == null) return null;
            foreach (var child in element.Children)
            {
                var result = RecursiveFindChildWithTextureName(child, textureName);
                if (result != null) return result;
            }
            return null;
        }

        private async void AutoCorruptEssence()
        {
            if (!Settings.AutoCorrupt.Value) return;

            var essencesOnGround = _reusableEssencesList;
            if (essencesOnGround.Count == 0) {
                return;
            }

            // group essences by position (same position = same monolith)
            var essenceGroups = essencesOnGround.GroupBy(e => e.Position).ToList();

            // find the closest monolith group by parent element
            var closestGroup = essenceGroups.OrderBy(g => g.First().entity.DistancePlayer).FirstOrDefault();
            if (closestGroup == null) {
                return;
            }

            var firstEssence = closestGroup.First();
            var distance = firstEssence.entity.DistancePlayer;
            if (distance > Settings.MinimumDistanceToEssenceToAutoCorrupt.Value) {
                return;
            }

            // calculate total price for all essences in this monolith
            var totalPrice = closestGroup.Sum(e => GetEssencePrice(e.EssenceName));
            if (totalPrice > Settings.MaximumPriceToAutoCorrupt.Value) {
                return;
            }

            // recursive find child with TextureName = Art/2DItems/Currency/CurrencyVaal.dds
            var child = RecursiveFindChildWithTextureName(firstEssence.label.Label, "Art/2DItems/Currency/CurrencyVaal.dds");

            if (child == null) {
                return;
            }

            var previousMousePosition = Mouse.GetCursorPosition();
            _lastAutoCorruptTime = DateTime.Now;

            // repeat clicking until no more child with texture is found
            while (child != null)
            {
                await Mouse.MoveMouse(child.GetClientRectCache.Center + GameController.Window.GetWindowRectangleTimeCache.TopLeft);
                await Task.Delay(10);
                await Mouse.LeftDown();
                await Task.Delay(10);
                await Mouse.LeftUp();
                await Task.Delay(10);

                // look for another child with the same texture
                child = RecursiveFindChildWithTextureName(firstEssence.label.Label, "Art/2DItems/Currency/CurrencyVaal.dds");
            }

            if (Settings.RestoreMouseToOriginalPosition)
            {
                await Mouse.MoveMouse(previousMousePosition);
            }
        }

        private bool ShouldAutoCorruptEssence()
        {
            if (!Settings.AutoCorrupt.Value)
                return false;
            return DateTime.Now - _lastAutoCorruptTime >= TimeSpan.FromMilliseconds(50);
        }
    }
}
