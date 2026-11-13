// Real Glass -- everything a glass pixel needs: what counts as glass, the pane it belongs to and
// how that pane is bent, Fresnel, the screen-space trace, what fills a reflection the screen
// cannot give, and the sun's glint. Included by Glass.shader. (Behind the glass there is nothing
// of ours: by day a dark inside, by night the game's own window -- Glass.shader.)
//
// Sources (credited in README.md and LICENSE):
//  - McGuire & Mara, "Efficient GPU Screen-Space Ray Tracing", JCGT 3(4) 2014 (BSD 2-Clause):
//    TraceScreenRay is a port of their traceScreenSpaceRay1.
//  - Uludag, "Hi-Z Screen-Space Cone-Traced Reflections", GPU Pro 5 (2014): TraceHiZ walks a
//    min-depth pyramid of the screen, whole cells at a time where nothing can be hit; with the
//    maximum beside each minimum (Tevs, Ihrke & Seidel, "Maximum Mipmaps", I3D 2008) it also
//    crosses whole cells a ray passes behind.
//  - Schlick 1994 (Fresnel), Walter et al. 2007 (GGX), Jimenez 2014 (interleaved gradient noise),
//    Hoskins (hash without sine).
//  - Pane bending: insulating-glass "pillowing" (a plate's first mode, Navier) and the roller
//    wave of toughened glass (ASTM C1651).
//  - Layering of the reflection (screen first, then a live cube, one face a frame): Mirror's Edge
//    Catalyst, "Lighting the City of Glass" (GDC 2016); the sky cube: GTA V's per-frame cubemap,
//    with the clouds drawn into it as Unreal's Sky Light captures volumetric clouds.
//  - What is mirrored is last frame's FINISHED picture, reprojected (Killzone Shadow Fall, GDC
//    2014): fog, clouds, the lamps' glow and the other glass are in the reflections.
//  - The buildings round, off screen: a top-down height map of the city round the view (as
//    CRYENGINE's height-map ambient occlusion and Unreal's heightfield sky occlusion of
//    reflections read one), walked cell by cell (Amanatides & Woo, Eurographics 1987): CityHit.
//  - The glass's edge (GlassShine, Glass.shader): the shine threshold anti-aliased as an alpha test
//    is -- a filtered step a pixel wide (RenderMan's filterstep, Apodaca & Gritz, Advanced
//    RenderMan 1999; Golus, "Anti-aliased Alpha Test", 2017) over four reads in Direct3D 11's
//    standard 4x rotated grid.
//  - Steady glints: specular anti-aliasing (Kaplanyan et al. 2016; Tokuyoshi & Kaplanyan 2019),
//    Filament's constants.

#ifndef REALGLASS_COMMON_INCLUDED
#define REALGLASS_COMMON_INCLUDED

#include "UnityCG.cginc"
#include "Finite.cginc"   // Finite / FiniteAlpha / SafeNormalize / SafeDiv: no NaN on any API

// ---------------------------------------------------------------------------------------------
// Uniforms. GlassUniforms.cs is the one place that writes them.
// ---------------------------------------------------------------------------------------------

float4 _RGGlass;      // x reflectance head-on (F0, the chosen glass), y reflectance edge-on, z clarity (VLT), w strength
float4 _RGGlass2;     // x silvery edge-on 0..1, y painted detail kept 0..1, z how (0 as painted, 1 under the sheen: the preview's), w -
float4 _RGColorMap;   // xy the game's object colour map (width, height): where a building's colour index points
float4 _RGFinished;   // x 1 while _RGSceneFinished holds last frame's finished picture
float4 _RGTint;       // rgb glass tint (linear, brightest channel 1), a own-colour mix 0..1
float4 _RGMask;       // x shininess threshold, y night-window weight, z show mask (1), w -
float4 _RGRefl;       // x reflection brightness, y roughness 0..1, z sun glint, w screen-edge fade 0..0.5
float4 _RGTrace;      // x trace on (1), y max steps, z stride (px), w thickness scale
float4 _RGFill;       // x off-screen fill (0 mirrored screen, 1 city colour, 2 sky), y ground level (world y), z cube mips, w -
float4 _RGPane;       // x pane width (m), y pillow bow (m), z tilt (rad), w roller-wave peak (m)
float4 _RGPane2;      // x bow bias -1 (all sunk) .. +1 (all bulging), y roller-wave length (m), z storey height (m), w -
float4 _RGLight;      // x daylight 0 (night) .. 1 (day), y the inside by day (x the sky's light), z -, w -
float4 _RGSunDir;     // xyz towards the sun (world), w sun above the horizon 0..1
float4 _RGSunColor;   // rgb the sun's radiance (colour x intensity)
float4 _RGSunThrough; // x 1 while the sky cube's alpha is the clouds' opacity (GlassSky), y the mip it is read at
float4 _RGAmbSky;     // rgb light from the sky (trilight "sky")
float4 _RGAmbGround;  // rgb light from below (trilight "ground")
float4 _RGFeatures;   // x the buildings round in the reflections, y -, z -, w steady glints
float4 _RGDebug;      // x: 0 off; the GPU preview's checks (12 the sun through the clouds; 1 trace, 2 depth, 3 screen copy, 5 the finished
                      // picture reprojected, magenta off it; 6 the city walk: green a building met, blue none,
                      // red no map; 8 the painted pane found: green, its border yellow, red none; 9 the
                      // hierarchical depth at level y, red where it is wrong; 10 the painted lines kept:
                      // yellow, the glass blue; 13 where a room can stand: green, red not). z 1: the rooms
                      // as before RoomShape (the preview's "Guards=0"). The mod never sets it.

// The city from above (CityMap.cs) and the walk across it (CityHit), with the walk's budget
// _RGBudget: in their own file since 2026-11-12, which the water's reflection takes too.
#include "CityWalk.cginc"

// Each building's own glass (AssetTextures.cs): the settings its ASSET has of its own, set by the
// player for every copy of that asset (RealGlassAssets.xml).
sampler2D _RGAssets;          // laid out as the object colour map, a texel per building: r strength, g painted detail
                              // (0..254 = 0..1), b its glass's slot in _RGAssetGlass (0..254); 255: the global setting
sampler2D _RGAssetGlass;      // 256 x 3: per slot, row 0 the tint (linear, brightest 1) and the own-colour mix,
                              // row 1 the reflectivity, the clarity, the shininess needed, the lit windows (0/1),
                              // row 2 the dirt on its panes (r)
float4 _RGDirt;               // x the Glass tab's Dirt: the share of buildings dirty (0..1; Dirt.cginc), y 1 while its
                              // textures are loaded, z how far it has built up since rain (0..1; 1 Dynamic dirt off)

float4 _RGBudget2;    // x steps each way across to a painted pane's sides, y steps of the Hi-Z trace

sampler2D _RGScene;           // the lit opaque picture, copied before any glass (mipmapped): what is UNDER a pane
float4 _RGScene_TexelSize;
sampler2D _RGSceneFinished;   // last frame's finished picture (fog, clouds, lamps, glass; mipmapped): what a pane MIRRORS
float4x4 _RGPrevVP;           // last frame's camera (OpenGL-convention projection x view): where a point was on it
sampler2D _RGCity;            // 1x1: the average colour of the lower half of the screen
samplerCUBE _RGSky;           // the live sky (or the game's environment map)
UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
sampler2D _RGHiZ;             // the scene's eye depth (m): mip k the nearest (r) and farthest (g) of each 2 x 2 of mip k - 1 (GlassScreen.cs)
float4 _RGHiZInfo;            // x levels built, yz level 0's size (px), w 1 while built

// ---------------------------------------------------------------------------------------------
// Small helpers.
// ---------------------------------------------------------------------------------------------

// Dave Hoskins, "Hash without Sine" (MIT): stable for the cell indices a city has (|n| < 1e5).
float Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float2 Hash22(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * float3(0.1031, 0.1030, 0.0973));
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.xx + p3.yz) * p3.zy);
}

// Jimenez 2014 ("Next Generation Post Processing in Call of Duty: Advanced Warfare"): a per-pixel
// jitter that does not clump like white noise.
float Ign(float2 pixel)
{
    return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float Luma(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

float3 NormaliseTint(float3 c)
{
    return c / max(max(c.r, max(c.g, c.b)), 0.02);
}

// ---------------------------------------------------------------------------------------------
// Which building a pixel is: its place in the game's object colour map (the colour index every
// distance tier carries, a sub-building its parent's), as the building's id -- the city walk's
// own building, which it must not meet.
// ---------------------------------------------------------------------------------------------

float IdOf(float2 colourIndex)
{
    // The index is a texel centre, (k + 0.5) / size: exact on every tier.
    float2 cell = floor(colourIndex * _RGColorMap.xy);
    return cell.x + cell.y * _RGColorMap.x;
}

// ---------------------------------------------------------------------------------------------
// What counts as glass: the asset's own specular map, read the way the game reads it
// (spec = 1 - _XYSMap.z), plus -- optionally -- the windows it lights at night (_ACIMap.b > 0.48).
// shine: the share of the pixel at or over the threshold, its edge anti-aliased (GlassShine,
// Glass.shader); spec, aci: the maps at the pixel's centre. _ACIMap.r is the asset's cut-out:
// where the game discards, so do we -- at the centre, as the game tests it. threshold (the
// shininess needed) and nightWindows (1: the lit windows count) are the building's: its asset's
// own, or the global _RGMask.x / .y (Glass.shader).
// ---------------------------------------------------------------------------------------------

// The share of a lit window that counts as glass below the threshold: from 0.35 under it to 0.1 under.
float NightWindowShare(float spec, float4 aci, float threshold, float nightWindows)
{
    float night = smoothstep(0.46, 0.56, aci.b) * nightWindows;
    return night * smoothstep(threshold - 0.35, threshold - 0.1, spec);
}

float GlassMask(float shine, float spec, float4 aci, float threshold, float nightWindows)
{
    float m = max(shine, NightWindowShare(spec, aci, threshold, nightWindows));
    return m * step(aci.r, 0.5);
}

// The window a room behind the glass fills at night: the glass, and a lit window that counts only
// partly (NightWindowShare) taken whole from half way. The game lights a building's windows all at
// once, whole: a room over only a share of one let the rest of the lit window shine through it, and
// the rooms vanished when the building's lights came on (2026-10-21). Which windows the game can light
// is its own rule here (Interiors.cginc, GameWindowLit: 20 x b up to 0.48, its bands above), not
// NightWindowShare's ramp from 0.46 to 0.56: a window painted at 0.50 (128, a common value) took a
// room over three quarters of it and the game's light under the rest (2026-10-28).
float RoomWindow(float shine, float spec, float4 aci, float threshold, float nightWindows)
{
    float lightable = saturate(aci.b * 20.0) * nightWindows;
    float m = max(shine, saturate(2.0 * lightable * smoothstep(threshold - 0.35, threshold - 0.1, spec)));
    return m * step(aci.r, 0.5);
}

// ---------------------------------------------------------------------------------------------
// The facade and its panes. The frame comes from the GEOMETRIC normal: t runs along the facade,
// level; floors are level whatever the facade does. A pane is paneWidth wide and one storey tall.
// ---------------------------------------------------------------------------------------------

float3 FacadeTangent(float3 n)
{
    float2 h = float2(n.z, -n.x);            // up x n, level
    float l = length(h);
    return l > 1e-3 ? float3(h.x / l, 0.0, h.y / l) : float3(1.0, 0.0, 0.0);
}

// A facade's identity for the hashes: its heading, quantised to 5 degrees, so one flat facade is
// one pattern and the next one round the corner is another.
float FacadeId(float3 n)
{
    // A flat roof pane (a skylight, an atrium) has no heading: atan2(0, 0) is 0 on Direct3D but
    // undefined on OpenGL and Metal, where a NaN here would spread through the pane's normal to
    // the whole pixel (a black pane). Such a pane gets the id Direct3D's 0 gives (audit, 2026-10-11).
    if (dot(n.xz, n.xz) < 1e-6)
        return 35.0;
    return floor((atan2(n.z, n.x) + 3.14159265) * 11.459);
}

// The pane normal: the facade normal bent by the pane's pillow (both panes of an insulating unit
// bow with its gas: w = w0 sin(pi u) sin(pi v), the first mode of a plate under uniform pressure),
// its install tilt and, close up, the roller wave. footprint = metres a pixel covers: a bend
// finer than the pixel is averaged away (never aliased). painted: the window the asset painted
// round the pixel (left, right along t; bottom, top), 0 when none -- then its panes are that
// window's, split into panes about the pane width wide; else a grid of pane width by storey.
// paneLocal: where the pixel is in its pane (xy 0..1, y up) and the pane's size (zw, metres).
float3 PaneNormal(float3 n, float3 t, float3 P, float baseY, float footprint, float4 painted, out float2 paneCell, out float4 paneLocal)
{
    float w = _RGPane.x;
    float h = _RGPane2.z;
    float fid = FacadeId(n);
    float2 c = float2(dot(P, t) / w, (P.y - baseY) / h);
    float2 which = float2(0.0, 0.0);
    if (painted.y > painted.x)
    {
        float2 span = float2(painted.y - painted.x, painted.w - painted.z);
        float2 count = max(round(span / float2(w, h)), 1.0);
        w = span.x / count.x;
        h = span.y / count.y;
        c = float2((dot(P, t) - painted.x) / w, (P.y - painted.z) / h);
        which = round(painted.xz * 4.0);   // one window's panes are not the next one's
    }
    paneCell = floor(c) + which;
    float2 f = c - floor(c);
    paneLocal = float4(f, w, h);
    float2 r = Hash22(paneCell + float2(fid * 17.0, fid * 3.0));
    float2 r2 = Hash22(paneCell.yx + float2(fid, 91.0));

    // Pillow: amplitude in metres, sign from the weather bias and the pane's own seal.
    float sign1 = (r.x * 2.0 - 1.0) + _RGPane2.x;
    float w0 = _RGPane.y * (0.35 + 0.65 * r.y) * clamp(sign1 * 1.5, -1.0, 1.0);
    const float PI = 3.14159265;
    float su = sin(PI * f.x), cu = cos(PI * f.x);
    float sv = sin(PI * f.y), cv = cos(PI * f.y);
    // A pane a few pixels wide bends nothing you could see -- only speckle that flickers as the
    // view moves by a fraction of a pixel (TAA, every frame): the pillow fades out from 12 pixels
    // down to 6, the tilt below from 8 to 4. A large facade far away is one calm sheet.
    float pillowVis = saturate(min(w, h) / max(footprint * 6.0, 1e-4) - 1.0);
    float du = w0 * PI / w * cu * sv * pillowVis;   // dw / dx (x along t)
    float dv = w0 * PI / h * su * cv * pillowVis;   // dw / dy

    // Install tilt: a few tenths of a degree, the reason a neighbour's edge steps at every pane
    // (faded with the pane's size on screen, above).
    float tiltVis = saturate(min(w, h) / max(footprint * 4.0, 1e-4) - 1.0);
    du += (r2.x * 2.0 - 1.0) * _RGPane.z * tiltVis;
    dv += (r2.y * 2.0 - 1.0) * _RGPane.z * tiltVis;

    // Roller wave: across one direction of the pane (the furnace's), ~0.35 m long.
    float lambda = max(_RGPane2.y, 0.05);
    float waveVis = saturate(lambda / max(footprint * 4.0, 1e-4) - 0.5);
    float along = (r.x > 0.5) ? c.x * w : c.y * h;
    float slope = _RGPane.w * 2.0 * PI / lambda * cos(2.0 * PI * along / lambda + r2.x * 6.283) * waveVis;
    if (r.x > 0.5) du += slope; else dv += slope;

    // Roof glass (skylights, atria) has no storeys to be bowed between: bend facades only.
    float vertical = saturate((1.0 - abs(n.y)) * 3.0);
    float3 up = float3(0.0, 1.0, 0.0);
    return normalize(n - (du * t + dv * up) * vertical);
}

// ---------------------------------------------------------------------------------------------
// Fresnel: Schlick's curve, rescaled so edge-on is the "glancing" setting rather than 1 (a raised
// camera sees every facade edge-on; physical 1 there read as "too bright" in the first attempt).
// In colour: head-on the coating's tint (F0 x coat), and the rise toward edge-on the same for
// every colour -- so a coated pane turns silvery edge-on, as real ones do. _RGGlass2.x says how
// far: 0 keeps the tint at every angle (the look before this), 1 is Schlick's curve per channel.
// Fs, the luminance, is the same either way: what the glass lets through does not change.
// ---------------------------------------------------------------------------------------------

float3 GlassFresnel(float cosTheta, float F0, float3 coat, out float Fs)
{
    // (1 - cos)^5 by products: pow is exp2(5 log2(x)), and log2(0) -- a pane seen exactly head-on
    // -- is -inf, which fast math (Metal's default) need not carry through to 0.
    float m = 1.0 - saturate(cosTheta);
    float m2 = m * m;
    float f = m2 * m2 * m;
    float Fmax = max(_RGGlass.y, F0);
    Fs = F0 + (Fmax - F0) * f;
    float3 physical = F0 * coat + (Fmax - F0 * coat) * f;
    return lerp(Fs * coat, physical, _RGGlass2.x);
}

// ---------------------------------------------------------------------------------------------
// The screen-space trace: McGuire & Mara's DDA (BSD 2-Clause), in Unity's view space (-z ahead).
// "Pixel" coordinates come from UNITY_MATRIX_P followed by ComputeScreenPos's affine map, so the
// Direct3D flip of render textures is handled exactly as for this pixel's own screen position,
// and the homogeneous interpolation of the original stays valid.
// ---------------------------------------------------------------------------------------------

float4 ToPixelH(float3 cs)
{
    float4 clip = mul(UNITY_MATRIX_P, float4(cs, 1.0));
    float4 s = ComputeScreenPos(clip);
    return float4(s.x * _ScreenParams.x, s.y * _ScreenParams.y, 0.0, clip.w);
}

float SceneZ(float2 pixel)
{
    float2 uv = (floor(pixel) + 0.5) / _ScreenParams.xy;
    return -LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0.0, 0.0)));
}

// Returns true on a hit; hitUV is where to read the picture. clipped: the ray left the screen
// before its length ran out (what the off-screen fill is for).
bool TraceScreenRay(float3 csOrig, float3 csDir, float jitter, out float2 hitUV, out float hitDistance, out bool clipped)
{
    hitUV = float2(0.0, 0.0);
    hitDistance = 0.0;
    clipped = false;

    float stride = _RGTrace.z;
    float maxSteps = _RGTrace.y;
    float maxDistance = 3000.0;
    float nearPlaneZ = -_ProjectionParams.y;

    // Clip to the near plane. (The divisions of both traces are kept off zero: an edge case's
    // 0 / 0 is a NaN that fast math need not keep out of the min()s below.)
    float rayLength = ((csOrig.z + csDir.z * maxDistance) > nearPlaneZ) ?
        clamp(SafeDiv(nearPlaneZ - csOrig.z, csDir.z), 0.0, maxDistance) : maxDistance;
    float3 csEnd = csOrig + csDir * rayLength;

    float4 H0 = ToPixelH(csOrig);
    float4 H1 = ToPixelH(csEnd);
    float k0 = 1.0 / max(H0.w, 1e-4), k1 = 1.0 / max(H1.w, 1e-4);
    float3 Q0 = csOrig * k0, Q1 = csEnd * k1;
    float2 P0 = H0.xy * k0, P1 = H1.xy * k1;

    // Clip the far end to the screen, so no step is spent outside it (P, Q and k are all linear
    // in the same parameter, so one alpha moves all three).
    float2 lo = float2(0.5, 0.5), hi = _ScreenParams.xy - 0.5;
    float2 dP = P1 - P0;
    float alpha = 1.0;
    if (P1.x > hi.x) alpha = min(alpha, SafeDiv(hi.x - P0.x, dP.x));
    if (P1.x < lo.x) alpha = min(alpha, SafeDiv(lo.x - P0.x, dP.x));
    if (P1.y > hi.y) alpha = min(alpha, SafeDiv(hi.y - P0.y, dP.y));
    if (P1.y < lo.y) alpha = min(alpha, SafeDiv(lo.y - P0.y, dP.y));
    alpha = saturate(alpha);
    clipped = alpha < 0.999;
    P1 = lerp(P0, P1, alpha);
    Q1 = lerp(Q0, Q1, alpha);
    k1 = lerp(k0, k1, alpha);

    // A degenerate line covers at least one pixel.
    P1 += (dot(P1 - P0, P1 - P0) < 0.0001) ? float2(0.01, 0.01) : float2(0.0, 0.0);
    float2 delta = P1 - P0;

    // Permute so the primary iteration is in x.
    bool permute = false;
    if (abs(delta.x) < abs(delta.y))
    {
        permute = true;
        delta = delta.yx; P0 = P0.yx; P1 = P1.yx;
    }

    float stepDir = sign(delta.x);
    float invdx = stepDir / delta.x;
    float3 dQ = (Q1 - Q0) * invdx;
    float dk = (k1 - k0) * invdx;
    float2 dPs = float2(stepDir, delta.y * invdx);

    dPs *= stride; dQ *= stride; dk *= stride;
    P0 += dPs * jitter; Q0 += dQ * jitter; k0 += dk * jitter;

    float3 Q = Q0;
    float end = P1.x * stepDir;
    float k = k0, stepCount = 0.0, prevZMaxEstimate = csOrig.z;
    float rayZMin = prevZMaxEstimate, rayZMax = prevZMaxEstimate;
    float sceneZMax = rayZMax + 100000.0;
    float2 P = P0;
    float2 hitPixel = P0;
    float thick = 1.0;
    bool hit = false;

    [loop]
    for (; (P.x * stepDir) <= end && stepCount < maxSteps; P += dPs, Q.z += dQ.z, k += dk, stepCount += 1.0)
    {
        rayZMin = prevZMaxEstimate;
        rayZMax = (dQ.z * 0.5 + Q.z) / (dk * 0.5 + k);
        prevZMaxEstimate = rayZMax;
        if (rayZMin > rayZMax) { float tmp = rayZMin; rayZMin = rayZMax; rayZMax = tmp; }

        hitPixel = permute ? P.yx : P;
        sceneZMax = SceneZ(hitPixel);
        // A pixel of the depth buffer is a slab this thick. Crossing a surface inside one step is
        // caught whatever the stride (the test takes the step's whole z range); the thickness
        // only decides what a ray may pass BEHIND: a metre near, 1% of the depth far away.
        thick = max(1.0, -sceneZMax * 0.01) * _RGTrace.w;
        if (rayZMax >= sceneZMax - thick && rayZMin < sceneZMax)
        {
            hit = true;
            break;
        }
    }

    if (hit)
    {
        // Refine towards the surface: a few halvings of the last stride.
        float2 Pa = P - dPs;
        float za = Q.z - dQ.z, ka = k - dk;
        float2 Pb = P;
        float zb = Q.z, kb = k;
        [unroll]
        for (int i = 0; i < 4; i++)
        {
            float2 Pm = (Pa + Pb) * 0.5;
            float zm = (za + zb) * 0.5, km = (ka + kb) * 0.5;
            float rz = zm / km;
            float2 pix = permute ? Pm.yx : Pm;
            float sz = SceneZ(pix);
            if (rz < sz) { Pb = Pm; zb = zm; kb = km; }
            else { Pa = Pm; za = zm; ka = km; }
        }
        hitPixel = permute ? Pb.yx : Pb;
        hitUV = hitPixel / _ScreenParams.xy;
        float hitZ = zb / kb;
        hitDistance = abs(hitZ - csOrig.z) / max(abs(csDir.z), 1e-3);
        clipped = false;
    }
    return hit;
}

// ---------------------------------------------------------------------------------------------
// The same ray over the hierarchical depth (Uludag, GPU Pro 5). Along McGuire & Mara's ray, P
// (pixels) and k = 1 / eye depth are both linear on screen, so where the ray leaves a cell and
// where it reaches a depth are exact. A cell holds nothing to hit when the ray stays in front of
// its NEAREST depth all the way through it, or behind its FARTHEST by more than a pixel's
// thickness (Tevs et al.'s maximum mipmaps): the ray crosses it whole and climbs a level (a cell
// of level 7 is 128 pixels). Otherwise it descends -- first, moving away, to that nearest depth --
// and a pixel (level 0) that passes neither test is a hit, the plain trace's test exactly.
// ---------------------------------------------------------------------------------------------

// A cell of a level: its nearest (x) and farthest (y) depth (clamped to the level's size: an odd
// edge's last cell holds the rest).
float2 HiZRange(float2 cell, float level)
{
    float2 size = max(floor(_RGHiZInfo.yz / exp2(level)), 1.0);
    return tex2Dlod(_RGHiZ, float4((min(cell, size - 1.0) + 0.5) / size, 0.0, level)).rg;
}

// A pixel of the depth buffer is a slab this thick, as the plain trace has it: a metre near, 1%
// of the depth far away. Rising with the depth, so a cell's farthest bounds all its pixels'.
float TraceThickness(float depth)
{
    return max(1.0, depth * 0.01) * _RGTrace.w;
}

bool TraceHiZ(float3 csOrig, float3 csDir, float jitter, out float2 hitUV, out float hitDistance, out bool clipped)
{
    hitUV = float2(0.0, 0.0);
    hitDistance = 0.0;
    clipped = false;

    float maxDistance = 3000.0;
    float nearPlaneZ = -_ProjectionParams.y;
    float rayLength = ((csOrig.z + csDir.z * maxDistance) > nearPlaneZ) ?
        clamp(SafeDiv(nearPlaneZ - csOrig.z, csDir.z), 0.0, maxDistance) : maxDistance;
    float3 csEnd = csOrig + csDir * rayLength;

    // The ray on screen in the pyramid's pixels, and 1 / eye depth along it (a perspective
    // camera's w is the eye depth).
    float2 toPyramid = _RGHiZInfo.yz / _ScreenParams.xy;
    float4 H0 = ToPixelH(csOrig);
    float4 H1 = ToPixelH(csEnd);
    float k0 = 1.0 / max(H0.w, 1e-4), k1 = 1.0 / max(H1.w, 1e-4);
    float2 P0 = H0.xy * k0 * toPyramid, P1 = H1.xy * k1 * toPyramid;

    // Clipped to the screen, as the plain trace.
    float2 lo = float2(0.5, 0.5), hi = _RGHiZInfo.yz - 0.5;
    float2 dP = P1 - P0;
    float alpha = 1.0;
    if (P1.x > hi.x) alpha = min(alpha, SafeDiv(hi.x - P0.x, dP.x));
    if (P1.x < lo.x) alpha = min(alpha, SafeDiv(lo.x - P0.x, dP.x));
    if (P1.y > hi.y) alpha = min(alpha, SafeDiv(hi.y - P0.y, dP.y));
    if (P1.y < lo.y) alpha = min(alpha, SafeDiv(lo.y - P0.y, dP.y));
    alpha = saturate(alpha);
    bool leaves = alpha < 0.999;
    P1 = lerp(P0, P1, alpha);
    k1 = lerp(k0, k1, alpha);
    dP = P1 - P0;
    float dk = k1 - k0;

    // No axis quite still (its cell edges would never be met); 0.01 px past an edge is in the
    // next cell.
    float2 ahead = step(0.0, dP);
    float2 safe = (ahead * 2.0 - 1.0) * max(abs(dP), 1e-4);
    float2 nudge = 0.01 / abs(safe);
    float len = max(abs(dP.x), abs(dP.y));

    // A pixel or two out (the origin is already off the glass), jittered as the plain trace.
    float t = (1.0 + jitter) / max(len, 1.0);
    float topLevel = _RGHiZInfo.x - 1.0;
    float level = min(2.0, topLevel);
    // The budget's steps, scaled by the trace quality as the plain trace's are (Ultra's 80 = all).
    float steps = min(_RGBudget2.y * saturate(_RGTrace.y / 80.0), 128.0);
    bool hit = false;
    float tHit = 0.0;

    [loop]
    for (int i = 0; i < 128; i++)
    {
        if (i >= steps || t > 1.0)
            break;
        float size = exp2(level);
        float2 cell = floor((P0 + dP * t) / size);
        float2 tEdge = ((cell + ahead) * size - P0) / safe;
        float tOut = min(tEdge.x, tEdge.y);
        float tNext = tOut + (tEdge.x < tEdge.y ? nudge.x : nudge.y);
        tOut = min(tOut, 1.0);
        float zIn = 1.0 / (k0 + dk * t);
        float zOut = 1.0 / (k0 + dk * tOut);
        // A pixel is judged on the depth texture itself, as the plain trace judges it (the pyramid
        // holds half floats); a coarser cell on the pyramid's nearest and farthest.
        float2 range = level > 0.5 ? HiZRange(cell, level) : (-SceneZ((cell + 0.5) / toPyramid)).xx;
        if (max(zIn, zOut) < range.x || min(zIn, zOut) > range.y + TraceThickness(range.y))
        {
            // In front of everything in the cell all the way through it, or behind all of it.
            t = tNext;
            level = min(level + 1.0, topLevel);
        }
        else if (level > 0.5)
        {
            // (A depth of 0 -- the pyramid's floor -- would be 1 / 0.)
            if (dk < 0.0 && zIn < range.x)
                t = max(t, (1.0 / max(range.x, 1e-3) - k0) / dk);
            level -= 1.0;
        }
        else
        {
            hit = true;
            tHit = abs(dk) > 1e-12 ? clamp((1.0 / max(range.x, 1e-3) - k0) / dk, t, tOut) : t;
            break;
        }
    }

    if (hit)
    {
        hitUV = (P0 + dP * tHit) / _RGHiZInfo.yz;
        float hitZ = -1.0 / (k0 + dk * tHit);
        hitDistance = abs(hitZ - csOrig.z) / max(abs(csDir.z), 1e-3);
    }
    else
    {
        clipped = leaves && t > 1.0;
    }
    return hit;
}

// The reflections' trace: over the hierarchical depth where GlassScreen built one (the budget has
// it and a float target exists), else the plain one.
bool TraceReflection(float3 csOrig, float3 csDir, float jitter, out float2 hitUV, out float hitDistance, out bool clipped)
{
    bool hit;
    if (_RGHiZInfo.w > 0.5 && _RGBudget2.y > 0.5)
        hit = TraceHiZ(csOrig, csDir, jitter, hitUV, hitDistance, clipped);
    else
        hit = TraceScreenRay(csOrig, csDir, jitter, hitUV, hitDistance, clipped);
    return hit;
}

// ---------------------------------------------------------------------------------------------
// What a reflection shows when the screen has nothing for it.
// ---------------------------------------------------------------------------------------------

// The screen's own picture, mirrored back in at the edge a ray leaves by (the "smart invert"):
// a facade seen from above then shows streets and roofs, not empty light.
float2 MirrorUV(float2 uv)
{
    float2 m = abs(frac(uv * 0.5) * 2.0 - 1.0);   // triangle wave: 0..1..0 over 0..2
    return 1.0 - m;
}

// Every picture the glass reads -- the sky cube (another camera's render, Volumetric Weather's
// clouds in it), the copies, last frame's finished picture, the city colour -- is read through
// Finite: a bad texel there stays one bad reflection, never a NaN in the glass.
float3 SkyRadiance(float3 R, float roughness)
{
    return Finite(texCUBElod(_RGSky, float4(R, roughness * _RGFill.z)).rgb);
}

// How much of the sun's disc the clouds leave, 0..1: the live sky's cube holds their opacity in its
// alpha (GlassSky: cleared after the skybox, then their premultiplied blend), so it is read where the
// sun is. The sun's own light in the glass -- its glint, the sunny side of a mirrored neighbour -- is
// its direct beam, which a cloud in front of the disc takes away (Beer-Lambert); the clouds' own
// light is already in what the glass mirrors. 1 where the cube has no such alpha (the game's map).
float SunThrough()
{
    float cloud = _RGSunThrough.x > 0.5 ? texCUBElod(_RGSky, float4(_RGSunDir.xyz, _RGSunThrough.y)).a : 0.0;
    return 1.0 - FiniteIn(cloud, 0.0, 1.0);
}

// The city's average colour (1x1).
float3 CityColour()
{
    return Finite(tex2Dlod(_RGCity, float4(0.5, 0.5, 0.0, 0.0)).rgb);
}

// Where a world point is on screen, in the convention the copies are read with: on this frame's
// camera, or -- lastFrame -- on last frame's, which the finished picture was drawn with. False
// behind the camera.
bool ScreenUV(float3 W, bool lastFrame, out float2 uv)
{
    // One exit: fxc takes early returns in inlined helpers for "potentially uninitialized".
    float4 clip = mul(UNITY_MATRIX_VP, float4(W, 1.0));
    float4 s = ComputeScreenPos(clip);
    float4 c = mul(_RGPrevVP, float4(W, 1.0));
    uv = lastFrame ? c.xy / max(c.w, 1e-4) * 0.5 + 0.5 : s.xy / max(s.w, 1e-4);
    return (lastFrame ? c.w : clip.w) > _ProjectionParams.y;
}

// What the glass mirrors at a place on screen: the finished picture (fog, clouds, lamps, the other
// glass) when there is one, else the copy taken before them.
float3 MirroredPicture(float2 uv, float lod, bool finished)
{
    float3 c;
    if (finished)
        c = tex2Dlod(_RGSceneFinished, float4(uv, 0.0, lod)).rgb;
    else
        c = tex2Dlod(_RGScene, float4(uv, 0.0, lod)).rgb;
    return Finite(c);
}

// Below the horizon and nothing hit: the ground at the building's feet, where the reflected ray
// meets it, is almost always what a facade shows from a city-builder's camera. Found on screen it
// is read there; off screen it is mirrored in (mode 0), or the city's average colour (mode 1).
float3 GroundFill(float3 P, float3 R, float roughness, float groundY)
{
    float3 city = CityColour();
    float3 result = city;
    if (_RGFill.x > 1.5)
    {
        result = SkyRadiance(R, roughness);
    }
    else if (_RGFill.x < 0.5)
    {
        float drop = max(P.y - groundY, 1.0);
        float t = min(drop / max(-R.y, 0.02), 4000.0);
        float3 G = P + R * t;
        bool finished = _RGFinished.x > 0.5;
        float2 uv;
        if (ScreenUV(G, finished, uv))
        {
            float outside = max(max(-uv.x, uv.x - 1.0), max(-uv.y, uv.y - 1.0));
            float2 muv = MirrorUV(uv);
            // Sharp where the ground point is on screen; blurred the further outside it had to
            // be mirrored from (a mirrored street is a plausible street, not the right one).
            float lod = saturate(outside * 4.0) * 1.5 + saturate(outside) * 2.5 + roughness * 3.0;
            float3 mirrored = MirroredPicture(muv, lod, finished);
            result = lerp(mirrored, city, saturate(outside - 0.6));
        }
    }
    return result;
}

// (The buildings round, off screen -- DecodeCity and CityHit, the walk across the city from above --
// are in CityWalk.cginc, included at the top.)

// What a mirrored building looks like from across the street, which the screen never showed from
// that side: its own facade, drawn from what is known of it. seen: the building as the screen shows
// it where the point met falls (blurred; the city's average colour where that is off screen) -- its
// own colours, its haze, and at night its windows as the game lights them. By day that body,
// lighter on its sunny side, its glass mirroring the sky above its horizon and the city below, a
// darker band at every floor slab; by night the seen colour itself: no lights of ours.
// span: pixels a storey of it covers in the reflection -- under a couple, no bands.
float3 NeighbourRadiance(float3 H, float3 R, float3 nb, float span, float roughness, float3 seen)
{
    float3 city = CityColour();
    float detail = saturate(span * 0.5 - 0.5);

    float3 Rn = reflect(R, nb);
    float3 mirrored = Rn.y > 0.0 ? SkyRadiance(Rn, max(roughness, 0.3)) : city;
    float sunSide = saturate(dot(nb, _RGSunDir.xyz)) * saturate(_RGSunDir.w) * SunThrough();
    float3 body = seen * (0.7 + 0.6 * sunSide);
    float y = max(H.y - _RGFill.y, 0.0);
    float band = step(frac(y / _RGPane2.z), 0.2) * detail;
    float3 day = lerp(body, mirrored * 0.45 + body * 0.35, 0.6) * (1.0 - 0.3 * band);
    return lerp(seen, day, _RGLight.x);
}

// ---------------------------------------------------------------------------------------------
// The sun's glint: GGX (Walter et al. 2007), capped so a mirror-smooth pane cannot put a firefly
// through the game's bloom.
// ---------------------------------------------------------------------------------------------

float3 SunGlint(float3 n, float3 V, float F, float roughness)
{
    float3 L = _RGSunDir.xyz;
    float nl = saturate(dot(n, L));
    float3 H = SafeNormalize(L - V, L);   // L = V: looking straight down the sun's own path
    float nh = saturate(dot(n, H));
    float nv = max(dot(n, -V), 0.05);
    float a = max(roughness * roughness, 0.0016);
    float a2 = a * a;
    float d = nh * nh * (a2 - 1.0) + 1.0;
    float D = a2 / (3.14159265 * d * d);
    float spec = min(D * F * 0.25 / nv, 60.0);
    return _RGSunColor.rgb * spec * nl * saturate(_RGSunDir.w) * _RGRefl.z * SunThrough();
}

#endif
