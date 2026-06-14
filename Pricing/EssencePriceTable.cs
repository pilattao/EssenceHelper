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

        // If the exalted conversion rate is unavailable, don't emit a table full of
        // zero-priced essences (that would silently mask the problem). Leave it empty
        // so callers treat the source as unusable and fall back to another source.
        if (multiplier <= 0)
            return;

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
