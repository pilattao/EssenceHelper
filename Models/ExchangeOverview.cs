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
