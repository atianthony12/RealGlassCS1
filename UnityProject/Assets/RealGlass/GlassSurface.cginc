// Real Glass -- the glass's SURFACE: the game's vertex rules per tier, and the first half of the
// fragment -- what counts as glass (the per-asset texel, the anti-aliased mask, the clips), the
// painted pane, the pane's bent normal and the steady-glint roughness. Shared by Glass.shader (the
// forward pass that colours the glass) and GlassPrePass.shader (the thin G-buffer the reflection
// pass reads, GlassReflection.shader), so both run the identical code and agree pixel for pixel.
// The including pass defines one of RG_NEAR / RG_LOD / RG_GROUP (multi_compile).

#ifndef REALGLASS_SURFACE_INCLUDED
#define REALGLASS_SURFACE_INCLUDED

#include "GlassCommon.cginc"
#include "Interiors.cginc"

sampler2D _MainTex;
float4 _MainTex_ST;
sampler2D _XYSMap;
sampler2D _ACIMap;
float4 _ACIMap_TexelSize;   // Unity's, with the texture: zw its size (0 if not given)

#if defined(RG_LOD)
float4x4 _BuildingLocations[8];
float4 _BuildingStates[8];
float4 _ObjectIndices[8];
#elif defined(RG_NEAR)
float4 _BuildingState;
float4 _ObjectIndex;
float4 _RGNearBase;     // a Procedural Objects building (GlassDraw.DrawProcedural): x the ground under it, w 1; 0 = its origin
#endif

// The shine the glass goes by: the asset's own (spec = 1 - _XYSMap.z) -- and on the middle
// and far tiers, where an LOD's glass is not its model's (none, or elsewhere), its model's,
// carried onto the LOD by the building's geometry and laid out as the atlas (LodGlass.cs):
// a texel's alpha x 255 is 1 + 254 x that shine, 0 where the LOD keeps its own. Between
// the two (an island's edge read filtered) the one fades into the other.
#if defined(RG_NEAR)
float ShineAt(float2 uv) { return 1.0 - tex2D(_XYSMap, uv).z; }
float ShineGrad(float2 uv, float2 gx, float2 gy) { return 1.0 - tex2Dgrad(_XYSMap, uv, gx, gy).z; }
float ShineBias(float2 uv, float bias) { return 1.0 - tex2Dbias(_XYSMap, float4(uv, 0.0, bias)).z; }
#else
sampler2D _RGLodGlass;
float LodShine(float own, float m)
{
    float m255 = FiniteIn(m, 0.0, 1.0) * 255.0;
    return lerp(own, saturate((m255 - 1.0) / 254.0), saturate(m255));
}
float ShineAt(float2 uv) { return LodShine(1.0 - tex2D(_XYSMap, uv).z, tex2D(_RGLodGlass, uv).a); }
float ShineGrad(float2 uv, float2 gx, float2 gy)
{
    return LodShine(1.0 - tex2Dgrad(_XYSMap, uv, gx, gy).z, tex2Dgrad(_RGLodGlass, uv, gx, gy).a);
}
float ShineBias(float2 uv, float bias)
{
    return LodShine(1.0 - tex2Dbias(_XYSMap, float4(uv, 0.0, bias)).z, tex2Dbias(_RGLodGlass, float4(uv, 0.0, bias)).a);
}
#endif

struct appdata
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    float4 texcoord : TEXCOORD0;
    float4 texcoord1 : TEXCOORD1;
    float4 texcoord2 : TEXCOORD2;
    float4 texcoord3 : TEXCOORD3;
    float4 color : COLOR;
};

struct v2f
{
    float4 pos : SV_POSITION;
    float4 worldPos : TEXCOORD0;     // xyz position, w the building's base height
    float4 worldNormal : TEXCOORD1;  // xyz geometric normal, w what the building's state leaves of its glass
    float2 uv : TEXCOORD2;
    float4 rect : TEXCOORD3;         // the asset's rectangle in the LOD atlas (LOD, GROUP)
    float4 screenPos : TEXCOORD4;
    float2 building : TEXCOORD5;     // the building's colour index: its texel in the object colour map
    float3 rel : TEXCOORD6;          // the position from the camera: the face's own orientation (RoomShape)
};

// Texture coordinates per metre ALONG the facade (xy) and UP it (zw), from the pixel's
// own derivatives: the screen-space changes of uv and of the facade position (along,
// up) give the 2 x 2 map between them.
float4 UvPerMetre(float2 uv, float3 P, float3 t)
{
    float2 duvx = ddx(uv), duvy = ddy(uv);
    float3 dPx = ddx(P), dPy = ddy(P);
    float2 sx = float2(dot(dPx, t), dPx.y);
    float2 sy = float2(dot(dPy, t), dPy.y);
    float det = sx.x * sy.y - sy.x * sx.y;
    return abs(det) > 1e-10 ? float4(duvx * sy.y - duvy * sx.y, duvy * sx.x - duvx * sy.x) / det : float4(0.0, 0.0, 0.0, 0.0);
}

#if defined(RG_NEAR)
// The window the asset painted round this pixel: where its glass stops above and below,
// looked for on a fixed grid of world heights (so every pixel of one window finds the
// same edges), _RGBudget.w steps (0.25 m) each way. (bottom, top), world y; 0, 0 when
// none was found (or a window under 0.9 m or over 7 m: not a pane).
float2 PaintedWindow(float2 uv, float3 P, float2 upUV, float glassThreshold)
{
    const float stepM = 0.25;
    float y0 = floor(P.y / stepM) * stepM;
    float threshold = glassThreshold - 0.08;
    float top = 0.0, bottom = 0.0;
    bool up = false, down = false;
    [loop]
    for (int k = 1; k <= 32; k++)
    {
        if (k > _RGBudget.w)
            break;
        if (!up)
        {
            float yk = y0 + k * stepM;
            float spec = 1.0 - tex2Dlod(_XYSMap, float4(uv + upUV * (yk - P.y), 0.0, 0.0)).z;
            if (spec < threshold)
            {
                top = yk - stepM * 0.5;
                up = true;
            }
        }
        if (!down)
        {
            float yk = y0 - (k - 1) * stepM;
            float spec = 1.0 - tex2Dlod(_XYSMap, float4(uv + upUV * (yk - P.y), 0.0, 0.0)).z;
            if (spec < threshold)
            {
                bottom = yk + stepM * 0.5;
                down = true;
            }
        }
        if (up && down)
            break;
    }

    float h = top - bottom;
    return up && down && h > 0.9 && h < 7.0 ? float2(bottom, top) : float2(0.0, 0.0);
}

// The same across: where the asset's glass stops left and right of this pixel along the
// facade (x = metres along t), on a fixed 0.1 m grid (painted mullions are thin: 0.25 m
// steps walked over them), _RGBudget2.x steps each way. (left, right), or 0, 0 when
// either side has none within reach.
float2 PaintedPaneSpan(float2 uv, float x, float2 alongUV, float glassThreshold)
{
    const float stepM = 0.1;
    float x0 = floor(x / stepM) * stepM;
    float threshold = glassThreshold - 0.08;
    float right = 0.0, left = 0.0;
    bool r = false, l = false;
    [loop]
    for (int k = 1; k <= 32; k++)
    {
        if (k > _RGBudget2.x)
            break;
        if (!r)
        {
            float xk = x0 + k * stepM;
            float spec = 1.0 - tex2Dlod(_XYSMap, float4(uv + alongUV * (xk - x), 0.0, 0.0)).z;
            if (spec < threshold)
            {
                right = xk - stepM * 0.5;
                r = true;
            }
        }
        if (!l)
        {
            float xk = x0 - (k - 1) * stepM;
            float spec = 1.0 - tex2Dlod(_XYSMap, float4(uv + alongUV * (xk - x), 0.0, 0.0)).z;
            if (spec < threshold)
            {
                left = xk + stepM * 0.5;
                l = true;
            }
        }
        if (r && l)
            break;
    }
    return r && l && right - left > 0.4 ? float2(left, right) : float2(0.0, 0.0);
}
#endif

// The lines the asset painted on its glass: a floor slab's band, a mullion, a frame --
// where its colour, or its shine, stands out from the storey round it. A whole facade
// marked shiny (a full-glass tower) still says where its floors are in its colour map.
// Unsharp masking (Schreiber 1970): the pixel against a storey-wide average of the same
// map, which is a coarser mip of it (Williams 1983) -- the pixel's own level biased by
// how many of its footprints a storey spans, so no texture size is needed. Far away
// the pixel's own level reaches the storey's and nothing stands out: no shimmer.
// 0 glass .. 1 the asset's own painting. Top level only: the bias needs derivatives.
float PaintedWall(float2 uv, float footprint, float3 albedo, float specHere)
{
    float bias = clamp(log2(_RGPane2.z / footprint), 0.0, 12.0);
    float3 around = tex2Dbias(_MainTex, float4(uv, 0.0, bias)).rgb;
    float specAround = ShineBias(uv, bias);

    // Only where the storey round is as shiny as this pixel: where the asset's shine map
    // already parts glass from wall (punched windows, spandrels painted dull) it has
    // said what is glass, and a pane darker than the wall round it is still a pane.
    float allGlass = 1.0 - smoothstep(0.03, 0.08, specHere - specAround);

    // Darker than round it by a fifth and more: a slab band, a shadowed frame. Lighter
    // by over half: a light frame or line. (Glass between dark bands is a little lighter
    // than their average, and a painted sky drifts slowly: neither is a line.)
    float tone = log2((Luma(albedo) + 0.03) / (Luma(around) + 0.03));
    float stroke = max(smoothstep(0.35, 0.75, -tone), smoothstep(0.70, 1.10, tone));

    // Duller than round it, though shiny enough to count as glass: a spandrel.
    float duller = smoothstep(0.06, 0.18, specAround - specHere);
    return max(stroke * allGlass, duller);
}

v2f vert(appdata v)
{
    v2f o;
    float4 local = v.vertex;
    float4 state;
    float4x4 place;
    float baseWeight;
#if defined(RG_LOD)
    int index = (int)(v.color.a * 8.0);      // Color32.a = k * 32 (BuildingInfoBase.InitMeshData)
    place = _BuildingLocations[index];
    state = _BuildingStates[index];
    baseWeight = 1.0;
    o.building = _ObjectIndices[index].xy;
#elif defined(RG_GROUP)
    place = float4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    state = float4(0.0, 100000.0, 0.0, 1.0);   // baked: nothing squashed, nothing extended
    baseWeight = 0.0;
    o.building = v.texcoord1.xy;               // BuildingAI.PopulateGroupData: uvs2 = the colour index
#else
    place = float4x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
    state = _BuildingState;
    baseWeight = v.color.a;
    o.building = _ObjectIndex.xy;
#endif
    // The game's rule: a building under construction is squashed above its progress
    // height (state.y); its base (the ring at y = -1) is stretched down to the ground.
    float y = min(local.y, local.y * 0.01 + abs(state.y));
    float isBase = abs(y + 1.0) <= 0.04 ? 1.0 : 0.0;
    y *= 1.0 + isBase * baseWeight * (state.w - 1.0);
    local.y = y;

    float4 placed = mul(place, local);
    float4 world = mul(unity_ObjectToWorld, placed);
    o.pos = mul(UNITY_MATRIX_VP, world);

    float3 n = mul((float3x3)place, v.normal);
#if defined(RG_GROUP)
    float3 wn = n;                              // baked in world space already
    float baseY = _RGFill.y;                    // no building origin in a baked block
#else
    float3 wn = UnityObjectToWorldNormal(n);
    float baseY = mul(unity_ObjectToWorld, float4(place._m03, place._m13, place._m23, 1.0)).y;
#endif
#if defined(RG_NEAR)
    // A building of Procedural Objects stands where its player put it, often on top of
    // another to make that one taller: its ground is the terrain under it, not its origin.
    baseY = _RGNearBase.w > 0.5 ? _RGNearBase.x : baseY;
#endif
    // Burned (state.x: fire damage over half, 0..1) glass is not glass any more; a
    // negative state.y is the game's collapse/flood clip.
    float stateFade = step(0.0, state.y) * saturate(1.0 - 1.5 * state.x);

    o.worldPos = float4(world.xyz, baseY);
    o.worldNormal = float4(wn, stateFade);
#if defined(RG_NEAR)
    o.uv = v.texcoord.xy * _MainTex_ST.xy + _MainTex_ST.zw;
    o.rect = float4(0.0, 0.0, 1.0, 1.0);
#else
    o.uv = v.texcoord.xy;
    // abs, as the game's group shader takes it: a baked block flags things in the
    // rectangle's sign (its vertex program: o5 = |uv3.xy, uv4.xy|, and uv4.x <= 0 a flag).
    o.rect = abs(float4(v.texcoord2.xy, v.texcoord3.xy));
#endif
    o.screenPos = ComputeScreenPos(o.pos);
    o.rel = world.xyz - _WorldSpaceCameraPos;
    return o;
}

// How much of the pixel is shiny enough to be glass, 0..1. The asset's shine against the
// threshold is an alpha test: a hard edge, and a painted frame thinner than a pixel blinks
// in and out of it whenever the view moves by a fraction of a pixel -- which TAA does every
// frame. So the shine is read four times round the pixel (Direct3D's standard 4x rotated
// grid, spread twice as wide -- +-0.75 px: a window grid finer than a pixel or two is
// filtered, not resolved; each read at the detail of half the pixel, tex2Dgrad, so each
// still tells glass from wall and the share stays true), each read passes the
// threshold over a filtered step a pixel wide -- smoothstep over +-0.75 of the shine's own
// change across the pixel has the slope of a one-pixel ramp at its middle (RenderMan's
// filterstep; Golus 2017) -- and the four are averaged: a thin frame covers its share of
// the pixel, steadily. Close up, where a texel spans many pixels, the four agree and the
// edge is the old one (+-0.04). spec: the shine at the pixel's centre (_XYSMap read
// there). lo, hi: where the reads must stay (an asset's atlas rectangle, LOD and far).
float GlassShine(float2 uv, float2 dx, float2 dy, float2 lo, float2 hi, float threshold, float spec)
{
    float w = max(0.04, fwidth(spec) * 0.75);
    float2 gx = dx * 0.5, gy = dy * 0.5;
    float2 ox = dx * 2.0, oy = dy * 2.0;
    float4 shine = float4(
        ShineGrad(clamp(uv + 0.125 * ox + 0.375 * oy, lo, hi), gx, gy),
        ShineGrad(clamp(uv + 0.375 * ox - 0.125 * oy, lo, hi), gx, gy),
        ShineGrad(clamp(uv - 0.125 * ox - 0.375 * oy, lo, hi), gx, gy),
        ShineGrad(clamp(uv - 0.375 * ox + 0.125 * oy, lo, hi), gx, gy));
    return dot(smoothstep(threshold - w, threshold + w, shine), float4(0.25, 0.25, 0.25, 0.25));
}

// What the first half of the fragment found: everything the second half (Glass.shader) and the
// reflection pass (through the pre-pass, GlassPrePass.shader) need.
struct GlassSurf
{
    float roomShape;
    float2 uv;
    float spec;
    float4 aci;
    float3 illumination;
    float4 asset;
    float strength;
    float detailKept;
    float F0;
    float clarity;
    float4 tintOwn;
    float glassThreshold;
    float nightWindows;
    float dirt;         // 0..1 the dirt on the panes: the building's own (its glass slot), else the global
    float dirtOwn;      // 1 when that is the building's own
    float mask;
    float roomWindow;
    float3 P;
    float baseY;
    float3 n;
    float3 V;
    float dist;
    float footprint;
    float3 t;
    float4 painted;
    float3 albedo;
    float lines;
    float wall;
    float3 np;
    float2 paneCell;
    float4 paneLocal;   // the pixel in its pane (xy 0..1, y up), the pane's size (zw, m): PaneNormal
    float rough;
};

// Part one: what counts as glass here, and the clip of what is not. The caller then shows the
// mask view (_RGMask.z) if it is asked for, before SurfacePane's strength clip.
void SurfaceMask(v2f i, inout GlassSurf s)
{
    // Where a room can stand (Interiors.cginc), before anything is clipped: on OpenGL and
    // Metal a pixel that clipped no longer runs, and its neighbours' derivatives are lost
    // with it -- the guard read the edge of every window band as flat (2026-10-21).
    float3 shapeN = SafeNormalize(i.worldNormal.xyz, float3(0.0, 1.0, 0.0));
    float roomShape = RoomShape(i.rel, shapeN, FacadeTangent(shapeN), i.worldPos.xyz, max(length(fwidth(i.worldPos.xyz)), 1e-4));
    roomShape = _RGDebug.z > 0.5 ? 1.0 : roomShape;   // the preview's "Guards=0": as before them

    float2 uv = i.uv;
    // The pixel's footprint in the maps (GlassShine reads across it), and where reads must
    // stay: anywhere in an asset's own maps (near), inside its atlas rectangle (LOD, far).
    float2 uvDx = ddx(i.uv), uvDy = ddy(i.uv);
    float2 uvLo = float2(-1e6, -1e6), uvHi = float2(1e6, 1e6);
#if !defined(RG_NEAR)
    // Stay inside this asset's rectangle of the atlas, as the game does, so a coarse
    // mip never reads the neighbouring asset.
    float2 fp = max(abs(uvDx), abs(uvDy));
    fp = min(fp, i.rect.zw * 0.25);
    uvLo = i.rect.xy + fp;
    uvHi = i.rect.xy + i.rect.zw - fp;
    uv = clamp(uv, uvLo, uvHi);
#endif
    float spec = ShineAt(uv);
    float4 aci = tex2D(_ACIMap, uv);
    // The illumination map, for rooms lit where the game lights the window (Interiors.cginc,
    // GameWindowLit). Further out as the game's building shader reads it (one level
    // sharper); up close, where a texel spans a pixel or more, the very texel: a blend
    // of a window's value with its frame's 0 passes through the always-lit values below
    // 0.48 -- a lit sliver of room down the edge of every dark window (preview,
    // 2026-10-25). x the texel, y the game's read, z how much of y.
    float2 aciSize = _ACIMap_TexelSize.zw;
    float illumGame = tex2Dbias(_ACIMap, float4(uv, 0.0, -1.0)).b;
    float illumTexel = tex2Dlod(_ACIMap, float4((floor(uv * aciSize) + 0.5) / max(aciSize, 1.0), 0.0, 0.0)).b;
    float texelsPerPixel = max(length(uvDx * aciSize), length(uvDy * aciSize));
    float3 illumination = float3(illumTexel, illumGame, aciSize.x > 0.5 ? saturate(texelsPerPixel * 2.0 - 1.0) : 1.0);

    // This building's own settings: its asset's, where the player gave it some
    // (AssetTextures.cs, at the colour index every tier carries); 255 in a channel is
    // the global setting. Its glass slot (b) holds the rest, resolved: the glass, and
    // what counts as glass -- read first, the mask needs it.
    float4 asset = tex2Dlod(_RGAssets, float4(i.building, 0.0, 0.0));
    float strength = asset.r < 0.999 ? asset.r * (255.0 / 254.0) : _RGGlass.w;
    float detailKept = asset.g < 0.999 ? asset.g * (255.0 / 254.0) : _RGGlass2.y;
    float F0 = _RGGlass.x;
    float clarity = _RGGlass.z;
    float4 tintOwn = _RGTint;
    float glassThreshold = _RGMask.x;
    float nightWindows = _RGMask.y;
    float dirt = _RGDirt.x;
    float dirtOwn = 0.0;
    if (asset.b < 0.999)
    {
        // Slot k = b x 255, the centre of texel k of 256; row r of 3 at v = (r + 0.5) / 3.
        float u = asset.b * (255.0 / 256.0) + 0.5 / 256.0;
        tintOwn = tex2Dlod(_RGAssetGlass, float4(u, 0.5 / 3.0, 0.0, 0.0));
        float4 numbers = tex2Dlod(_RGAssetGlass, float4(u, 1.5 / 3.0, 0.0, 0.0));
        // Its dirt: its own (0..254), or 255 the Glass tab's (_RGDirt.x, as set above).
        float ownDirt = tex2Dlod(_RGAssetGlass, float4(u, 2.5 / 3.0, 0.0, 0.0)).r;
        if (ownDirt < 0.999)
        {
            dirt = ownDirt * (255.0 / 254.0);
            dirtOwn = 1.0;
        }
        F0 = numbers.r;
        clarity = numbers.g;
        glassThreshold = numbers.b;
        nightWindows = numbers.a;
    }

    float shine = GlassShine(uv, uvDx, uvDy, uvLo, uvHi, glassThreshold, spec);
    float mask = GlassMask(shine, spec, aci, glassThreshold, nightWindows) * i.worldNormal.w;
    // (roomShape >= 0, so the min is the mask's: it only ties the guard's derivatives to
    // before the first clip, where a compiler would otherwise sink them.)
    clip(min(mask - 0.004, roomShape + 1.0));
    float roomWindow = RoomWindow(shine, spec, aci, glassThreshold, nightWindows) * i.worldNormal.w;

    s.roomShape = roomShape;
    s.uv = uv;
    s.spec = spec;
    s.aci = aci;
    s.illumination = illumination;
    s.asset = asset;
    s.strength = strength;
    s.detailKept = detailKept;
    s.F0 = F0;
    s.clarity = clarity;
    s.tintOwn = tintOwn;
    s.glassThreshold = glassThreshold;
    s.nightWindows = nightWindows;
    s.dirt = dirt * _RGDirt.y;   // none while the dirt's textures are not loaded
    s.dirtOwn = dirtOwn;
    s.mask = mask;
    s.roomWindow = roomWindow;
}

// Part two: the strength clip, then the pane -- its painted window, its bent normal np (never
// facing away), and the roughness the steady glints leave.
void SurfacePane(v2f i, inout GlassSurf s)
{
    // Strength 0: nothing of ours on this building at all.
    clip(s.strength - 0.004);

    float3 P = i.worldPos.xyz;
    float baseY = i.worldPos.w;
    float3 n = SafeNormalize(i.worldNormal.xyz, float3(0.0, 1.0, 0.0));   // a degenerate mesh normal
    float3 toP = P - _WorldSpaceCameraPos;
    float dist = length(toP);
    float3 V = toP / max(dist, 1e-3);
    float footprint = max(length(fwidth(P)), 1e-4);
    float3 t = FacadeTangent(n);
    float2 uv = s.uv;

    // Close up, the pane the building painted round this pixel: its top and bottom, then
    // its sides. (The derivatives outside every branch.)
    float4 uvPerMetre = UvPerMetre(uv, P, t);
    float4 painted = float4(0.0, 0.0, 0.0, 0.0);   // the painted pane: left, right (along t), bottom, top
#if defined(RG_NEAR)
    if (_RGBudget2.x > 0.5 && footprint < 0.5)   // where a pane is a few pixels wide
    {
        float2 glassSpan = PaintedWindow(uv, P, uvPerMetre.zw, s.glassThreshold);
        if (glassSpan.y > glassSpan.x)
        {
            float2 across = PaintedPaneSpan(uv, dot(P, t), uvPerMetre.xy, s.glassThreshold);
            if (across.y > across.x)
                painted = float4(across, glassSpan);
        }
    }
#endif

    float3 albedo = tex2D(_MainTex, uv).rgb;
    float lines = PaintedWall(uv, footprint, albedo, s.spec);

    float2 paneCell;
    float4 paneLocal;
    float3 np = PaneNormal(n, t, P, baseY, footprint, painted, paneCell, paneLocal);
    float facing = dot(np, -V);
    if (facing < 0.01)
        np = normalize(np + V * (facing - 0.01));   // a bent pane never faces away

    // Steady glints: where the pane's normal changes within the pixel, the glint and the
    // reflection widen to match instead of flickering (Kaplanyan et al. 2016; Tokuyoshi &
    // Kaplanyan 2019; Filament's constants: the kernel's screen-space variance 0.15, at
    // most 0.2 added to alpha squared).
    float3 dnx = ddx(np), dny = ddy(np);
    float kernel = min(2.0 * 0.15 * (dot(dnx, dnx) + dot(dny, dny)), 0.2) * _RGFeatures.w;
    float rough = _RGRefl.y;
    float alpha = rough * rough;
    rough = sqrt(sqrt(saturate(alpha * alpha + kernel)));

    s.P = P;
    s.baseY = baseY;
    s.n = n;
    s.V = V;
    s.dist = dist;
    s.footprint = footprint;
    s.t = t;
    s.painted = painted;
    s.albedo = albedo;
    s.lines = lines;
    s.wall = lines * s.detailKept;
    s.np = np;
    s.paneCell = paneCell;
    s.paneLocal = paneLocal;
    s.rough = rough;
}

// The pane normal as two numbers (octahedral: Cigolle et al., "A Survey of Efficient
// Representations for Independent Unit Vectors", JCGT 3(2) 2014) for the pre-pass's half floats.
float2 OctWrap(float2 v)
{
    return (1.0 - abs(v.yx)) * float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

float2 OctEncode(float3 n)
{
    n /= max(abs(n.x) + abs(n.y) + abs(n.z), 1e-6);
    return n.z >= 0.0 ? n.xy : OctWrap(n.xy);
}

#endif
