using Warlord.Core.Generation;
using Warlord.Core.Persistence;
using Warlord.Core.State;
using Xunit;

namespace Warlord.Tests;

/// <summary>Tests terrain classification and that terrain survives a save/load.</summary>
public class TerrainTests
{
    private static CampaignMapState Gen(int seed = 12345) =>
        MapGenerator.Generate(seed, columns: 64, rows: 64, provinceCount: 40);

    [Fact]
    public void LandCells_AreNeverClassifiedAsWater()
    {
        var map = Gen();
        foreach (var p in map.Provinces)
            foreach (var cell in p.Cells)
                Assert.NotEqual(Terrain.Water, map.TerrainAt(cell));
    }

    [Fact]
    public void SeaCells_AreWater()
    {
        var map = Gen();
        // A cell with no province is sea and must classify as Water.
        for (int y = 0; y < map.Rows; y++)
            for (int x = 0; x < map.Columns; x++)
            {
                var cell = new Cell(x, y);
                if (map.ProvinceAt(cell) is null)
                    Assert.Equal(Terrain.Water, map.TerrainAt(cell));
            }
    }

    [Fact]
    public void GeneratesAVarietyOfTerrainTypes()
    {
        var map = Gen();
        var kinds = new HashSet<Terrain>();
        foreach (var p in map.Provinces)
            foreach (var cell in p.Cells)
                kinds.Add(map.TerrainAt(cell));

        // Expect meaningfully varied terrain, not just one land type.
        Assert.True(kinds.Count >= 3, $"only {kinds.Count} terrain types generated");
        Assert.Contains(Terrain.Coast, kinds); // an island must have coastline
    }

    [Fact]
    public void SameSeed_ProducesSameTerrain()
    {
        var a = Gen(99);
        var b = Gen(99);
        foreach (var p in a.Provinces)
            foreach (var cell in p.Cells)
                Assert.Equal(a.TerrainAt(cell), b.TerrainAt(cell));
    }

    [Fact]
    public void Terrain_SurvivesSaveLoadRoundtrip()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"warlord_terrain_{Guid.NewGuid():N}.db");
        try
        {
            var original = Gen();
            var store = new SaveStore(dbPath);
            store.Save(original);
            var loaded = store.Load();

            foreach (var p in original.Provinces)
                foreach (var cell in p.Cells)
                    Assert.Equal(original.TerrainAt(cell), loaded.TerrainAt(cell));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }
}
