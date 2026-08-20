using System.Numerics;
using Warlord.Core.Generation;
using Warlord.Core.State;

namespace Warlord.Core.Geometry;

/// <summary>
/// Derives smooth region outlines from the grid. Pure, deterministic geometry
/// (same map -> same polygons), so it lives in Core and is unit-testable; the
/// renderer just projects and draws the polygons it returns.
///
/// Coordinates are in *grid-corner space*: integer point (c, r) is the shared
/// corner of cells (c-1,r-1), (c,r-1), (c-1,r), (c,r). The renderer projects
/// these to isometric screen space.
/// </summary>
public static class MapGeometry
{
    // Fixed noise seed so the cosmetic coastline wobble is stable and reproducible.
    private const int CoastNoiseSeed = 1337;
    private const float PerturbFrequency = 0.35f;

    /// <summary>
    /// Coastline loops (land vs sea), smoothed by Chaikin corner-cutting so the
    /// blocky per-cell staircase becomes rounded curves.
    /// </summary>
    /// <param name="roughness">
    /// Purely cosmetic sub-cell jitter applied to the rendered outline (does not
    /// change which cells are land). 0 = smooth; ~0.5 = gently wavy coast.
    /// </param>
    public static List<List<Vector2>> ExtractCoastlines(
        CampaignMapState map, int smoothingIterations = 3, float roughness = 0f) =>
        BuildSmoothedOutlines(
            map.Columns, map.Rows,
            (c, r) => map.ProvinceAt(new Cell(c, r)) is not null,
            smoothingIterations, roughness, canonicalNormal: false);

    /// <summary>
    /// Groups Mountains cells into contiguous clusters (8-connected, so
    /// diagonal neighbors count), so a whole mountain range can be rendered as
    /// one connected shape instead of one disconnected blob per cell.
    /// </summary>
    public static List<List<Cell>> ExtractMountainClusters(CampaignMapState map)
    {
        var visited = new bool[map.Columns, map.Rows];
        var clusters = new List<List<Cell>>();

        for (int r = 0; r < map.Rows; r++)
        {
            for (int c = 0; c < map.Columns; c++)
            {
                if (visited[c, r] || map.TerrainAt(new Cell(c, r)) != Terrain.Mountains) continue;

                var cluster = new List<Cell>();
                var queue = new Queue<Cell>();
                queue.Enqueue(new Cell(c, r));
                visited[c, r] = true;

                while (queue.Count > 0)
                {
                    var cell = queue.Dequeue();
                    cluster.Add(cell);

                    for (int dr = -1; dr <= 1; dr++)
                    {
                        for (int dc = -1; dc <= 1; dc++)
                        {
                            if (dc == 0 && dr == 0) continue;
                            int nc = cell.Col + dc, nr = cell.Row + dr;
                            if (nc < 0 || nr < 0 || nc >= map.Columns || nr >= map.Rows) continue;
                            if (visited[nc, nr]) continue;
                            if (map.TerrainAt(new Cell(nc, nr)) != Terrain.Mountains) continue;

                            visited[nc, nr] = true;
                            queue.Enqueue(new Cell(nc, nr));
                        }
                    }
                }
                clusters.Add(cluster);
            }
        }
        return clusters;
    }

    /// <summary>
    /// Smoothed outline loops for every province (each province vs. everything
    /// else), so province borders round the same way the coast does. Interior
    /// borders shared by two provinces are traced from both, in opposite
    /// directions — Chaikin alone gives identical points on those shared runs,
    /// but the cosmetic roughness jitter needs <c>canonicalNormal</c> (below) or
    /// the two traces perturb to opposite sides and the border draws doubled.
    /// </summary>
    public static List<List<Vector2>> ExtractProvinceOutlines(
        CampaignMapState map, int smoothingIterations = 3, float roughness = 0f)
    {
        var all = new List<List<Vector2>>();
        foreach (var province in map.Provinces)
        {
            int id = province.Id;
            all.AddRange(BuildSmoothedOutlines(
                map.Columns, map.Rows,
                (c, r) => map.ProvinceAt(new Cell(c, r))?.Id == id,
                smoothingIterations, roughness, canonicalNormal: true));
        }
        return all;
    }

    // Trace the outline of an arbitrary cell region, smoothed and (optionally)
    // roughened — shared by coastline and province-border extraction.
    private static List<List<Vector2>> BuildSmoothedOutlines(
        int columns, int rows, Func<int, int, bool> inRegion, int smoothingIterations, float roughness,
        bool canonicalNormal)
    {
        bool IsLand(int c, int r) => inRegion(c, r);

        // Emit each region boundary edge as a DIRECTED edge, oriented so the
        // land cell is always on the same side (clockwise around each cell).
        // Consistent direction lets us follow the boundary unambiguously and
        // resolves diagonal "pinch" points into separate simple loops instead
        // of self-touching polygons.
        var outgoing = new Dictionary<(int, int), List<(int, int)>>();
        void AddEdge((int, int) a, (int, int) b)
        {
            if (!outgoing.TryGetValue(a, out var list)) outgoing[a] = list = new List<(int, int)>();
            list.Add(b);
        }

        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < columns; c++)
            {
                if (!IsLand(c, r)) continue;
                if (!IsLand(c, r - 1)) AddEdge((c, r), (c + 1, r));         // north
                if (!IsLand(c + 1, r)) AddEdge((c + 1, r), (c + 1, r + 1)); // east
                if (!IsLand(c, r + 1)) AddEdge((c + 1, r + 1), (c, r + 1)); // south
                if (!IsLand(c - 1, r)) AddEdge((c, r + 1), (c, r));         // west
            }
        }

        var noise = new ValueNoise(CoastNoiseSeed);
        var result = new List<List<Vector2>>();
        foreach (var loop in WalkLoops(outgoing))
        {
            var points = loop.Select(p => new Vector2(p.Item1, p.Item2)).ToList();
            points = Chaikin(points, smoothingIterations);
            if (roughness > 0f)
            {
                points = Perturb(points, noise, roughness, PerturbFrequency, canonicalNormal);
                points = Chaikin(points, 1); // soften any spikes the jitter introduced
            }
            result.Add(points);
        }
        return result;
    }

    // Displace each vertex along a local normal by fractal noise, for cosmetic
    // wavy borders. Deterministic (noise is a function of position), so the
    // border is stable frame-to-frame.
    //
    // canonicalNormal=false derives the normal from the loop's own travel
    // direction (prev -> next), which gives a coherent single-direction wobble
    // along one continuous boundary — used for the coastline.
    //
    // canonicalNormal=true instead derives it from the two neighboring points
    // sorted into a fixed order, independent of which way the loop is being
    // walked. A province border shared with a neighbor is traced once per
    // province, in opposite directions; with the travel-direction normal the
    // two traces perturb to opposite sides of the border and it renders as two
    // separate lines with a gap between them. The canonical order makes both
    // traces compute the identical displacement for the identical shared point,
    // so the two lines coincide again.
    private static List<Vector2> Perturb(
        List<Vector2> points, ValueNoise noise, float amplitude, float frequency, bool canonicalNormal)
    {
        int n = points.Count;
        var result = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
        {
            Vector2 prev = points[(i - 1 + n) % n];
            Vector2 next = points[(i + 1) % n];

            Vector2 lo = prev, hi = next;
            if (canonicalNormal && (hi.X < lo.X || (hi.X == lo.X && hi.Y < lo.Y)))
                (lo, hi) = (hi, lo);
            Vector2 tangent = hi - lo;

            float len = tangent.Length();
            Vector2 normal = len > 1e-4f ? new Vector2(tangent.Y, -tangent.X) / len : Vector2.Zero;

            float signed = noise.Fractal(points[i].X * frequency, points[i].Y * frequency, 4) * 2f - 1f;
            result.Add(points[i] + normal * (amplitude * signed));
        }
        return result;
    }

    // Follow the directed edges into closed loops. At a junction (a saddle),
    // prefer turning right, then straight, then left — a consistent wall-follow
    // that yields simple, non-self-intersecting polygons.
    private static List<List<(int, int)>> WalkLoops(Dictionary<(int, int), List<(int, int)>> outgoing)
    {
        var used = new HashSet<((int, int), (int, int))>();
        var loops = new List<List<(int, int)>>();

        foreach (var start in outgoing.Keys)
        {
            foreach (var first in outgoing[start])
            {
                if (used.Contains((start, first))) continue;

                var loop = new List<(int, int)> { start };
                var prev = start;
                var current = first;
                used.Add((start, first));

                while (current != start)
                {
                    loop.Add(current);
                    var next = ChooseNext(outgoing, used, prev, current);
                    if (next is null) break; // dead end — shouldn't happen on a closed coast
                    used.Add((current, next.Value));
                    prev = current;
                    current = next.Value;
                }
                loops.Add(loop);
            }
        }
        return loops;
    }

    // Pick the next directed edge out of `current`, preferring (relative to the
    // incoming direction) a right turn, then straight, then left, then back.
    private static (int, int)? ChooseNext(
        Dictionary<(int, int), List<(int, int)>> outgoing,
        HashSet<((int, int), (int, int))> used,
        (int, int) prev, (int, int) current)
    {
        if (!outgoing.TryGetValue(current, out var candidates)) return null;

        (int dx, int dy) = (current.Item1 - prev.Item1, current.Item2 - prev.Item2);
        var priorities = new (int, int)[]
        {
            (-dy, dx),   // right turn
            (dx, dy),    // straight
            (dy, -dx),   // left turn
            (-dx, -dy),  // back
        };

        foreach (var dir in priorities)
        {
            foreach (var n in candidates)
            {
                if (used.Contains((current, n))) continue;
                if (n.Item1 - current.Item1 == dir.Item1 && n.Item2 - current.Item2 == dir.Item2)
                    return n;
            }
        }
        return null;
    }

    // Chaikin's corner-cutting on a closed loop: replace each corner with two
    // points 1/4 and 3/4 along its edges. Each pass rounds the corners further.
    private static List<Vector2> Chaikin(List<Vector2> points, int iterations)
    {
        for (int it = 0; it < iterations; it++)
        {
            int n = points.Count;
            if (n < 3) break;
            var next = new List<Vector2>(n * 2);
            for (int i = 0; i < n; i++)
            {
                Vector2 a = points[i];
                Vector2 b = points[(i + 1) % n];
                next.Add(a * 0.75f + b * 0.25f);
                next.Add(a * 0.25f + b * 0.75f);
            }
            points = next;
        }
        return points;
    }
}
