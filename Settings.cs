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

        // NOTE: RangeNode is new(value, min, max); the original plugin had value < min (broken slider).
        [Menu("Maximum price in exalts to auto corrupt")]
        public RangeNode<int> MaximumPriceToAutoCorrupt { get; set; } = new(100, 1, 1000);

        [Menu("Minimum distance to essence to auto corrupt")]
        public RangeNode<int> MinimumDistanceToEssenceToAutoCorrupt { get; set; } = new(50, 0, 1000);

        [Menu("Restore mouse to original position", "Restore mouse to original position after auto corrupt")]
        public ToggleNode RestoreMouseToOriginalPosition { get; set; } = new(true);
    }
}
