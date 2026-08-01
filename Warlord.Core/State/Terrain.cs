namespace Warlord.Core.State;

/// <summary>
/// Terrain type of a map cell, derived from elevation and moisture during
/// generation. Ordered low-to-high so ranges read naturally. Persisted as its
/// integer value, so DO NOT renumber existing members — only append.
/// </summary>
public enum Terrain
{
    Water = 0,
    Coast = 1,     // low land bordering the sea
    Plains = 2,    // low, dry
    Forest = 3,    // low, wet
    Hills = 4,     // mid elevation
    Mountains = 5, // high elevation
}
