namespace Warlord.Core.Generation;

/// <summary>
/// Seeded 2D value noise with fractal (fBm) octaves. Deterministic: the same
/// seed and coordinates always yield the same value, which is what makes map
/// generation reproducible and testable. Self-contained — no external noise
/// library. (FastNoiseLite is the drop-in upgrade when we want OpenSimplex.)
/// </summary>
public sealed class ValueNoise
{
    private readonly int _seed;

    public ValueNoise(int seed) => _seed = seed;

    /// <summary>Integer lattice hash -> [0, 1).</summary>
    private float Hash(int x, int y)
    {
        unchecked
        {
            int h = _seed;
            h = h * 374761393 + x * 668265263;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            h = h * 668265263 + y * 374761393;
            h = (h ^ (h >> 13)) * 1274126177;
            h ^= h >> 16;
            return (h & 0x7FFFFFFF) / (float)int.MaxValue;
        }
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    /// <summary>Bilinearly interpolated value noise at (x, y) -> [0, 1].</summary>
    public float Value(float x, float y)
    {
        int x0 = (int)MathF.Floor(x);
        int y0 = (int)MathF.Floor(y);
        float fx = Smooth(x - x0);
        float fy = Smooth(y - y0);

        float v00 = Hash(x0, y0);
        float v10 = Hash(x0 + 1, y0);
        float v01 = Hash(x0, y0 + 1);
        float v11 = Hash(x0 + 1, y0 + 1);

        float top = v00 + (v10 - v00) * fx;
        float bottom = v01 + (v11 - v01) * fx;
        return top + (bottom - top) * fy;
    }

    /// <summary>Fractal Brownian motion: summed octaves, normalized to [0, 1].</summary>
    public float Fractal(float x, float y, int octaves, float persistence = 0.5f, float lacunarity = 2f)
    {
        float total = 0f, amplitude = 1f, frequency = 1f, maxAmplitude = 0f;
        for (int i = 0; i < octaves; i++)
        {
            total += Value(x * frequency, y * frequency) * amplitude;
            maxAmplitude += amplitude;
            amplitude *= persistence;
            frequency *= lacunarity;
        }
        return total / maxAmplitude;
    }
}
