using Warlord.Core.Generation;
using Warlord.Core.Geometry;
using Xunit;

namespace Warlord.Tests;

/// <summary>Tests coastline extraction + smoothing (pure Core geometry).</summary>
public class MapGeometryTests
{
    private static Warlord.Core.State.CampaignMapState Gen(int seed = 12345) =>
        MapGenerator.Generate(seed, columns: 64, rows: 64, provinceCount: 24);

    [Fact]
    public void ProducesAtLeastOneCoastlineLoop()
    {
        var coasts = MapGeometry.ExtractCoastlines(Gen());
        Assert.NotEmpty(coasts);
        Assert.All(coasts, loop => Assert.True(loop.Count >= 3));
    }

    [Fact]
    public void SmoothingAddsPoints()
    {
        var map = Gen();
        var rough = MapGeometry.ExtractCoastlines(map, smoothingIterations: 0);
        var smooth = MapGeometry.ExtractCoastlines(map, smoothingIterations: 3);

        int roughTotal = rough.Sum(l => l.Count);
        int smoothTotal = smooth.Sum(l => l.Count);
        Assert.True(smoothTotal > roughTotal, "Chaikin smoothing should subdivide the loops");
    }

    [Fact]
    public void IsDeterministicForSameSeed()
    {
        var a = MapGeometry.ExtractCoastlines(Gen(42));
        var b = MapGeometry.ExtractCoastlines(Gen(42));

        Assert.Equal(a.Count, b.Count);
        Assert.Equal(a.Sum(l => l.Count), b.Sum(l => l.Count));
    }

    [Fact]
    public void MountainClustersAreContiguousAndCoverAllMountainCells()
    {
        var map = Gen();
        var clusters = MapGeometry.ExtractMountainClusters(map);

        int totalMountainCells = 0;
        for (int r = 0; r < map.Rows; r++)
            for (int c = 0; c < map.Columns; c++)
                if (map.TerrainAt(new Warlord.Core.State.Cell(c, r)) == Warlord.Core.State.Terrain.Mountains)
                    totalMountainCells++;

        Assert.Equal(totalMountainCells, clusters.Sum(cl => cl.Count));

        // No cluster should contain two cells more than one step apart (8-connectivity).
        foreach (var cluster in clusters)
        {
            var seen = new HashSet<Warlord.Core.State.Cell> { cluster[0] };
            var frontier = new Queue<Warlord.Core.State.Cell>();
            frontier.Enqueue(cluster[0]);
            while (frontier.Count > 0)
            {
                var cell = frontier.Dequeue();
                foreach (var other in cluster)
                {
                    if (seen.Contains(other)) continue;
                    if (Math.Abs(other.Col - cell.Col) <= 1 && Math.Abs(other.Row - cell.Row) <= 1)
                    {
                        seen.Add(other);
                        frontier.Enqueue(other);
                    }
                }
            }
            Assert.Equal(cluster.Count, seen.Count);
        }
    }
}
