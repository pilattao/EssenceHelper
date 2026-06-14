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
