namespace Warlord.Core.State;

/// <summary>
/// The logical state of the campaign map: its dimensions, the factions, and the
/// provinces that partition the grid. This is engine-independent — the Godot
/// layer reads it to draw the map but never the other way around. Eventually
/// this becomes part of the full GameState that the SaveStore persists.
/// </summary>
public sealed class CampaignMapState
{
    public int Columns { get; }
    public int Rows { get; }
    public IReadOnlyList<Faction> Factions { get; }
    public IReadOnlyList<Province> Provinces { get; }

    private readonly Dictionary<Cell, Province> _cellToProvince;
    private readonly Dictionary<int, Faction> _factionsById;

    public CampaignMapState(
        int columns,
        int rows,
        IReadOnlyList<Faction> factions,
        IReadOnlyList<Province> provinces)
    {
        Columns = columns;
        Rows = rows;
        Factions = factions;
        Provinces = provinces;

        _factionsById = factions.ToDictionary(f => f.Id);
        _cellToProvince = new Dictionary<Cell, Province>();
        foreach (var province in provinces)
            foreach (var cell in province.Cells)
                _cellToProvince[cell] = province;
    }

    /// <summary>The province containing a cell, or null if the cell is off-map.</summary>
    public Province? ProvinceAt(Cell cell) =>
        _cellToProvince.GetValueOrDefault(cell);

    /// <summary>The faction that owns a province.</summary>
    public Faction? OwnerOf(Province province) =>
        _factionsById.GetValueOrDefault(province.OwnerFactionId);

    /// <summary>The faction that owns the province at a cell, or null if off-map.</summary>
    public Faction? OwnerAt(Cell cell)
    {
        var province = ProvinceAt(cell);
        return province is null ? null : OwnerOf(province);
    }

    /// <summary>
    /// A hand-authored demo map: a 12x12 grid divided into 3x3 provinces,
    /// with three clans scattered diagonally so faction borders are visible.
    /// </summary>
    public static CampaignMapState CreateDemo()
    {
        var factions = Clans.Default;

        const int size = 12;
        const int block = 4; // 3x3 grid of 4x4-cell provinces

        string[] names =
        {
            "Owari", "Mino", "Ise",
            "Kai", "Shinano", "Suruga",
            "Echigo", "Etchu", "Kaga",
        };

        var provinces = new List<Province>();
        int id = 0;
        for (int blockRow = 0; blockRow < size / block; blockRow++)
        {
            for (int blockCol = 0; blockCol < size / block; blockCol++)
            {
                var cells = new List<Cell>();
                for (int r = 0; r < block; r++)
                    for (int c = 0; c < block; c++)
                        cells.Add(new Cell(blockCol * block + c, blockRow * block + r));

                provinces.Add(new Province
                {
                    Id = id,
                    Name = names[id],
                    OwnerFactionId = (blockCol + blockRow) % 3,
                    Cells = cells,
                });
                id++;
            }
        }

        return new CampaignMapState(size, size, factions, provinces);
    }
}
