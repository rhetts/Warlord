namespace Warlord.Core.State;

/// <summary>
/// A plain RGB color. Core defines its own so it stays free of any Godot
/// dependency; the Game layer converts this to a Godot Color at draw time.
/// </summary>
public readonly record struct RgbColor(byte R, byte G, byte B);
