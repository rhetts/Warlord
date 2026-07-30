using Warlord.Core.State;

namespace Warlord.Core.Generation;

/// <summary>
/// Procedurally generates a campaign map from a seed. Deterministic: the same
/// seed always produces the same map. Lives in Core (engine-free) and returns
/// the same CampaignMapState the game renders and persists — so it drops in
/// wherever CreateDemo() was used, and nothing downstream changes.
///
/// Pipeline:
///   1. Land/sea via fractal noise with a radial island falloff.
///   2. Provinces via a grid Voronoi partition (each land cell -> nearest seed).
///   3. Lloyd relaxation to even out province sizes.
///   4. Contiguous faction territories via nearest-capital assignment.
/// </summary>
public static class MapGenerator
{
    public static CampaignMapState Generate(
        int seed,
        int columns,
        int rows,
        int provinceCount,
        IReadOnlyList<Faction>? factions = null)
    {
        factions ??= Clans.Default;
        var rng = new Random(seed);
        var noise = new ValueNoise(seed);

        var landCells = BuildLandMask(noise, columns, rows);
        if (landCells.Count == 0)
            throw new InvalidOperationException("Generation produced no land; adjust parameters.");

        var seeds = ScatterSeeds(rng, landCells, provinceCount);
        var cellsBySeed = Partition(landCells, seeds);
        cellsBySeed = Relax(landCells, cellsBySeed, iterations: 2);

        var provinces = BuildProvinces(rng, cellsBySeed);
        AssignFactions(provinces, factions);

        return new CampaignMapState(columns, rows, factions, provinces);
    }

    // 1. Fractal noise minus a radial falloff => an island that fills the middle
    //    and fades to sea at the edges.
    private static List<Cell> BuildLandMask(ValueNoise noise, int columns, int rows)
    {
        const float noiseScale = 0.11f;
        const float seaLevel = 0.35f;

        float cx = (columns - 1) / 2f;
        float cy = (rows - 1) / 2f;
        float maxDist = MathF.Sqrt(cx * cx + cy * cy);

        var land = new List<Cell>();
        for (int y = 0; y < rows; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                float n = noise.Fractal(x * noiseScale, y * noiseScale, octaves: 5);
                float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxDist;
                // No penalty near the center; edges get pushed under the sea.
                float elevation = n - MathF.Max(0f, d - 0.50f) * 1.4f;
                if (elevation > seaLevel)
                    land.Add(new Cell(x, y));
            }
        }
        return land;
    }

    // 2a. Poisson-ish seed points: random land cells with a minimum spacing.
    private static List<Cell> ScatterSeeds(Random rng, List<Cell> land, int count)
    {
        float minSpacing = MathF.Sqrt(land.Count / (float)count) * 0.65f;
        float minSpacingSq = minSpacing * minSpacing;

        var seeds = new List<Cell>();
        int attempts = 0;
        while (seeds.Count < count && attempts < count * 400)
        {
            attempts++;
            var candidate = land[rng.Next(land.Count)];
            bool tooClose = false;
            foreach (var s in seeds)
            {
                if (DistSq(s, candidate) < minSpacingSq) { tooClose = true; break; }
            }
            if (!tooClose)
                seeds.Add(candidate);
        }
        return seeds;
    }

    // 2b. Assign every land cell to its nearest seed => discrete Voronoi cells.
    private static Dictionary<int, List<Cell>> Partition(List<Cell> land, List<Cell> seeds)
    {
        var groups = new Dictionary<int, List<Cell>>();
        for (int i = 0; i < seeds.Count; i++)
            groups[i] = new List<Cell>();

        foreach (var cell in land)
        {
            int best = 0;
            int bestDist = int.MaxValue;
            for (int i = 0; i < seeds.Count; i++)
            {
                int d = DistSq(seeds[i], cell);
                if (d < bestDist) { bestDist = d; best = i; }
            }
            groups[best].Add(cell);
        }
        return groups;
    }

    // 3. Lloyd relaxation: move each seed to its group's centroid (snapped to the
    //    nearest owned land cell) and repartition. Smooths lumpy regions.
    private static Dictionary<int, List<Cell>> Relax(
        List<Cell> land, Dictionary<int, List<Cell>> groups, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            var seeds = new List<Cell>();
            foreach (var group in groups.Values)
            {
                if (group.Count == 0) continue;
                float mx = (float)group.Average(c => c.Col);
                float my = (float)group.Average(c => c.Row);
                seeds.Add(NearestCell(group, mx, my));
            }
            groups = Partition(land, seeds);
        }
        return groups;
    }

    // 4. Build Province objects from non-empty groups, with generated names.
    private static List<Province> BuildProvinces(Random rng, Dictionary<int, List<Cell>> groups)
    {
        var provinces = new List<Province>();
        int id = 0;
        foreach (var group in groups.Values)
        {
            if (group.Count == 0) continue;
            provinces.Add(new Province
            {
                Id = id,
                Name = GenerateName(rng),
                OwnerFactionId = 0, // set by AssignFactions
                Cells = group,
            });
            id++;
        }
        return provinces;
    }

    // Each faction gets a spread-out capital province (farthest-point sampling),
    // then every province is owned by its nearest capital's faction => contiguous
    // starting territories rather than a random checkerboard.
    private static void AssignFactions(List<Province> provinces, IReadOnlyList<Faction> factions)
    {
        var centroids = provinces.Select(Centroid).ToArray();

        var capitals = new List<int> { 0 };
        while (capitals.Count < factions.Count && capitals.Count < provinces.Count)
        {
            int farthest = -1;
            float farthestDist = -1f;
            for (int i = 0; i < provinces.Count; i++)
            {
                if (capitals.Contains(i)) continue;
                float nearest = capitals.Min(c => DistSq(centroids[i], centroids[c]));
                if (nearest > farthestDist) { farthestDist = nearest; farthest = i; }
            }
            capitals.Add(farthest);
        }

        for (int i = 0; i < provinces.Count; i++)
        {
            int nearestCapital = capitals
                .OrderBy(c => DistSq(centroids[i], centroids[c]))
                .First();
            provinces[i].OwnerFactionId = factions[capitals.IndexOf(nearestCapital)].Id;
        }
    }

    private static (float x, float y) Centroid(Province p) =>
        ((float)p.Cells.Average(c => c.Col), (float)p.Cells.Average(c => c.Row));

    private static float DistSq((float x, float y) a, (float x, float y) b)
    {
        float dx = a.x - b.x, dy = a.y - b.y;
        return dx * dx + dy * dy;
    }

    private static int DistSq(Cell a, Cell b)
    {
        int dx = a.Col - b.Col, dy = a.Row - b.Row;
        return dx * dx + dy * dy;
    }

    private static Cell NearestCell(List<Cell> cells, float x, float y)
    {
        Cell best = cells[0];
        float bestDist = float.MaxValue;
        foreach (var c in cells)
        {
            float dx = c.Col - x, dy = c.Row - y;
            float d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = c; }
        }
        return best;
    }

    private static readonly string[] NameA =
        { "Aki", "Owa", "Shi", "Ech", "Kai", "Mi", "Sat", "Hi", "To", "Na", "Ise", "Uzu", "Kan", "Sa", "Bi", "Mu" };
    private static readonly string[] NameB =
        { "no", "ga", "shu", "to", "ma", "wa", "zen", "go", "chi", "bu", "shima", "moto" };

    private static string GenerateName(Random rng) =>
        NameA[rng.Next(NameA.Length)] + NameB[rng.Next(NameB.Length)];
}
