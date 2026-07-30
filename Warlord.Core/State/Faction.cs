namespace Warlord.Core.State;

/// <summary>A playable clan/faction. Owns provinces on the campaign map.</summary>
public sealed record Faction(int Id, string Name, RgbColor Color);
