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
