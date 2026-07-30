namespace Warlord.Core.State;

/// <summary>The default set of playable clans, shared by map factories.</summary>
public static class Clans
{
    public static IReadOnlyList<Faction> Default { get; } = new List<Faction>
    {
        new(0, "Oda",    new RgbColor(0xC0, 0x39, 0x2B)), // red
        new(1, "Takeda", new RgbColor(0x2C, 0x6F, 0xBB)), // blue
        new(2, "Uesugi", new RgbColor(0xD8, 0xA5, 0x2B)), // gold
    };
}
