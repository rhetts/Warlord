using Warlord.Core.Persistence;
using Warlord.Core.State;
using Xunit;

namespace Warlord.Tests;

/// <summary>
/// Save -> load roundtrip tests against a real temp SQLite file. Verifies the
/// persistence boundary reconstructs campaign state faithfully.
/// </summary>
public class SaveStoreTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"warlord_test_{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }

    [Fact]
    public void Roundtrip_PreservesGridProvincesAndFactions()
    {
        var original = CampaignMapState.CreateDemo();
        var store = new SaveStore(_dbPath);

        store.Save(original);
        var loaded = store.Load();

        Assert.Equal(original.Columns, loaded.Columns);
        Assert.Equal(original.Rows, loaded.Rows);
        Assert.Equal(original.Provinces.Count, loaded.Provinces.Count);
        Assert.Equal(original.Factions.Count, loaded.Factions.Count);
    }

    [Fact]
    public void Roundtrip_PreservesEveryCellsOwnership()
    {
        var original = CampaignMapState.CreateDemo();
        var store = new SaveStore(_dbPath);
        store.Save(original);
        var loaded = store.Load();

        for (int row = 0; row < original.Rows; row++)
            for (int col = 0; col < original.Columns; col++)
            {
                var cell = new Cell(col, row);
                Assert.Equal(original.OwnerAt(cell)?.Id, loaded.OwnerAt(cell)?.Id);
            }
    }

    [Fact]
    public void Roundtrip_PreservesFactionColors()
    {
        var original = CampaignMapState.CreateDemo();
        var store = new SaveStore(_dbPath);
        store.Save(original);
        var loaded = store.Load();

        foreach (var f in original.Factions)
        {
            var match = loaded.Factions.Single(x => x.Id == f.Id);
            Assert.Equal(f.Color, match.Color);
            Assert.Equal(f.Name, match.Name);
        }
    }

    [Fact]
    public void Save_OverwritesPreviousContent_NoDuplication()
    {
        var store = new SaveStore(_dbPath);
        store.Save(CampaignMapState.CreateDemo());
        store.Save(CampaignMapState.CreateDemo()); // save again into same slot

        var loaded = store.Load();
        Assert.Equal(9, loaded.Provinces.Count); // not 18
        Assert.Equal(144, loaded.Provinces.Sum(p => p.Cells.Count)); // not 288
    }

    [Fact]
    public void ChangedOwnership_Persists()
    {
        var map = CampaignMapState.CreateDemo();
        var province = map.ProvinceAt(new Cell(0, 0))!;
        int newOwner = (province.OwnerFactionId + 1) % map.Factions.Count;
        province.OwnerFactionId = newOwner;

        var store = new SaveStore(_dbPath);
        store.Save(map);
        var loaded = store.Load();

        Assert.Equal(newOwner, loaded.OwnerAt(new Cell(0, 0))!.Id);
    }
}
