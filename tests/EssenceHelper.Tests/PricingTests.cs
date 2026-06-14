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
