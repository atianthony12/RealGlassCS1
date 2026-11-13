// Real Glass -- facade cards (FacadeCards.cs; docs: a preview round, 2026-11-12): each building
// asset's four sides and its roof drawn once, orthographic, into tiles of an atlas, and per building
// standing in the city map where its card is. A reflected ray that the screen cannot answer walks the
// city from above (CityHit, CityWalk.cginc); where it meets a building that has a card, CardLookup
// meets the ray with that building's box and reads the texel of its facade there, and CardRadiance
// lights it as the frame is lit: the building's real facade in place of a guess.
//
// The idea is Unreal's Lumen's: "Screen Traces are done first, followed by a more reliable method"
// -- a coarse scene to trace and a Surface Cache of cards holding each mesh "from multiple angles"
// to shade what the trace meets (Epic Games, "Lumen Technical Details", Unreal Engine 5
// documentation). Ours: the coarse scene is the city map, the cards one tile per side of the asset's
// bounding box. The ray against the box: the slab test (Kay & Kajiya 1986).
//
// Read only by the passes that are compiled with RG_FACADE_CARDS (GlassReflection.shader pass 0,
// CityReflection.shader pass 0); the mod never enables it this round.

#ifndef REALGLASS_FACADECARDS_INCLUDED
#define REALGLASS_FACADECARDS_INCLUDED

#include "CityWalk.cginc"

sampler2D _RGCardAtlas;    // rgb the albedo (an sRGB target), a the share of the texel that is glass -- both x the drawn share
sampler2D _RGCardLight;    // r the windows that glow day and night (b <= 0.48, as b / 0.48), g the share with b in 0.48..0.75
                           // (band 1's), b the sum of their b, a the share of the texel the mesh covers
sampler2D _RGCardSpec;     // r the specular (1 - _XYSMap.z), g the share with b over 0.75 (band 2's), b the sum of their b,
                           // a the sum of every b -- all x the share drawn
sampler2D _RGCardRecords;  // per building three texels (position, base and heading, slot), then per slot three (the box)
float4 _RGCardInfo;        // x the atlas's texels a side, y a tile's width, z its height (texels), w tiles a row
float4 _RGCardInfo2;       // x the records' width, y their height, z the slots' first row, w 1 while cards are bound
float4 _RGCardOrigin;      // xy the world point the records' positions count from (x, z), z the highest mip read, w -
float4 _RGCardInfo3;       // the small tiles (far buildings): x the first small slot (0-based), yz their width and height
                           // (texels; y 0: one kind of tile), w the atlas row they start at
float4 _RGCardSun;         // rgb the main light (the sun, the moon at night) on a face square to it, as the game's building shader receives it
float4 _RGCardSunDir;      // xyz toward that light (world)
float4 _RGCardAmbient;     // rgb the ambient light on an upright face
float4 _RGCardAmbientUp;   // rgb the ambient light on a roof
float4 _RGCardLamp;        // rgb a window the game lights at night (its palette's colour in the game)
float4 _RGCardNight;       // x 1: the lit-window rule run on the illumination value filtered to the footprint (the
                           // spec atlas's a); 0: on the four bins (the preview's choice, round 2's R5)
float4 _RGCardMode;        // x 1: no refinement, the walk's point put on the nearest face (the preview's check); 0: the ray met with the box;
                           // y 1: the cards' glass lit along the ray (the preview's check); 0: as the camera sees it
sampler2D _RGCardGain;     // 3 x 1 (FacadeCardGain.shader's REDUCE): the walls' sums (frame, card lit; frame, card shaded), the
                           // glass's, the four weights (walls lit, walls shaded, glass lit, glass shaded)
float4 _RGCardGainInfo;    // x 1: the cards' level from the frame (0: off, a card as its formula lights it), y the least weight a class
                           // needs, z the lowest gain, w the highest

// The frame's light and the glass's look: GlassCommon.cginc has them where it is included.
#ifndef REALGLASS_COMMON_INCLUDED
float4 _RGGlass;
float4 _RGGlass2;
float4 _RGTint;
float4 _RGRefl;
float4 _RGFill;
float4 _RGLight;
float4 _RGSunDir;
float4 _RGAmbSky;
float4 _RGColorMap;
samplerCUBE _RGSky;
#endif
#ifndef REALGLASS_INTERIORS_INCLUDED
float4 _SimulationTime2;
float _GiantMarshmallow;
#endif

// The game's own buildings take their sheen from the theme's static cube (the global
// _EnvironmentCubemap, RenderProperties.m_cubemap), not the live sky: the card does too. On Metal the
// live sky stands in (one sampler a texture there, and the readers sit at 15 / 14 of 16).
#if defined(SHADER_API_METAL)
#define RG_CARD_ENV _RGSky
#else
samplerCUBE _EnvironmentCubemap;
#define RG_CARD_ENV _EnvironmentCubemap
#endif

struct CardSample
{
    float found;      // 1: a card answered
    float fine;       // 1: the walk's point was within a cell of the box (the fine map's hit), 0: further (a coarse block's)
    float3 albedo;
    float glass;      // the share of it that is glass
    float spec;       // its specular, as the game reads it (1 - _XYSMap.z)
    float glow;       // windows that glow day and night, b / 0.48
    float4 lit;       // the windows the game can light at night: band 1's share and mean value, band 2's share and mean
    float bMean;      // the illumination value's mean over the footprint, one level finer
    float3 n;         // the face's normal (world)
    float t;          // metres along the ray to the point met
    float3 at;        // that point (world)
};

float CardU16(float2 hiLo)
{
    float2 b = floor(hiLo * 255.0 + 0.5);
    return b.x * 256.0 + b.y;
}

// Where a slot's face tile is (FacadeCards.TileRect): its size and corner (atlas texels) and the highest
// level that keeps a read inside it. Full-size slots first; from _RGCardInfo3.x on, the small region's.
void CardTile(float s0, float face, out float2 tile, out float2 corner, out float cap)
{
    if (_RGCardInfo3.y > 0.5 && s0 >= _RGCardInfo3.x)
    {
        tile = _RGCardInfo3.yz;
        float perRow = floor(_RGCardInfo.x / tile.x);
        float ks = (s0 - _RGCardInfo3.x) * 5.0 + face;
        corner = float2(fmod(ks, perRow), floor(ks / perRow)) * tile + float2(0.0, _RGCardInfo3.w);
        cap = max(log2(min(tile.x, tile.y)) - 1.0, 0.0);
    }
    else
    {
        tile = _RGCardInfo.yz;
        float k = s0 * 5.0 + face;
        corner = float2(fmod(k, _RGCardInfo.w), floor(k / _RGCardInfo.w)) * tile;
        cap = _RGCardOrigin.z;
    }
}

float4 CardTexel(float x, float y)
{
    return tex2Dlod(_RGCardRecords, float4((float2(x, y) + 0.5) / max(_RGCardInfo2.xy, 1.0), 0.0, 0.0));
}

// Dave Hoskins, "Hash without Sine" (MIT), as GlassCommon.cginc's Hash12.
float CardHash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float3 SafeNormalizeCard(float3 v, float3 fallback)
{
    float l = dot(v, v);
    return l > 1e-12 ? v * rsqrt(l) : fallback;
}

// The game's rule for a window it lights at night (b > 0.48), as Interiors.cginc's GameWindowLit.
float CardWindowLit(float b, float2 colourIndex, float buildingId)
{
    float2 threshold = sin(colourIndex * 31.3 + _SimulationTime2.w * float2(11.0, 7.0)) * 0.125 + float2(0.615, 0.885);
    float alpha = CardHash12(float2(fmod(buildingId, 997.0) + 0.5, floor(buildingId / 997.0) + 0.71));
    float on = saturate((alpha + 0.01 - _GiantMarshmallow) * 50.0);
    on = on * on * (3.0 - 2.0 * on);
    float2 band = saturate(float2(threshold.x - b, b - threshold.y) * 5.0);
    band = band * band * (3.0 - 2.0 * band);
    return (band.x + band.y) * on;
}

// The card of the building the walk met (hitId, tHit along R from origin): its record, the ray met
// with its box in the building's own frame -- the side it enters by, or the roof it comes down on --
// and the texel there, read at the level the ray's footprint covers (cone: metres of footprint a
// metre of the ray, start: metres at its origin). Not found: no card for that building, or the ray
// misses its box (a coarse block's hit names that block's tallest building, not always the one the
// ray meets): the caller keeps its guess then.
void CardLookup(float3 origin, float3 R, float tHit, float hitId, float cone, float start, out CardSample s)
{
    s.found = 0.0;
    s.fine = 0.0;
    s.albedo = float3(0.0, 0.0, 0.0);
    s.glass = 0.0;
    s.spec = 0.0;
    s.glow = 0.0;
    s.lit = float4(0.0, 0.0, 0.0, 0.0);
    s.bMean = 0.0;
    s.n = float3(0.0, 1.0, 0.0);
    s.t = tHit;
    s.at = origin + R * tHit;

    float col = fmod(hitId, 256.0) * 3.0, row = floor(hitId / 256.0);
    float slot = CardU16(CardTexel(col + 2.0, row).rg);
    if (_RGCardInfo2.w > 0.5 && slot > 0.5)
    {
        float4 r0 = CardTexel(col, row);
        float4 r1 = CardTexel(col + 1.0, row);
        float s0 = slot - 1.0;
        float scol = fmod(s0, 256.0) * 3.0, srow = _RGCardInfo2.z + floor(s0 / 256.0);
        float4 b0 = CardTexel(scol, srow);
        float4 b1 = CardTexel(scol + 1.0, srow);
        float4 b2 = CardTexel(scol + 2.0, srow);
        float3 mn = float3(CardU16(b0.rg), CardU16(b0.ba), CardU16(b1.rg)) / 32.0 - 1024.0;
        float3 mx = float3(CardU16(b1.ba), CardU16(b2.rg), CardU16(b2.ba)) / 32.0 - 1024.0;
        float2 pos = float2(CardU16(r0.rg), CardU16(r0.ba)) / 8.0 + _RGCardOrigin.xy;
        float baseY = CardU16(r1.rg) / 16.0 - 512.0;
        float angle = CardU16(r1.ba) * (6.28318530718 / 65536.0);
        float ca = cos(angle), sa = sin(angle);
        float2 ax = float2(ca, -sa), az = float2(sa, ca);   // the building's x and z axes (world xz)

        float3 d = origin - float3(pos.x, baseY, pos.y);
        float3 o = float3(dot(d.xz, ax), d.y, dot(d.xz, az));
        float3 rl = float3(dot(R.xz, ax), R.y, dot(R.xz, az));
        float3 safe = float3(abs(rl.x) > 1e-6 ? rl.x : 1e-6, abs(rl.y) > 1e-6 ? rl.y : 1e-6, abs(rl.z) > 1e-6 ? rl.z : 1e-6);
        float3 ta = (mn - o) / safe, tb = (mx - o) / safe;
        float3 tlo = min(ta, tb), thi = max(ta, tb);
        float tEnter = max(max(tlo.x, tlo.y), tlo.z);
        float tLeave = min(min(thi.x, thi.y), thi.z);

        // The walk's own point and how far it is from the box: a fine cell's hit lies on it or a cell
        // off; a coarse block's may be anywhere in its 32 m.
        float3 hl = o + rl * tHit;
        float far = length(max(max(mn - hl, hl - mx), 0.0));
        s.fine = far < 6.0 ? 1.0 : 0.0;

        float face = -1.0;
        float3 p = hl;
        float t = tHit;
        if (_RGCardMode.x < 0.5)
        {
            // The ray against the box: the face it enters by (none from inside it, or from below).
            if (tEnter <= tLeave && tEnter > 0.0)
            {
                t = tEnter;
                p = o + rl * t;
                if (tlo.x >= tlo.y && tlo.x >= tlo.z)
                    face = rl.x > 0.0 ? 3.0 : 1.0;
                else if (tlo.z >= tlo.y)
                    face = rl.z > 0.0 ? 0.0 : 2.0;
                else
                    face = rl.y < 0.0 ? 4.0 : -1.0;
            }
        }
        else if (far < 6.0)
        {
            // No refinement (the preview's check of what it gains): the walk's point, onto the face
            // of the box nearest it among those the ray can meet.
            p = clamp(hl, mn, mx);
            float best = 1e9;
            float dist = p.z - mn.z;
            if (rl.z > 0.0 && dist < best) { best = dist; face = 0.0; }
            dist = mx.x - p.x;
            if (rl.x < 0.0 && dist < best) { best = dist; face = 1.0; }
            dist = mx.z - p.z;
            if (rl.z < 0.0 && dist < best) { best = dist; face = 2.0; }
            dist = p.x - mn.x;
            if (rl.x > 0.0 && dist < best) { best = dist; face = 3.0; }
            dist = mx.y - p.y;
            if (rl.y < 0.0 && dist < best) { best = dist; face = 4.0; }
        }

        if (face > -0.5)
        {
            // The face's own (u, v) and size, as FacadeCards.Frame lays them out.
            float3 ext = max(mx - mn, 0.01);
            float3 q = saturate((p - mn) / ext);
            float2 uv = float2(q.x, q.y);
            float2 size = ext.xy;
            float3 nl = float3(0.0, 0.0, -1.0);
            if (face > 0.5 && face < 1.5) { uv = float2(q.z, q.y); size = ext.zy; nl = float3(1.0, 0.0, 0.0); }
            else if (face > 1.5 && face < 2.5) { uv = float2(1.0 - q.x, q.y); nl = float3(0.0, 0.0, 1.0); }
            else if (face > 2.5 && face < 3.5) { uv = float2(1.0 - q.z, q.y); size = ext.zy; nl = float3(-1.0, 0.0, 0.0); }
            else if (face > 3.5) { uv = float2(q.x, q.z); size = ext.xz; nl = float3(0.0, 1.0, 0.0); }

            // The tile, and the level the ray's footprint there covers; the read kept half a texel of
            // that level inside the tile (tiles sit on their own size's grid: no level up to the cap
            // mixes two of them).
            float2 tile, corner;
            float cap;
            CardTile(s0, face, tile, corner, cap);
            float2 texelM = size / tile;
            float footprint = start + cone * t;
            float lod = clamp(log2(max(footprint / min(texelM.x, texelM.y), 1.0)), 0.0, cap);
            float halfTexel = 0.5 * exp2(ceil(lod));
            float2 inTile = clamp(uv * tile, halfTexel, tile - halfTexel);
            float2 auv = (corner + inTile) / _RGCardInfo.x;
            float4 A = tex2Dlod(_RGCardAtlas, float4(auv, 0.0, lod));
            float4 B = tex2Dlod(_RGCardLight, float4(auv, 0.0, lod));
            float4 S = tex2Dlod(_RGCardSpec, float4(auv, 0.0, lod));
            float cover = max(B.a, 1e-3);
            s.found = B.a > 0.02 ? 1.0 : 0.0;
            s.albedo = A.rgb / cover;
            s.glass = saturate(A.a / cover);
            s.spec = saturate(S.r / cover);
            s.glow = saturate(B.r / cover);
            s.lit = float4(saturate(B.g / cover), B.b / max(B.g, 1e-4), saturate(S.g / cover), S.b / max(S.g, 1e-4));
            if (_RGCardNight.x > 0.5)
            {
                float4 S1 = tex2Dlod(_RGCardSpec, float4(auv, 0.0, max(lod - 1.0, 0.0)));
                float4 B1 = tex2Dlod(_RGCardLight, float4(auv, 0.0, max(lod - 1.0, 0.0)));
                s.bMean = saturate(S1.a / max(B1.a, 1e-3));
            }
            s.n = nl.x * float3(ax.x, 0.0, ax.y) + float3(0.0, nl.y, 0.0) + nl.z * float3(az.x, 0.0, az.y);
            s.t = t;
            s.at = origin + R * t;
        }
    }
}

// The way the card's glass is looked at: as the CAMERA sees that point (what the screen shows of the
// same building -- the screen trace's answer, beside the card's on the water and the glass -- so the
// two agree where both can answer), or (_RGCardMode.y = 1, the preview's check) along the ray.
float3 CardView(CardSample c, float3 R)
{
    return _RGCardMode.y > 0.5 ? R : SafeNormalizeCard(c.at - _WorldSpaceCameraPos, R);
}

// What the card's glass mirrors goes this way: the caller looks below the horizon (the ground at its
// feet, as the glass's GroundFill does) and passes it to CardRadiance.
float3 CardMirror(CardSample c, float3 R)
{
    return reflect(CardView(c, R), c.n);
}

// The card's texel lit as the frame is, the way the game and the glass draw that building: its
// albedo in the sun (N.L, no shadow) and the ambient; the windows that glow, and those the game lights
// at night by its own rule (the building's colour index from its id); where it is glass, the glass as
// Glass.shader draws it -- the dark inside by day, the game's window by night, and what it mirrors:
// the sky's cube above the horizon, below it what the caller passes. No fog, no shadows.
float3 CardRadiance(CardSample c, float3 R, float buildingId, float roughness, float3 below)
{
    float nl = saturate(dot(c.n, _RGCardSunDir.xyz));
    float3 ambient = c.n.y > 0.5 ? _RGCardAmbientUp.rgb : _RGCardAmbient.rgb;
    // The light on the face: the main light and the ambient as the frame's own building shader
    // receives them (FacadeCards.SetLight: measured from the frame, not fitted).
    float3 light = _RGCardSun.rgb * nl + ambient;
    float3 lit = c.albedo * light;

    float2 ci = float2((fmod(buildingId, _RGColorMap.x) + 0.5) / max(_RGColorMap.x, 1.0),
                       (floor(buildingId / max(_RGColorMap.x, 1.0)) + 0.5) / max(_RGColorMap.y, 1.0));
    // The windows the game lights: band 1's bin and band 2's, each lit by the rule at its own mean
    // value (c.lit: share 1, mean 1, share 2, mean 2).
    float windows = c.lit.x * CardWindowLit(c.lit.y, ci, buildingId) + c.lit.z * CardWindowLit(c.lit.w, ci, buildingId);
    float3 emission = c.albedo * (c.glow * 9.6) + _RGCardLamp.rgb * windows;
    if (_RGCardNight.x > 0.5)
    {
        // The illumination map's value filtered to the footprint (its mean over the texel, one level
        // finer: the game reads its own map with a mip bias of -1), the rule run on that value.
        float bF = c.bMean;
        emission = bF > 0.48 ? _RGCardLamp.rgb * CardWindowLit(bF, ci, buildingId) : c.albedo * (20.0 * bF);
    }
    // The game's own reflection on every surface of its buildings: cube x 0.2 x spec x (albedo +
    // 0.25), the cube read 8 x (1 - spec) mips down -- and, as its final pass has it, LIT as the albedo is
    // (out = (light + ambient) x (albedo + sheen) + sheen + emission: Custom/Buildings/Building/Default,
    // its GLCore program read 2026-11-12).
    float3 V = CardView(c, R);
    float3 Rn = reflect(V, c.n);
    float3 sheen = Finite(texCUBElod(RG_CARD_ENV, float4(Rn, 8.0 * (1.0 - c.spec))).rgb) * 0.2 * c.spec * (c.albedo + 0.25);
    float3 game = (c.albedo + sheen) * light + sheen + emission;

    // The glass (Glass.shader's, without its panes, rooms and glint): the coat's colour from the tint
    // (twice through it, the outer surface's 4% untinted), Schlick's rise to the glancing setting.
    float3 own = c.albedo / max(max(c.albedo.r, max(c.albedo.g, c.albedo.b)), 0.02);
    float3 tint = lerp(_RGTint.rgb, own, _RGTint.a);
    tint /= max(max(tint.r, max(tint.g, tint.b)), 0.02);
    float tl = dot(tint, float3(0.2126, 0.7152, 0.0722));
    float3 filter = tint / max(tl, 0.05);
    float3 twice = tint * tint;
    twice /= max(dot(twice, float3(0.2126, 0.7152, 0.0722)), 0.05);
    float F0 = _RGGlass.x;
    float outer = saturate(0.04 / max(F0, 1e-4));
    float3 coat = min(outer + (1.0 - outer) * twice, 1.0 / max(F0, 1e-4));
    float m = 1.0 - saturate(dot(c.n, -V));
    float m2 = m * m;
    float f5 = m2 * m2 * m;
    float Fmax = max(_RGGlass.y, F0);
    float Fs = F0 + (Fmax - F0) * f5;
    // GlassFresnel's colour: the tinted coat head-on, silvery toward edge-on as far as _RGGlass2.x says.
    float3 F = lerp(Fs * coat, F0 * coat + (Fmax - F0 * coat) * f5, _RGGlass2.x);

    float3 mirrored = Rn.y > 0.0 ? Finite(texCUBElod(_RGSky, float4(Rn, max(roughness, 0.1) * _RGFill.z)).rgb) : below;
    float3 inside = _RGAmbSky.rgb * _RGLight.y * min(_RGGlass.z * filter, 1.0) * (1.0 - Fs);
    float3 glassy = lerp(game, inside, _RGLight.x) + F * mirrored * _RGRefl.x;
    float3 result = lerp(game, glassy, c.glass * saturate(_RGGlass.w));
    // The preview's check of the parts (_RGCardMode.w): 1 the lit albedo, 2 the windows' light, 3 the
    // game's own sheen, 4 what the glass mirrors (x F x brightness), 5 the glass share as grey.
    if (_RGCardMode.w > 0.5)
    {
        float part = _RGCardMode.w;
        result = part < 1.5 ? lit : (part < 2.5 ? emission : (part < 3.5 ? sheen : (part < 4.5 ? F * mirrored * _RGRefl.x : c.glass.xxx)));
    }
    return Finite(result);
}

// The card of building id at a world point P of its surface whose normal is n (from the screen's
// depth: FacadeCardGain.shader), seen at pixelM metres a pixel: the face n names in the building's
// own frame, when P lies within tol metres of that face of the box (and within a metre of its extent)
// -- else not found (a part of the asset off its box: a setback, a dome, a canopy). face: 0..4 (-1
// none); texelM: how wide the read is there (m: the card's texel at the level read, or the pixel).
void CardAtPoint(float id, float3 P, float3 n, float pixelM, float tol, out CardSample s, out float face, out float texelM)
{
    s.found = 0.0;
    s.fine = 1.0;
    s.albedo = float3(0.0, 0.0, 0.0);
    s.glass = 0.0;
    s.spec = 0.0;
    s.glow = 0.0;
    s.lit = float4(0.0, 0.0, 0.0, 0.0);
    s.bMean = 0.0;
    s.n = n;
    s.t = 0.0;
    s.at = P;
    face = -1.0;
    texelM = 1.0;

    float col = fmod(id, 256.0) * 3.0, row = floor(id / 256.0);
    float slot = id > 0.5 ? CardU16(CardTexel(col + 2.0, row).rg) : 0.0;
    if (_RGCardInfo2.w > 0.5 && slot > 0.5)
    {
        float4 r0 = CardTexel(col, row);
        float4 r1 = CardTexel(col + 1.0, row);
        float s0 = slot - 1.0;
        float scol = fmod(s0, 256.0) * 3.0, srow = _RGCardInfo2.z + floor(s0 / 256.0);
        float4 b0 = CardTexel(scol, srow);
        float4 b1 = CardTexel(scol + 1.0, srow);
        float4 b2 = CardTexel(scol + 2.0, srow);
        float3 mn = float3(CardU16(b0.rg), CardU16(b0.ba), CardU16(b1.rg)) / 32.0 - 1024.0;
        float3 mx = float3(CardU16(b1.ba), CardU16(b2.rg), CardU16(b2.ba)) / 32.0 - 1024.0;
        float2 pos = float2(CardU16(r0.rg), CardU16(r0.ba)) / 8.0 + _RGCardOrigin.xy;
        float baseY = CardU16(r1.rg) / 16.0 - 512.0;
        float angle = CardU16(r1.ba) * (6.28318530718 / 65536.0);
        float ca = cos(angle), sa = sin(angle);
        float2 ax = float2(ca, -sa), az = float2(sa, ca);

        float3 d = P - float3(pos.x, baseY, pos.y);
        float3 o = float3(dot(d.xz, ax), d.y, dot(d.xz, az));
        float3 nl = float3(dot(n.xz, ax), n.y, dot(n.xz, az));
        float3 an = abs(nl);
        float off = 1e9;
        if (an.y > max(an.x, an.z))
        {
            if (nl.y > 0.0)
            {
                face = 4.0;
                off = abs(o.y - mx.y);
            }
        }
        else if (an.x > an.z)
        {
            face = nl.x > 0.0 ? 1.0 : 3.0;
            off = nl.x > 0.0 ? abs(o.x - mx.x) : abs(o.x - mn.x);
        }
        else
        {
            face = nl.z > 0.0 ? 2.0 : 0.0;
            off = nl.z > 0.0 ? abs(o.z - mx.z) : abs(o.z - mn.z);
        }
        // As a float, not a negated bool: HLSLcc's `!` of a combined bool is never false (CLAUDE.md).
        float inside = all(o >= mn - 1.0) && all(o <= mx + 1.0) ? 1.0 : 0.0;
        if (face < -0.5 || off > tol || inside < 0.5)
        {
            face = -1.0;
        }
        else
        {
            float3 p = clamp(o, mn, mx);
            float3 ext = max(mx - mn, 0.01);
            float3 q = saturate((p - mn) / ext);
            float2 uv = float2(q.x, q.y);
            float2 size = ext.xy;
            float3 fn = float3(0.0, 0.0, -1.0);
            if (face > 0.5 && face < 1.5) { uv = float2(q.z, q.y); size = ext.zy; fn = float3(1.0, 0.0, 0.0); }
            else if (face > 1.5 && face < 2.5) { uv = float2(1.0 - q.x, q.y); fn = float3(0.0, 0.0, 1.0); }
            else if (face > 2.5 && face < 3.5) { uv = float2(1.0 - q.z, q.y); size = ext.zy; fn = float3(-1.0, 0.0, 0.0); }
            else if (face > 3.5) { uv = float2(q.x, q.z); size = ext.xz; fn = float3(0.0, 1.0, 0.0); }

            float2 tile, corner;
            float cap;
            CardTile(s0, face, tile, corner, cap);
            float2 texel = size / tile;
            float texelMin = min(texel.x, texel.y);
            float lod = clamp(log2(max(pixelM / texelMin, 1.0)), 0.0, cap);
            float halfTexel = 0.5 * exp2(ceil(lod));
            float2 inTile = clamp(uv * tile, halfTexel, tile - halfTexel);
            float2 auv = (corner + inTile) / _RGCardInfo.x;
            float4 A = tex2Dlod(_RGCardAtlas, float4(auv, 0.0, lod));
            float4 B = tex2Dlod(_RGCardLight, float4(auv, 0.0, lod));
            float4 S = tex2Dlod(_RGCardSpec, float4(auv, 0.0, lod));
            float cover = max(B.a, 1e-3);
            s.found = B.a > 0.02 ? 1.0 : 0.0;
            s.albedo = A.rgb / cover;
            s.glass = saturate(A.a / cover);
            s.spec = saturate(S.r / cover);
            s.glow = saturate(B.r / cover);
            s.lit = float4(saturate(B.g / cover), B.b / max(B.g, 1e-4), saturate(S.g / cover), S.b / max(S.g, 1e-4));
            if (_RGCardNight.x > 0.5)
            {
                float4 S1 = tex2Dlod(_RGCardSpec, float4(auv, 0.0, max(lod - 1.0, 0.0)));
                float4 B1 = tex2Dlod(_RGCardLight, float4(auv, 0.0, max(lod - 1.0, 0.0)));
                s.bMean = saturate(S1.a / max(B1.a, 1e-3));
            }
            s.n = fn.x * float3(ax.x, 0.0, ax.y) + float3(0.0, fn.y, 0.0) + fn.z * float3(az.x, 0.0, az.y);
            texelM = max(texelMin * exp2(lod), pixelM);
        }
    }
}

// One class's share on top of the shared level: (frame / card) / level, drawn toward 1 by how few points
// it has (weight / (weight + _RGCardGainInfo.y): a class seen in 20 points moves half way).
float CardGainOf(float frame, float card, float weight, float level)
{
    float own = card > 1e-5 && frame > 0.0 ? frame / card / max(level, 1e-4) : 1.0;
    float trust = weight / (weight + max(_RGCardGainInfo.y, 1.0));
    return 1.0 + (own - 1.0) * trust;
}

// The cards' level from the frame (FacadeCardGain.shader): ONE level shared by every class -- all the
// frame's light over all the cards' at the same points -- times each class's own share on top, drawn
// toward the shared level where the class has few points (walls and glass by a texel's glass share,
// faces the main light reaches and those it does not by N.L, blended as the samples were); clamped.
// Off (_RGCardGainInfo.x = 0): 1.
float CardGain(float3 n, float glass)
{
    float g = 1.0;
    if (_RGCardGainInfo.x > 0.5)
    {
        float4 walls = tex2Dlod(_RGCardGain, float4(0.5 / 3.0, 0.5, 0.0, 0.0));
        float4 panes = tex2Dlod(_RGCardGain, float4(1.5 / 3.0, 0.5, 0.0, 0.0));
        float4 w = tex2Dlod(_RGCardGain, float4(2.5 / 3.0, 0.5, 0.0, 0.0));
        float frame = walls.x + walls.z + panes.x + panes.z, card = walls.y + walls.w + panes.y + panes.w;
        float level = card > 1e-5 && frame > 0.0 ? frame / card : 1.0;
        float lw = CardGainOf(walls.x, walls.y, w.x, level), sw = CardGainOf(walls.z, walls.w, w.y, level);
        float lg = CardGainOf(panes.x, panes.y, w.z, level), sg = CardGainOf(panes.z, panes.w, w.w, level);
        float wl = smoothstep(0.0, 0.3, saturate(dot(n, _RGCardSunDir.xyz)));
        float share = lerp(lerp(sw, lw, wl), lerp(sg, lg, wl), saturate(glass));
        g = clamp(level * share, _RGCardGainInfo.z, _RGCardGainInfo.w);
    }
    return g;
}

#endif
