// Real Glass -- dirt on the panes (Glass.shader only; the reflection pass and its pre-pass never see it).
//
// How much of a pane's surface is grime, 0..1 ("cover"), from two layers:
// - THE LAYOUT, procedural, in metres on the facade, the way Real Flag's cloth gets dirty
//   (FlagWear.cginc): each building its own level (some just washed), a slow patchy field over the
//   facade (value noise with the quintic fade, Perlin 2002, summed over octaves, Musgrave et al.
//   1994), and vertical DROPS of panes a little cleaner or dirtier than the next (a facade is
//   washed drop by drop from a gondola), their edges feathered -- no hard step anywhere, so on the
//   panes' own grid (which does not line up with the asset's painted mullions) no seam crosses the
//   glass. Where the asset's painted window was found (close up, PaintedWindow), each pane also
//   takes its own level, its own placing of the fine grain, grime built up along its bottom edge and
//   rain's runs from its top edge -- moved WITHIN the pane (each shape's mean over the pane is 0), so
//   a pane is no dirtier close up than far away.
// - THE FINE GRAIN, one texture of ambientCG's (CC0; DirtTextures.cs): a film and water spots,
//   read twice at two scales and placings (no two panes, no two scales line up), and the runs from
//   a third map. Read with tex2Dgrad and the gradients of the CONTINUOUS metre coordinate: a pane's
//   own placing jumps at its edge, and gradients taken there would pick the coarsest mip (a line).
//
// Everything per pane fades to its mean while the pane is still 16 to 8 pixels wide (the pillow's
// rule): before the painted window's search stops (0.5 m a pixel), and long before TAA could see a
// pane's step crawl. The field's octaves fade likewise when finer than a few pixels; the mips
// average the grain. Faded, every layer is its mean, so a building is as dirty on every tier.
//
// Hashes: GlassCommon.cginc's (Hoskins 2014, arithmetic only, the same on every GPU).

#ifndef REALGLASS_DIRT_INCLUDED
#define REALGLASS_DIRT_INCLUDED

sampler2D _RGDirtGrime;   // 1024^2 DXT5, linear, repeating: a the film, g water spots
sampler2D _RGDirtRuns;    // 1024 x 512 Alpha8, linear: rain's runs down from the top edge (v = 1)
float4 _RGDirtMeans;      // the maps' averages: x film, y spots, z runs
// The amount: GlassSurf.dirt (a building's own from its glass slot, else _RGDirt.x; GlassCommon.cginc).

// The tile sizes (m) of the two reads of the grain and of the runs across a pane.
#define RG_DIRT_TILE1 1.9
#define RG_DIRT_TILE2 1.15
#define RG_DIRT_RUNS_ACROSS 0.9

// A dirty building's grime at its most (its amount 1, built up all the way): x of 1 - e^-x. Kept low
// (the author's call after a first look in the game, 2026-11-13: subtle, just noticeable).
#define RG_DIRT_MOST 0.45

float DirtValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = p - i;
    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);   // quintic
    float a = Hash12(i);
    float b = Hash12(i + float2(1.0, 0.0));
    float c = Hash12(i + float2(0.0, 1.0));
    float d = Hash12(i + float2(1.0, 1.0));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// An octave of wavelength lambda (m) counts while it is 4+ pixels wide, fading to its mean (0.5) below.
float DirtOctave(float2 m, float lambda, float2 o, float footprint)
{
    float vis = saturate(lambda / max(footprint * 4.0, 1e-4) - 1.0);
    return lerp(0.5, DirtValueNoise(m / lambda + o), vis);
}

struct DirtIn
{
    float3 P;           // world
    float3 n;           // the facade's normal
    float3 t;           // along the facade, level
    float footprint;    // metres a pixel covers
    float building;     // the building's id (IdOf)
    float facade;       // its facade's id (FacadeId)
    float2 paneCell;    // the pane (PaneNormal)
    float4 paneLocal;   // the pixel in its pane (xy 0..1, y up), the pane's size (zw, m)
    float painted;      // 1 where the pane is the asset's painted window's
    float4 grad;        // the metre coordinate's gradients: xy d(along, up)/dx, zw /dy -- taken outside any branch
    float flat;         // 1 on flat, upright glass (RoomShape, the rooms' guard), 0 on curved or sloped
    float own;          // 1: the amount is the building's own (the building window), else the Glass tab's share
    float builtUp;      // 0 just washed by rain .. 1 built up all the way (DirtWeather; 1 with Dynamic dirt off; not for an own amount)
};

// Everything laid out along the facade (mean 1): the patchy field, the drops, the grain, a painted
// pane's own. m: the metre coordinate (along, up); seed / bseed: the building's and facade's hashes.
float DirtLayout(DirtIn d, float2 m, float2 seed, float bseed)
{
    float2 dmx = d.grad.xy, dmy = d.grad.zw;

    // The slow patchy field (mean 1): wavelengths 9, 4 and 1.7 m.
    float field = 0.5 * DirtOctave(m * float2(1.0, 0.7), 9.0, seed, d.footprint) +
                  0.3 * DirtOctave(m * float2(1.0, 0.8), 4.0, seed + 17.0, d.footprint) +
                  0.2 * DirtOctave(m, 1.7, seed + 41.0, d.footprint);
    field = 0.6 + 0.8 * field;   // gentle: a facade weathers fairly evenly (2026-11-13: 0.25 + 1.5 read as camouflage)

    // The drops: vertical bands one or two panes wide, each its own (mean 1), feathered over ~0.3 m.
    float dropW = max(_RGPane.x, 0.5) * (bseed > 0.5 ? 2.0 : 1.0);
    float col = m.x / dropW - 0.5;
    float ci = floor(col);
    float e = saturate(0.15 / dropW);
    float drop = lerp(Hash12(float2(ci, seed.x)), Hash12(float2(ci + 1.0, seed.x)), smoothstep(0.5 - e, 0.5 + e, col - ci));
    drop = lerp(1.0, 0.6 + 0.8 * drop, saturate(dropW / max(d.footprint * 6.0, 1e-4) - 1.0));

    // The grain (mean 1): the film twice (two scales, one mirrored), the spots once.
    float2 uv1 = frac(m / RG_DIRT_TILE1 + seed * 0.37);
    float2 uv2 = frac(float2(-m.x, m.y) / RG_DIRT_TILE2 + seed.yx * 0.53);
    float4 g1 = tex2Dgrad(_RGDirtGrime, uv1, dmx / RG_DIRT_TILE1, dmy / RG_DIRT_TILE1);
    float4 g2 = tex2Dgrad(_RGDirtGrime, uv2, float2(-dmx.x, dmx.y) / RG_DIRT_TILE2, float2(-dmy.x, dmy.y) / RG_DIRT_TILE2);
    float filmMean = max(_RGDirtMeans.x, 0.01), spotsMean = max(_RGDirtMeans.y, 0.01);
    float grain = 0.55 * (0.6 * g1.a + 0.4 * g2.a) / filmMean + 0.45 * g1.g / spotsMean;

    // A painted pane's own: its level, its own placing of the grain, the bottom edge, the runs.
    float w = d.paneLocal.z, h = d.paneLocal.w;
    float vPane = d.painted * saturate(min(w, h) / max(d.footprint * 8.0, 1e-4) - 1.0);
    float vertical = saturate((1.0 - abs(d.n.y)) * 3.0);   // roof glass has no "down"
    float pane = 1.0;
    [branch]
    if (vPane > 0.0)
    {
        float2 key = d.paneCell + float2(d.facade * 7.0, d.building * 0.0131);
        float4 r = float4(Hash22(key), Hash22(key.yx + 53.7));
        // Mean 1, spread wide: most panes a light film, some nearly clean, a few much worse
        // (2026-11-13: not every window the same).
        float own = 4.0 * r.x * r.x * r.x;
        float2 uv3 = frac(m / RG_DIRT_TILE1 + r.yz * 13.0);
        float4 g3 = tex2Dgrad(_RGDirtGrime, uv3, dmx / RG_DIRT_TILE1, dmy / RG_DIRT_TILE1);
        float grain3 = 0.55 * g3.a / filmMean + 0.45 * g3.g / spotsMean;

        // Grime along the bottom edge, where water stands and drains: e^(-y / 0.1 m), less its mean
        // over the pane's height.
        float yb = d.paneLocal.y * h;
        float bottom = exp(-yb / 0.1) - (0.1 / h) * (1.0 - exp(-h / 0.1));
        // Rain's runs from the top edge (the map's v = 1), across the pane at its own placing.
        float2 uvR = float2(frac(d.paneLocal.x * w / RG_DIRT_RUNS_ACROSS + r.w * 7.0), clamp(d.paneLocal.y, 0.002, 0.998));
        float runs = tex2Dgrad(_RGDirtRuns, uvR, float2(dmx.x / RG_DIRT_RUNS_ACROSS, dmx.y / h), float2(dmy.x / RG_DIRT_RUNS_ACROSS, dmy.y / h)).a;
        // Some panes streaked, most hardly. At most 0.6 of the runs' swing: where the map is empty the
        // pane keeps 0.4 of its film -- a run ADDS grime, it never washes a pane clean (2026-11-13:
        // 1.6 left black holes at the panes' feet).
        float runsShare = 0.6 * r.z * r.z;
        float shape = max(1.0 + vertical * (1.2 * bottom + runsShare * (runs / max(_RGDirtMeans.z, 0.01) - 1.0)), 0.0);

        grain = lerp(grain, grain3, vPane);
        pane = lerp(1.0, own * shape, vPane);
    }
    return field * drop * grain * pane;
}

// The grime's cover at this pixel, 0..1. amount: the Glass tab's Dirt -- the SHARE of buildings that
// are dirty, as the RGB lights' rows are the share of rooms -- or, own, the building's own amount.
float DirtCover(DirtIn d, float amount)
{
    // Which buildings are dirty, and how much: each its own two draws. A building whose draw is
    // under the share is dirty, the others clean; a dirty one's amount is skewed (most a little,
    // a few much worse). A building given an amount of its own is dirty by exactly that.
    float pick = Hash12(float2(d.building * 0.0191 + 0.7, 11.7));
    float howMuch = Hash12(float2(d.building * 0.0233 + 5.1, 2.9));
    float level = d.own > 0.5 ? amount : (pick < amount ? 0.2 + 0.8 * howMuch * howMuch : 0.0);
    // The rain washes the Glass tab's dirt only: a building's own amount (its window) is set by hand
    // and stays as set, Dynamic dirt or not (the author's call, 2026-11-14).
    level *= d.own > 0.5 ? 1.0 : d.builtUp;
    if (level <= 0.0)
        return 0.0;

    float2 m = float2(dot(d.P, d.t), d.P.y);
    float2 seed = Hash22(float2(d.building * 0.0137 + 3.1, d.facade * 1.71 + 0.3)) * 97.0;
    float bseed = Hash12(float2(d.building * 0.0173, 5.3));

    // On curved or sloped glass "along the facade" is no length (t turns with a smooth-shaded
    // normal: dot(P, t) follows where the building stands, not the wall -- thin stripes on a round
    // tower, 2026-11-13): there every layer laid out along it is its mean, an even film at the
    // building's level, as the rooms stand back from such glass -- and none of it is worked out.
    float flat = saturate(d.flat);
    float layout = 1.0;
    [branch]
    if (flat > 0.0)
        layout = lerp(1.0, DirtLayout(d, m, seed, bseed), flat);

    float x = RG_DIRT_MOST * level * layout;
    return 1.0 - exp(-x);
}

#endif
