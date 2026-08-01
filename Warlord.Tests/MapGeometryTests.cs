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
}
