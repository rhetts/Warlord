namespace Warlord.Core.State;

/// <summary>A logical grid coordinate on the campaign map.</summary>
public readonly record struct Cell(int Col, int Row);

/// <summary>
/// A province: a named region of the map made up of grid cells, owned by one
/// faction. Ownership is mutable (provinces change hands); its shape is not.
/// </summary>
public sealed class Province
{
    public required int Id { get; init; }
    public required string Name { get; init; }
    public required int OwnerFactionId { get; set; }
    public required IReadOnlyList<Cell> Cells { get; init; }
}
