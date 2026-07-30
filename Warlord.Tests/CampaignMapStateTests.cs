using Warlord.Core.State;
using Xunit;

namespace Warlord.Tests;

/// <summary>
/// Tests the campaign map model directly — no Godot involved. This is the
/// payoff of keeping Warlord.Core engine-independent: the whole simulation
/// is testable as a plain library.
/// </summary>
public class CampaignMapStateTests
{
    private readonly CampaignMapState _map = CampaignMapState.CreateDemo();

    [Fact]
    public void DemoMap_Has12x12Grid()
    {
        Assert.Equal(12, _map.Columns);
        Assert.Equal(12, _map.Rows);
    }

    [Fact]
    public void DemoMap_HasNineProvincesAndThreeFactions()
    {
        Assert.Equal(9, _map.Provinces.Count);
        Assert.Equal(3, _map.Factions.Count);
    }

    [Fact]
    public void EveryCellInGridBelongsToExactlyOneProvince()
    {
        for (int row = 0; row < _map.Rows; row++)
            for (int col = 0; col < _map.Columns; col++)
                Assert.NotNull(_map.ProvinceAt(new Cell(col, row)));

        // 12x12 = 144 cells, no cell claimed twice.
        int totalCells = _map.Provinces.Sum(p => p.Cells.Count);
        Assert.Equal(144, totalCells);
    }

    [Fact]
    public void OffMapCell_HasNoProvince()
    {
        Assert.Null(_map.ProvinceAt(new Cell(-1, 0)));
        Assert.Null(_map.ProvinceAt(new Cell(100, 100)));
        Assert.Null(_map.OwnerAt(new Cell(-1, 0)));
    }

    [Fact]
    public void OwnerAt_MatchesProvinceOwner()
    {
        var cell = new Cell(0, 0);
        var province = _map.ProvinceAt(cell);
        Assert.NotNull(province);

        var ownerViaCell = _map.OwnerAt(cell);
        var ownerViaProvince = _map.OwnerOf(province!);
        Assert.Same(ownerViaProvince, ownerViaCell);
        Assert.Equal(province!.OwnerFactionId, ownerViaCell!.Id);
    }

    [Fact]
    public void EachProvince_HasAResolvableOwner()
    {
        foreach (var province in _map.Provinces)
            Assert.NotNull(_map.OwnerOf(province));
    }

    [Fact]
    public void ChangingOwnership_IsReflectedByLookups()
    {
        var province = _map.ProvinceAt(new Cell(0, 0))!;
        int newOwner = (province.OwnerFactionId + 1) % _map.Factions.Count;

        province.OwnerFactionId = newOwner;

        Assert.Equal(newOwner, _map.OwnerAt(new Cell(0, 0))!.Id);
    }
}
