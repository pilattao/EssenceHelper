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
