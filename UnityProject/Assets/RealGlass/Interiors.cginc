// Real Glass -- the rooms behind the glass (Glass/Interiors.cs, Glass/RoomAtlas.cs). Included by
// Glass.shader after GlassCommon.cginc. What a window shows of the room behind it: a picture of a
// room (by night lit by its own lamp, by day by the sky), the room's curtain or blind in front of it,
// and whether its light is on -- each chosen per room, so no two neighbours agree by more than
// chance.
//
// Sources (credited in README.md and THIRD-PARTY-NOTICES.md):
//  - The layers -- a room picture, a curtain or blind of its own that lets light through at night,
//    the glass in front; per room a picture from a group of its building's kind; far away one flat
//    picture: Forza Horizon 4's windows (Harwood, "A window into Playground Games' latest shader
//    development", Game Developer 2019).
//  - The room a box behind the window, its walls, floor and ceiling met by the view's ray: Joost van
//    Dongen, "Interior Mapping", CGI 2008; each room ONE picture of its box seen from in front of the
//    window, read back through that picture's camera: SimCity (Maxis 2013, Andrew Willmott), as Ben
//    Golus wrote it up (Unity forums, "Interior Mapping", 2016). RoomPictureUV.
//  - Lit windows scattered at random, some office buildings a whole floor at a time: Young's
//    procedural city (2009). Which rooms are lit, by the hour: Interiors.cs.
//  - The lamps' colours: Mitchell Charity's blackbody colour table (CIE 1931 2 deg, linear sRGB).
//  - The pictures: Poly Haven's indoor panoramas (CC0), painted on the box from the room's middle and
//    graded by tools/interiors.
//  - The rooms laid out on each asset's own windows -- its storeys, its floors, the walls between its
//    windows -- measured from its model (RoomFit.cs: autocorrelation as Rabiner found pitch, 1977;
//    epoch folding, Leahy et al. 1983): RoomFrameAt.

#ifndef REALGLASS_INTERIORS_INCLUDED
#define REALGLASS_INTERIORS_INCLUDED

float4 _RGRooms;          // x rooms on every building (1), y by day too (1), z the clock (game hours), w game hours a room keeps its light (0: for good)
float4 _RGRooms2;         // x a lit room's average light (brightness x Interiors.LitRoomLuminance), y the coloured lights' share, z the curtains' share, w pictures loaded (1)
float4 _RGRooms3;         // x room depth: 0 a flat picture on the glass .. 1 a box room behind it; y 1: up close the storey grid too (the LODs' rooms); z 1: rooms lit by the assets' illumination maps; w the coloured lights' share in workplaces (offices, shops, industry, public)
// The game's own, read as its building shader reads them (GameWindowLit): the simulation's clock,
// x seconds wrapping at 3600, w the same in radians (SimulationManager.Update); and its buildings'
// lights level, the hours from 6:00 or 18:00, whichever is nearer -- negative at night
// (LightSystem.SetDaylightIntensity, from DayNightProperties.Refresh).
float4 _SimulationTime2;
float _GiantMarshmallow;
float4 _RGRoomShare;      // the share of rooms lit now: x homes, y offices and industry, z shops, w hotels, leisure, public
float4 _RGRoomAtlasInfo;  // x tiles a row, y a night tile's pixels, z a day tile's pixels, w the back wall's share of a picture
float _RGRoomAtlasRows;   // rows of tiles (16 x 8 since 2026-11-08; 0 unset: as many as a row has tiles)
float4 _RGRoomPools[8];   // per kind of room (Interiors.Pool*): x its first tile, y how many, z its box's aspect, w its depth (half-heights)
sampler2D _RGRoomAtlas;   // the rooms by night, each lit by its own lamps in a neutral white (sRGB DXT1)
sampler2D _RGRoomAtlasDay;// the same rooms by day, in the same places
float4 _RGRoomFitInfo;    // x 1 while both below are bound, y their rows, z a row's texels
// Read texel by texel (Load): on Direct3D 11 that takes no sampler -- the middle and far variants
// already use all sixteen (ps_4_0's limit). Metal's limit is sixteen too, and there Unity 5.6 gives
// EVERY texture a sampler of its own, Load or not: these two made 17-18, and the Mac's compiler
// refused the whole shader (1.1.2, 2026-10-22: the mod stood down). So the fit runs on Direct3D 11
// and OpenGL Core (HLSLcc); on Metal, and anywhere else, the rooms stand on the storey grid, as
// before the fit (tools\bundle-apis.ps1 fails a Metal program past sampler 15).
#if defined(SHADER_API_D3D11) || (defined(UNITY_COMPILER_HLSLCC) && !defined(SHADER_API_METAL))
#define RG_ROOM_FIT 1
Texture2D _RGRoomPlaces;  // per building, two texels side by side (RoomFitTextures.cs): its model's origin (world) and its fit's row (0 none); its model's x and z axes (world xz)
Texture2D _RGRoomFits;    // a row per measured asset (RoomFit.cs): the storey (0 none), the ground storey's top, 1 no rooms, where its floors are; then per side its room width, its walls' place, 1 too narrow, 1 found
#endif

#define RG_ROOM_TILE_MEAN 0.18   // every picture's mean luminance, linear (tools/interiors/render-rooms.py)
#define RG_ROOM_OVERSCAN 1.25    // a room's front this much larger than the window the asset painted

// Charity's blackbody colours, linear sRGB, red 1 (bbr_color.txt, 2 deg).
static const float3 RG_K2200 = float3(1.0, 0.3126, 0.0219);
static const float3 RG_K2700 = float3(1.0, 0.4250, 0.0889);
static const float3 RG_K3000 = float3(1.0, 0.4870, 0.1411);
static const float3 RG_K3500 = float3(1.0, 0.5809, 0.2433);
static const float3 RG_K4000 = float3(1.0, 0.6636, 0.3583);
static const float3 RG_K5000 = float3(1.0, 0.7992, 0.6045);
static const float3 RG_K6500 = float3(1.0, 0.9436, 0.9621);

// A random number per (seed, k), 0..1.
float RoomRand(float seed, float k)
{
    return Hash12(float2(seed * 4096.0, k));
}

// The building's kind and its asset's own switches -- rooms, curtains, illumination map -- from its
// _RGAssets texel's a channel (Interiors.cs): kind + 8 x switch + 24 x curtains + 72 x illumination
// (each 0 the global one, 1 off, 2 on); 255 not looked at yet (a home, the global switches).
void RoomKind(float a, out float kind, out float assetSwitch, out float curtains, out float illumination)
{
    float a8 = floor(a * 255.0 + 0.5);
    bool unseen = a8 > 254.5;
    kind = unseen ? 0.0 : fmod(a8, 8.0);
    float switches = floor(a8 / 8.0);
    // Halfway between two multiples of 3: a division a little off on the GPU still floors right.
    float upper = floor((switches + 0.5) / 3.0);
    float top = floor((upper + 0.5) / 3.0);
    assetSwitch = unseen ? 0.0 : switches - 3.0 * upper;
    curtains = unseen ? 0.0 : upper - 3.0 * top;
    illumination = unseen ? 0.0 : top;
}

// How much the game lights this pixel of a building's window now: its building shader's rule
// (Custom/Buildings/Building/Default, read 2026-10-25; docs/NIGHT-MAP-PLAN.md), from the asset's
// illumination map at the pixel. Up to 0.48 the window glows day and night, 20 x b (0 none); above,
// two bands light as thresholds swing with the simulation's clock -- 0.49..0.74 eleven times an hour
// of it, 0.76..1.01 seven times, each building at its own phase -- so 0.75 never lights. The game
// switches a whole building on in the hour before 18:00 and off after 6:00, each at its palette
// texel's alpha; that palette would be a seventeenth texture (Metal: one sampler each, sixteen at
// most), so the building's own number stands in for it. b: x the map's texel at the pixel, y the
// map as the game reads it, z how much of y (Glass.shader: y further out, x up close).
float GameWindowLit(float3 b, float2 colourIndex, float buildingId)
{
    float2 threshold = sin(colourIndex * 31.3 + _SimulationTime2.w * float2(11.0, 7.0)) * 0.125 + float2(0.615, 0.885);
    float alpha = Hash12(float2(fmod(buildingId, 997.0) + 0.5, floor(buildingId / 997.0) + 0.71));
    float on = saturate((alpha + 0.01 - _GiantMarshmallow) * 50.0);
    on = on * on * (3.0 - 2.0 * on);
    float4 band = saturate(float4(threshold.x - b.x, b.x - threshold.y, threshold.x - b.y, b.y - threshold.y) * 5.0);
    band = band * band * (3.0 - 2.0 * band);
    float texelLit = b.x > 0.48 ? (band.x + band.y) * on : saturate(b.x * 20.0);
    float gameLit = b.y > 0.48 ? (band.z + band.w) * on : saturate(b.y * 20.0);
    return lerp(texelLit, gameLit, b.z);
}

// Which pictures a room of this kind on this storey takes (Interiors.Pool*), and which share of the
// hour it follows (one-hot: homes, offices, shops, the rest).
float RoomPool(float kind, float storey, out float4 group)
{
    bool ground = storey < 0.5;
    float pool = 0.0;
    group = float4(1.0, 0.0, 0.0, 0.0);
    if (kind > 0.5 && kind < 1.5)        { pool = 1.0; group = float4(0.0, 1.0, 0.0, 0.0); }   // offices
    else if (kind > 1.5 && kind < 2.5)   { if (ground) { pool = 2.0; group = float4(0.0, 0.0, 1.0, 0.0); } }   // shop, homes above
    else if (kind > 2.5 && kind < 3.5)   { pool = ground ? 4.0 : 3.0; group = float4(0.0, 0.0, 0.0, 1.0); }   // hotel: lobby, rooms
    else if (kind > 3.5 && kind < 4.5)   { pool = 4.0; group = float4(0.0, 0.0, 0.0, 1.0); }   // leisure
    else if (kind > 4.5 && kind < 5.5)   { pool = 5.0; group = float4(0.0, 0.0, 0.0, 1.0); }   // public
    else if (kind > 5.5 && kind < 6.5)   { pool = 6.0; group = float4(0.0, 1.0, 0.0, 0.0); }   // industry
    else if (kind > 6.5 && kind < 7.5)
    {
        pool = ground ? 2.0 : 1.0;                                                          // shop, offices above
        group = ground ? float4(0.0, 0.0, 1.0, 0.0) : float4(0.0, 1.0, 0.0, 0.0);
    }
    return pool;
}

// A room's lamp: a share of coloured lights (LED strips, RGB bulbs), the rest the whites real rooms
// have -- homes mostly warm, offices neutral to cool, shops between. Luminance 1.
float3 LampColour(float u, float pool, float coloured)
{
    float3 c;
    if (u < coloured)
    {
        float k = floor(u / max(coloured, 1e-4) * 6.0);
        c = k < 0.5 ? float3(0.55, 0.10, 1.00)      // purple
          : k < 1.5 ? float3(1.00, 0.12, 0.45)      // pink
          : k < 2.5 ? float3(0.05, 0.85, 1.00)      // cyan
          : k < 3.5 ? float3(0.12, 0.25, 1.00)      // blue
          : k < 4.5 ? float3(0.15, 1.00, 0.25)      // green
          :           float3(1.00, 0.08, 0.04);     // red
        return c / Luma(c);
    }

    u = saturate((u - coloured) / max(1.0 - coloured, 1e-4));
    bool work = pool > 0.5 && pool < 1.5 || pool > 4.5;          // offices, public, industry
    bool shop = pool > 1.5 && pool < 2.5 || pool > 3.5 && pool < 4.5;
    if (work)
        c = u < 0.05 ? RG_K3000 : (u < 0.25 ? RG_K3500 : (u < 0.70 ? RG_K4000 : (u < 0.93 ? RG_K5000 : RG_K6500)));
    else if (shop)
        c = u < 0.15 ? RG_K2700 : (u < 0.40 ? RG_K3000 : (u < 0.75 ? RG_K4000 : RG_K5000));
    else
        c = u < 0.10 ? RG_K2200 : (u < 0.45 ? RG_K2700 : (u < 0.67 ? RG_K3000 : (u < 0.77 ? RG_K3500 :
            (u < 0.87 ? RG_K4000 : (u < 0.95 ? RG_K5000 : RG_K6500)))));
    return c / Luma(c);
}

// The average lamp of a kind of room: what a room too small to see shows.
float3 AverageLamp(float pool)
{
    bool work = pool > 0.5 && pool < 1.5 || pool > 4.5;
    float3 c = work ? RG_K4000 : (pool > 1.5 && pool < 2.5 || pool > 3.5 && pool < 4.5 ? RG_K3500 : RG_K2700);
    return c / Luma(c);
}

// Detail finer than about three pixels fades to its average: folds, slats, a scalloped hem.
float Fine(float period, float footprint)
{
    return saturate(period / max(footprint * 3.0, 1e-5) - 0.5);
}

// The room's curtain or blind, hanging just behind the glass. xy: 0..1 across the window (from its
// bottom left); size: the window in metres; share: the building's share of windows with one. Returns
// how much of it covers the point; albedo, its colour; trans, the share of the room's light it lets
// through at night.
float RoomCurtain(float seed, float pool, float2 xy, float2 size, float footprint, float share, out float3 albedo, out float trans)
{
    albedo = float3(0.8, 0.8, 0.8);
    trans = 0.0;
    bool work = pool > 0.5 && pool < 1.5 || pool > 4.5;
    bool shop = pool > 1.5 && pool < 2.5 || pool > 3.5 && pool < 4.5;
    if (shop)
        share *= 0.3;
    float u = RoomRand(seed, 31.0);
    if (u >= share)
        return 0.0;
    float t = u / max(share, 1e-4);
    float2 m = xy * size;
    float r1 = RoomRand(seed, 32.0), r2 = RoomRand(seed, 33.0), r3 = RoomRand(seed, 34.0);

    // Blinds: roller (a translucent sheet) or venetian (slats), drawn from the top down.
    bool roller = shop || (work ? t >= 0.6 : (t >= 0.82 && t < 0.91));
#if defined(UNITY_COMPILER_HLSLCC)
    // Unity 5.6's HLSLcc (OpenGL Core, Metal) writes the "not" of a combined condition as
    // ~int(b) * int(0xffffffffu): never false (not-true is 2). So no "not" there; Direct3D 11 keeps
    // its own line, byte for byte (tools\bundle-apis.ps1 fails a bundle with the pattern in it).
    // A shop's roller wins below, so the shop needs no test here.
    bool venetian = work ? t < 0.6 : t >= 0.91;
#else
    bool venetian = !shop && (work ? t < 0.6 : t >= 0.91);
#endif
    if (roller || venetian)
    {
        float drop = roller ? lerp(0.2, 0.95, r1) : lerp(0.35, 1.0, r1);
        float covered = step(1.0 - drop, xy.y);
        float3 tone = lerp(float3(0.62, 0.60, 0.56), float3(0.90, 0.89, 0.86), r2);
        if (roller)
        {
            albedo = r3 < 0.2 ? tone * float3(0.55, 0.62, 0.70) : tone;
            trans = 0.35;
            return covered * 0.97;
        }
        // Venetian slats 5 cm apart, open by r3: a stripe close up, its share further off.
        float open = lerp(0.15, 0.55, r3);
        float slat = step(open, frac(m.y / 0.05));
        float k = Fine(0.05, footprint);
        albedo = tone;
        trans = 0.25;
        return covered * lerp(1.0 - open, slat, k) * 0.98;
    }

    // Fabric: tulle across the window, drapes at its sides, or both.
    bool tulle = t < 0.56 || (t >= 0.76 && t < 0.82 && r3 < 0.3);
    float sideL = 0.0, sideR = 0.0;
    if (t >= 0.30 && t < 0.68)       { sideL = lerp(0.12, 0.25, r1); sideR = lerp(0.12, 0.25, r2); }   // drawn back
    else if (t >= 0.68 && t < 0.76)  { sideL = lerp(0.30, 0.45, r1); sideR = lerp(0.30, 0.45, r2); }   // half drawn
    else if (t >= 0.76 && t < 0.82)  { sideL = 0.5 - 0.03 * r1; sideR = 0.5 - 0.03 * r2; }             // closed
    bool drape = xy.x < sideL || xy.x > 1.0 - sideR;

    if (drape)
    {
        // A drape's colour: one of the colours fabric comes in.
        float pick = floor(RoomRand(seed, 35.0) * 10.0);
        float3 c = pick < 0.5 ? float3(0.42, 0.12, 0.15)     // burgundy
                 : pick < 1.5 ? float3(0.36, 0.35, 0.18)     // olive
                 : pick < 2.5 ? float3(0.13, 0.19, 0.31)     // navy
                 : pick < 3.5 ? float3(0.78, 0.72, 0.60)     // beige
                 : pick < 4.5 ? float3(0.35, 0.24, 0.17)     // brown
                 : pick < 5.5 ? float3(0.69, 0.55, 0.24)     // gold
                 : pick < 6.5 ? float3(0.48, 0.48, 0.47)     // grey
                 : pick < 7.5 ? float3(0.18, 0.36, 0.36)     // teal
                 : pick < 8.5 ? float3(0.70, 0.48, 0.48)     // rose
                 :              float3(0.89, 0.85, 0.76);    // cream
        float folds = 1.0 + 0.35 * sin(6.2832 * m.x / 0.16 + r3 * 6.2832) * Fine(0.16, footprint);
        albedo = c * c * folds;   // the palette is sRGB-ish: squared, about linear
        trans = 0.14 * folds;
        return 0.96;
    }

    if (tulle)
    {
        // A sheer, ivory to white, softly folded; its hem a little above the sill, scalloped.
        float hem = 0.03 + 0.015 * sin(6.2832 * m.x / 0.25) * Fine(0.25, footprint);
        float below = step(xy.y, hem);
        float folds = 1.0 + 0.2 * sin(6.2832 * m.x / 0.09 + r1 * 6.2832) * Fine(0.09, footprint);
        albedo = lerp(float3(0.78, 0.75, 0.66), float3(0.86, 0.86, 0.84), r2) * folds;
        trans = 0.7 * folds;
        return (1.0 - below) * 0.55 * folds;
    }
    return 0.0;
}

// A room's light (its lamp, its television, or none) by night: the room's place in the grid, its
// building's kind of rooms, its building's seed; gameLit, how much the game lights the window here
// (GameWindowLit) where the building follows its asset's illumination map, else below 0. seed: the
// room's own number; lit, tv: 0..1. The same room gives the same answer whichever pixel asks: every
// tier, near or far, agrees (following the map, as the game's own window does).
float3 RoomLight(float2 cellId, float pool, float4 group, float buildingSeed, float gameLit, out float seed, out float lit, out float tv)
{
    seed = Hash12(cellId + buildingSeed * 4096.0);

    // Offices in half the buildings a whole floor at a time, else three rooms together.
    bool work = pool > 0.5 && pool < 1.5 || pool > 5.5;
    float2 lightCell = cellId;
    if (work)
        lightCell = float2(RoomRand(buildingSeed, 3.0) < 0.5 ? 0.0 : floor(cellId.x / 3.0), cellId.y);
    float lightSeed = Hash12(lightCell + buildingSeed * 4096.0 + 17.0);

    // On or off: the room's own number for this stretch of hours against the share lit now, each
    // room's stretches starting at its own moment (Interiors.cs). At the turn, a moment's change.
    float stretch = _RGRooms.w;
    float phase = RoomRand(lightSeed, 5.0);
    float e = stretch > 0.0 ? _RGRooms.z / stretch + phase : phase;
    float ei = floor(e);
    float share = dot(group, _RGRoomShare);
    float litNow = step(RoomRand(lightSeed, ei + 11.0), share);
    float litBefore = step(RoomRand(lightSeed, ei + 10.0), share);
    lit = lerp(litBefore, litNow, saturate((e - ei) * 200.0));
    lit = gameLit >= 0.0 ? gameLit : lit;
    // Some lit homes are only a television.
    bool homeLike = pool < 0.5 || pool > 2.5 && pool < 3.5;
    tv = homeLike ? step(RoomRand(lightSeed, ei + 23.0), 0.07) : 0.0;

    // Coloured lights: homes, hotels and leisure take _RGRooms2.y, workplaces _RGRooms3.w (2026-11-08).
    float coloured = (pool > 0.5 && pool < 2.5) || pool > 4.5 ? _RGRooms3.w : _RGRooms2.y;
    float3 lamp = LampColour(RoomRand(seed, 7.0), pool, coloured);
    float bright = (0.55 + 0.9 * RoomRand(seed, 6.0)) * _RGRooms2.x;
    float flicker = 0.75 + 0.25 * sin(_Time.y * 7.0 + seed * 40.0) * sin(_Time.y * 2.3 + seed * 17.0);
    float3 tvLight = float3(0.55, 0.70, 1.0) / Luma(float3(0.55, 0.70, 1.0)) * 0.18 * flicker * _RGRooms2.x;
    return lit * lerp(lamp * bright, tvLight, tv);
}

// Whether a room can stand behind this pixel of glass: 1 yes .. 0 no (2026-10-21). A room's frame --
// its front, its floors, its place along the facade, dot(P, t) -- follows the mesh's normal n, so the
// glass must be what that normal says. Upright: a sloped roof, a canopy or a solar panel holds no room
// (within 6 degrees of vertical, none past 12). The face's own normal: smoothed shading tilts n off
// the face, and the room's box is seen at a slant (within 3 degrees, none past 6; a normal turned
// inside out is the far side). Flat: where n turns, dot(P, t) runs on at the turn times the distance
// from the map's middle -- a tenth of a degree at 2 km moves a room by metres, shredding the rooms
// into stripes (none once it runs 60% faster than the facade); and, the same turn seen without that
// lever (which is nil along the one line where the facade points past the map's middle), none where
// n turns by more than 3 degrees over 3 metres, a room's width (6 and over). The face's own orientation is the
// cross product of the position's screen derivatives, taken from the camera so its numbers are small
// and exact to the pixel. rel: the position from the camera; n, t: the mesh's normal and the facade's
// level tangent (FacadeTangent(n)); P: the world position; footprint: the metres a pixel spans.
float RoomShape(float3 rel, float3 n, float3 t, float3 P, float footprint)
{
    float3 face = cross(ddy(rel), ddx(rel));
    float len = length(face);
    face = len > 1e-12 ? face / len : n;
    face *= dot(face, rel) > 0.0 ? -1.0 : 1.0;   // the side the camera sees
    float upright = 1.0 - smoothstep(0.105, 0.208, abs(face.y));
    float own = saturate((dot(n, face) - 0.99452) / (0.99863 - 0.99452));
    float stretch = (abs(dot(P, ddx(t))) + abs(dot(P, ddy(t)))) / max(footprint, 1e-4);
    float bend = length(fwidth(n)) / max(footprint, 1e-4) * 3.0;   // radians n turns over 3 m
    float flat = (1.0 - smoothstep(0.3, 0.6, stretch)) * (1.0 - smoothstep(0.052, 0.105, bend));
    return upright * own * flat;
}

// The share of a storey that is floor slab and ceiling, dark at night, where the asset painted no
// window of its own (a curtain wall's spandrel): bottom, top.
#define RG_ROOM_SLAB float2(0.14, 0.06)

// Where the rooms stand on a building: fitted to its asset's own windows (RoomFit.cs, measured from
// its model and its shine map when the city loads) -- its storeys and floors, on each side the
// walls between its windows -- in the building's own frame, the same on every tier. Where nothing is
// known yet, the storey grid (_RGPane2.z) and the kind's room width, as before the fit.
struct RoomFrame
{
    float2 q;        // the point: metres along the facade (from the model's middle line) and up from the building's base
    float2 shift;    // the world's (dot(P, t), P.y) minus q
    float storey;    // a storey's height
    float ground;    // the ground storey's top (0: every storey alike)
    float floor0;    // where the storeys above it start (their floors at floor0 + k x storey)
    float width;     // a room's width
    float phase;     // along the facade, where a room's side wall stands
    float allowed;   // 0: the asset's glass holds no rooms here
};

RoomFrame RoomFrameAt(float3 P, float3 n, float3 t, float2 colourIndex, float baseY, float cellW)
{
    RoomFrame f;
    f.q = float2(dot(P, t), P.y - baseY);
    f.shift = float2(0.0, baseY);
    f.storey = max(_RGPane2.z, 2.0);
    f.ground = 0.0;
    f.floor0 = 0.0;
    f.width = cellW;
    f.phase = 0.0;
    f.allowed = 1.0;
#if defined(RG_ROOM_FIT)
    if (_RGRoomFitInfo.x < 0.5)
        return f;

    int2 cell = int2(floor(colourIndex * _RGColorMap.xy));
    float4 place = _RGRoomPlaces.Load(int3(2 * cell.x, cell.y, 0));
    float4 axes = _RGRoomPlaces.Load(int3(2 * cell.x + 1, cell.y, 0));
    if (dot(axes.xy, axes.xy) < 0.5)
        return f;   // not placed yet

    // Its own frame: along from its model's middle line, up from its base -- the far tier, which has
    // no matrix, as the near and middle ones.
    float3 d = P - place.xyz;
    f.q = float2(dot(d, t), d.y);
    f.shift = float2(dot(place.xyz, t), place.y);
    f.phase = 0.5 * cellW;   // a room centred on the middle line
    if (place.w < 0.5)
        return f;   // not measured

    int row = int(min(place.w + 0.5, _RGRoomFitInfo.y - 1.0));
    float4 own = _RGRoomFits.Load(int3(0, row, 0));
    if (own.x > 0.5)
    {
        f.storey = own.x;
        f.ground = own.y;
        f.floor0 = own.y > 0.0 ? own.y : own.w;
    }
    f.allowed = 1.0 - own.z;

    // This face's side: its heading in the model's space, in 16 steps centred on 0 (RoomFit.SideOf).
    float2 nm = float2(dot(n.xz, axes.xy), dot(n.xz, axes.zw));
    nm = dot(nm, nm) > 1e-10 ? nm : float2(1.0, 0.0);
    float k = floor(atan2(nm.y, nm.x) / 6.2831853 * 16.0 + 0.5);
    k -= 16.0 * floor(k / 16.0);
    float4 side = _RGRoomFits.Load(int3(1 + int(k), row, 0));
    if (side.w > 0.5)
    {
        f.width = side.x;
        f.phase = side.y;
    }
    f.allowed *= 1.0 - side.z;
#endif
    return f;
}

// The storey a height above the base is in: its number (0 the ground's), its bottom and its height.
float StoreyOf(float y, RoomFrame f, out float bottom, out float height)
{
    if (f.ground > 0.0 && y < f.ground)
    {
        bottom = 0.0;
        height = f.ground;
        return 0.0;
    }
    float k = floor((y - f.floor0) / f.storey);
    bottom = f.floor0 + k * f.storey;
    height = f.storey;
    return k + (f.ground > 0.0 ? 1.0 : 0.0);
}

// The room a point (RoomFrame.q) is in: its column along the facade and its storey.
float2 RoomCellOf(float2 q, RoomFrame f)
{
    float bottom, height;
    return float2(floor((q.x - f.phase) / f.width), StoreyOf(q.y, f, bottom, height));
}

// The room's front, the face of its box that holds the window: the box's aspect (A, width over
// height), covering the window (a painted window RG_ROOM_OVERSCAN larger: the room is wider than its
// window; a storey's cell exactly, so its floor and ceiling are the storey's). Its height, metres.
float RoomFrontHeight(float aspect, float2 windowSize, bool grid)
{
    float over = grid ? 1.0 : RG_ROOM_OVERSCAN;
    return windowSize.x / max(windowSize.y, 0.01) < aspect ? windowSize.y * over : windowSize.x * over / aspect;
}

// Where this pixel of glass looks in its room's picture. The room is a box behind the window: x
// across (-A..A), y floor to ceiling (-1..1), z in (0..D), the window a part of its front at z = 0;
// the picture is that box seen from a camera in front of the window, far enough that the back wall
// fills _RGRoomAtlasInfo.w of it (tools/interiors/render-rooms.py, box_view) -- so the ray from
// the eye through this pixel, met with the box's walls, floor, ceiling or back, and projected
// through that camera, is the right place in it: the room moves as a room does when the view does
// (interior mapping, van Dongen 2008; one picture per room, SimCity 2013 as Ben Golus wrote it up).
// _RGRooms3.x blends from the flat picture on the glass (0) to the box (1). Mirrored for half the
// rooms; the window's place in the room's front a room's own (crop). q, windowMin: in the building's
// frame (RoomFrame).
float2 RoomPictureUV(float aspect, float depth, float2 q, float3 V, float3 n, float3 t,
                     float2 windowMin, float2 windowSize, bool grid, float seed)
{
    // The front, in metres, round the window: the window at a place of the room's own within it.
    float frontH = RoomFrontHeight(aspect, windowSize, grid);
    float2 front = float2(frontH * aspect, frontH);
    float2 slack = max(front - windowSize, 0.0);
    float2 crop = float2(RoomRand(seed, 1.0), lerp(0.3, 0.7, RoomRand(seed, 2.0)));
    float2 onFront = saturate((q - windowMin + slack * crop) / front);   // 0..1 across the front

    // Into the box: the front point and the view's direction in the room's frame (half-heights).
    float mirror = RoomRand(seed, 4.0) < 0.5 ? -1.0 : 1.0;
    float3 F = float3((onFront.x * 2.0 - 1.0) * aspect * mirror, onFront.y * 2.0 - 1.0, 0.0);
    float3 d = float3(dot(V, t) * mirror, V.y, max(dot(V, -n), 1e-3));
    float sx = d.x >= 0.0 ? 1.0 : -1.0, sy = d.y >= 0.0 ? 1.0 : -1.0;
    float tx = (sx * aspect - F.x) / (abs(d.x) > 1e-5 ? d.x : sx * 1e-5);
    float ty = (sy - F.y) / (abs(d.y) > 1e-5 ? d.y : sy * 1e-5);
    float tz = depth / d.z;
    float3 H = F + d * max(min(min(tx, ty), tz), 0.0);

    // Through the picture's camera.
    float backShare = clamp(_RGRoomAtlasInfo.w, 0.05, 0.95);
    float cam = backShare * depth / (1.0 - backShare);
    float2 box = H.xy * (cam / (cam + H.z));
    float2 boxUV = float2(box.x / aspect, box.y) * 0.5 + 0.5;
    float2 flatUV = float2(F.x / aspect, F.y) * 0.5 + 0.5;
    return lerp(flatUV, boxUV, saturate(_RGRooms3.x));
}

// What the room behind this pixel of glass sends through it, by night and by day, once through the
// glass (nightGlass, dayGlass: what the glass lets through of each). Returns how much of the
// window is the room's: 0 where there are no rooms (switched off, not loaded, an asset of its own
// says no, glass that is not a facade or holds none), else 1 (less on a slanted pane). colourIndex:
// the building's (its texel in the object colour map: where its frame is, RoomFrameAt); illumination:
// the asset's illumination map at the pixel (GameWindowLit's b).
float RoomsBehind(float3 P, float3 n, float3 t, float3 V, float baseY, float footprint, float4 painted, float buildingId,
                  float2 colourIndex, float assetA, float3 illumination, float3 inside, float3 dayGlass, float3 nightGlass,
                  out float3 night, out float3 day)
{
    night = float3(0.0, 0.0, 0.0);
    day = inside;
    float kind, assetSwitch, assetCurtains, assetIllumination;
    RoomKind(assetA, kind, assetSwitch, assetCurtains, assetIllumination);
    bool on = _RGRooms2.w > 0.5 && (assetSwitch > 1.5 || (assetSwitch < 0.5 && _RGRooms.x > 0.5));
    float facade = saturate((1.0 - abs(n.y)) * 3.0);
#if defined(UNITY_COMPILER_HLSLCC)
    // The same test with no "not" in it (see RoomCurtain): "!on || ..." kept every room away on
    // OpenGL Core and Metal, from 1.0.0 to 2026-10-22.
    if ((on ? facade : 0.0) <= 0.0)
#else
    if (!on || facade <= 0.0)
#endif
        return 0.0;

    // The building's frame: its asset's storeys and rooms where they were fitted. Glass its asset
    // holds no rooms behind (strips, a round tower's facets) has none -- unless its building window
    // asks for them.
    float cellW = kind > 0.5 && kind < 1.5 || kind > 6.5 ? 6.0 : (kind > 5.5 && kind < 6.5 ? 8.0 : 3.6);
    RoomFrame f = RoomFrameAt(P, n, t, colourIndex, baseY, cellW);
    if (f.allowed < 0.5 && assetSwitch < 1.5)
        return 0.0;
    float2 q = f.q;

    // The room: the window the asset painted here (close up), else a cell one storey tall and a
    // room wide along the facade. Its place in the grid (column, storey) names it, the same on every
    // tier as far as can be: a painted window by its middle. With _RGRooms3.y, the grid up close too:
    // the LOD models have no painted windows to find, so a building changing model keeps its rooms.
    if (_RGRooms3.y > 0.5)
        painted = float4(0.0, 0.0, 0.0, 0.0);
    float2 cellMin, cellSize, cellId;
    if (painted.y > painted.x)
    {
        painted -= f.shift.xxyy;
        cellMin = float2(painted.x, painted.z);
        cellSize = float2(painted.y - painted.x, painted.w - painted.z);
        cellId = RoomCellOf(cellMin + cellSize * 0.5, f);
    }
    else
    {
        float bottom, height;
        float storey = StoreyOf(q.y, f, bottom, height);
        float column = floor((q.x - f.phase) / f.width);
        cellMin = float2(f.phase + column * f.width, bottom);
        cellSize = float2(f.width, height);
        cellId = float2(column, storey);
    }

    float4 group;
    float pool = RoomPool(kind, cellId.y, group);
    float fid = FacadeId(n);
    float buildingSeed = Hash12(float2(fmod(buildingId, 997.0) + 0.5, floor(buildingId / 997.0) + fid * 0.37));
    // Which rooms are lit: where and when the game lights this window (its asset's illumination
    // map: the Interiors tab's switch, or the asset's own, Alt+G), else ours (below 0).
    bool follow = assetIllumination > 1.5 || assetIllumination < 0.5 && _RGRooms3.z > 0.5;
    float gameLit = follow ? GameWindowLit(illumination, colourIndex, buildingId) : -1.0;
    float seed, lit, tv;
    float3 roomLight = RoomLight(cellId, pool, group, buildingSeed, gameLit, seed, lit, tv);
    float share = gameLit >= 0.0 ? gameLit : dot(group, _RGRoomShare);
    float3 dark = _RGAmbSky.rgb * 0.03;
    float3 dayDark = _RGAmbSky.rgb * _RGLight.y;

    // Where the asset painted no window, a storey of curtain wall: its floor slab and ceiling dark,
    // the room between them.
    bool grid = !(painted.y > painted.x);
    float slabShare = grid ? RG_ROOM_SLAB.x + RG_ROOM_SLAB.y : 0.0;
    float2 bandMin = cellMin, bandSize = cellSize;
    if (grid)
    {
        bandMin.y += RG_ROOM_SLAB.x * cellSize.y;
        bandSize.y *= 1.0 - slabShare;
    }

    // How many pixels a room spans. Up close: its picture and its curtain. Further out: only its
    // light, read for four rooms under the pixel (a box filter of the pattern, so it does not crawl
    // as the view moves -- TAA); under half a pixel, the light of rooms of its kind on average.
    float roomPx = min(cellSize.x, cellSize.y) / max(footprint, 1e-4);
    float detail = saturate((roomPx - 3.0) / 2.0);
    float pattern = saturate((roomPx - 0.4) / 0.6);
    // Curtains: the Interiors tab's share, none where the asset says so (its building window).
    float curtainShare = assetCurtains > 0.5 && assetCurtains < 1.5 ? 0.0 : _RGRooms2.z;
    float fabric = 1.0 - 0.3 * curtainShare;
    float3 farNight = dark;
    if (detail < 1.0)
    {
        float3 sum = float3(0.0, 0.0, 0.0);
        [unroll]
        for (int k = 0; k < 4; k++)
        {
            float2 offset = float2(k == 1 || k == 3 ? 0.35 : -0.35, k >= 2 ? 0.35 : -0.35) * footprint;
            float2 tapId = RoomCellOf(q + offset, f);
            float4 tapGroup;
            float tapPool = RoomPool(kind, tapId.y, tapGroup);
            float tapSeed, tapLit, tapTv;
            sum += RoomLight(tapId, tapPool, tapGroup, buildingSeed, gameLit, tapSeed, tapLit, tapTv);
        }
        float3 average = share * AverageLamp(pool) * _RGRooms2.x;
        farNight = lerp(average, sum * 0.25, pattern) * (1.0 - slabShare) * fabric + dark;
    }

    float3 roomNight = farNight;
    float3 roomDay = dayDark;
    float3 albedo = float3(0.0, 0.0, 0.0);
    float cover = 0.0;
    float2 xy = saturate((q - bandMin) / bandSize);
    if (detail > 0.0)
    {
        float4 range = _RGRoomPools[(int)pool];
        float tile = range.x + min(floor(RoomRand(seed, 3.0) * range.y), max(range.y - 1.0, 0.0));
        float perRow = max(_RGRoomAtlasInfo.x, 1.0);
        float2 grid2 = float2(perRow, _RGRoomAtlasRows > 0.5 ? _RGRoomAtlasRows : perRow);
        float2 tileXY = float2(fmod(tile, perRow), floor(tile / perRow));
        float2 tileUV = RoomPictureUV(range.z, range.w, q, V, n, t, bandMin, bandSize, grid, seed);

        // The level of detail the pixel covers (a little sharper than its longest footprint), the
        // reads kept inside the tile at that level. The picture spans the room's front: a little
        // more than the window.
        float tileMetres = RoomFrontHeight(range.z, bandSize, grid);
        float nightPx = max(_RGRoomAtlasInfo.y, 1.0), dayPx = max(_RGRoomAtlasInfo.z, 1.0);
        float lodN = clamp(log2(max(footprint * 0.7 * nightPx / tileMetres, 1e-4)), 0.0, log2(nightPx));
        float lodD = clamp(log2(max(footprint * 0.7 * dayPx / tileMetres, 1e-4)), 0.0, log2(dayPx));
        float marginN = 0.5 * exp2(lodN) / nightPx, marginD = 0.5 * exp2(lodD) / dayPx;
        float2 uvN = (tileXY + clamp(tileUV, marginN, 1.0 - marginN)) / grid2;
        float2 uvD = (tileXY + clamp(tileUV, marginD, 1.0 - marginD)) / grid2;
        float3 picN = Finite(tex2Dlod(_RGRoomAtlas, float4(uvN, 0.0, lodN)).rgb) / RG_ROOM_TILE_MEAN;
        float3 picD = Finite(tex2Dlod(_RGRoomAtlasDay, float4(uvD, 0.0, lodD)).rgb) / RG_ROOM_TILE_MEAN;

        // By night lit by its lamp (or a television), else dark; by day by the sky's light, as dark
        // as today's inside on average.
        float3 nearNight = picN * (roomLight + dark);
        float3 nearDay = picD * dayDark;

        // The curtain, at the glass: no slide. Lit from behind at night, by the sky by day.
        float trans;
        cover = RoomCurtain(seed, pool, xy, bandSize, footprint, curtainShare, albedo, trans);
        nearNight = lerp(nearNight, roomLight * trans * albedo * 1.2 + dark * albedo, cover);
        nearDay = lerp(nearDay, _RGAmbSky.rgb * albedo * 0.55, cover);

        // The slab and the ceiling, dark.
        float inBand = grid ? step(bandMin.y, q.y) * step(q.y, bandMin.y + bandSize.y) : 1.0;
        nearNight = lerp(dark * 0.5, nearNight, inBand);
        nearDay = lerp(dayDark * 0.5, nearDay, inBand);

        roomNight = lerp(farNight, nearNight, detail);
        roomDay = lerp(dayDark, nearDay, detail);
    }

    if (_RGDebug.x > 10.5 && _RGDebug.x < 11.5)
    {
        // 11: the rooms -- lit (green), dark (red), a television (blue); cells outlined by their kind's
        // pool (brighter edge).
        float2 edge = min(xy, 1.0 - xy) * cellSize;
        float3 dbg = tv > 0.5 && lit > 0.5 ? float3(0.1, 0.3, 1.0) : (lit > 0.5 ? float3(0.1, 0.9, 0.2) : float3(0.6, 0.05, 0.05));
        night = min(edge.x, edge.y) < 0.08 ? float3(1.0, 1.0, 0.2) * (0.3 + pool * 0.1) : dbg;
        day = night;
        return facade;
    }

    night = roomNight * nightGlass;
    day = roomDay * dayGlass;
    return facade;
}

#endif
