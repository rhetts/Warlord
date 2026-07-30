using Warlord.Core.Generation;
using Warlord.Core.State;
using Xunit;

namespace Warlord.Tests;

/// <summary>
/// Tests the procedural generator. The headline property is determinism:
/// same seed => identical map. Everything else (contiguity, coverage) rides
/// on the generated CampaignMapState honoring the same invariants as any map.
/// </summary>
public class MapGeneratorTests
{
    private static CampaignMapState Gen(int seed = 12345) =>
        MapGenerator.Generate(seed, columns: 32, rows: 32, provinceCount: 16);

    [Fact]
    public void SameSeed_ProducesIdenticalMap()
    {
        var a = Gen(777);
        var b = Gen(777);

        Assert.Equal(a.Provinces.Count, b.Provinces.Count);
        for (int y = 0; y < a.Rows; y++)
            for (int x = 0; x < a.Columns; x++)
            {
                var cell = new Cell(x, y);
                Assert.Equal(a.ProvinceAt(cell)?.Name, b.ProvinceAt(cell)?.Name);
                Assert.Equal(a.OwnerAt(cell)?.Id, b.OwnerAt(cell)?.Id);
            }
    }

    [Fact]
    public void DifferentSeeds_ProduceDifferentMaps()
    {
        var a = Gen(1);
        var b = Gen(2);

        int differences = 0;
        for (int y = 0; y < a.Rows; y++)
            for (int x = 0; x < a.Columns; x++)
            {
                var cell = new Cell(x, y);
                if (a.OwnerAt(cell)?.Id != b.OwnerAt(cell)?.Id) differences++;
            }
        Assert.True(differences > 50, $"expected distinct maps, only {differences} cells differed");
    }

    [Fact]
    public void EveryProvince_HasCellsAndAResolvableOwner()
    {
        var map = Gen();
        Assert.NotEmpty(map.Provinces);
        foreach (var p in map.Provinces)
        {
            Assert.NotEmpty(p.Cells);
            Assert.NotNull(map.OwnerOf(p));
        }
    }

    [Fact]
    public void EveryFaction_OwnsAtLeastOneProvince()
    {
        var map = Gen();
        foreach (var faction in map.Factions)
            Assert.Contains(map.Provinces, p => p.OwnerFactionId == faction.Id);
    }

    [Fact]
    public void ProducesBothLandAndSea()
    {
        var map = Gen();
        int land = map.Provinces.Sum(p => p.Cells.Count);
        int total = map.Columns * map.Rows;

        Assert.True(land > 0, "map has no land");
        Assert.True(land < total, "map has no sea (island falloff not applied?)");
    }

    [Fact]
    public void NoCell_BelongsToTwoProvinces()
    {
        var map = Gen();
        var seen = new HashSet<Cell>();
        foreach (var p in map.Provinces)
            foreach (var cell in p.Cells)
                Assert.True(seen.Add(cell), $"cell {cell} claimed by two provinces");
    }
}
