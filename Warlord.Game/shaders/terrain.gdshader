shader_type canvas_item;

// Terrain fill shader.
//
// The land is drawn as the smoothed coastline polygon (rounded shape). Its
// color comes from two per-cell lookup textures sampled by UV (0..1 across the
// whole map), with linear filtering so terrain types blend softly between cells.
// Noise adds organic variation so flat regions aren't dead flat.

uniform sampler2D terrain_tex : filter_linear, repeat_disable;
uniform sampler2D faction_tex : filter_linear, repeat_disable;
uniform float noise_scale = 40.0;

float hash(vec2 p)
{
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

float vnoise(vec2 p)
{
    vec2 i = floor(p);
    vec2 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float a = hash(i);
    float b = hash(i + vec2(1.0, 0.0));
    float c = hash(i + vec2(0.0, 1.0));
    float d = hash(i + vec2(1.0, 1.0));
    return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float fbm(vec2 p)
{
    float v = 0.0;
    float a = 0.5;
    for (int i = 0; i < 4; i++)
    {
        v += a * vnoise(p);
        p *= 2.0;
        a *= 0.5;
    }
    return v;
}

void fragment()
{
    vec4 terrain = texture(terrain_tex, UV);
    float n = fbm(UV * noise_scale);
    float shade = 0.82 + 0.32 * n;
    vec3 col = terrain.rgb * shade;

    // Translucent faction ownership tint.
    vec4 faction = texture(faction_tex, UV);
    col = mix(col, faction.rgb, faction.a);

    COLOR = vec4(col, 1.0);
}
