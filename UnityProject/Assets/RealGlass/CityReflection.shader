// The city in the map's water, by day and by night (RealGlass/Water/WaterCityReflection.cs has the
// model and its sources; ported from RealLight's CityReflection, 2026-10-03, which checked it against
// the CPU in an editor test). Two command buffers (since the camera-pan fix: one frame, no lag): BEFORE
// THE WATER, at the final pass, the rays and the share of each texel the city covers (passes 0, 4, 1),
// which the sky layer reads in the same render to leave out the covered sky; AFTER THE OPAQUE IMAGE
// EFFECTS (the fog), before Bloom, the city's light at those rays' hits (5, 3, 1) and onto the water (2),
// so reflected lights glow as the lights do. Only on visible simulated water: the separate
// surface-depth pass records the water meshes, independently of the terrain behind them. Six passes:
//   0 TRACE (a cube round the camera, into a half-size float buffer): the ray reflected by the
//     ripples at the water's point (RippleSlope: the game's own ripple texture, or ours, read as
//     the game reads it; the flat sea's mirror ray with the gain at 0), its
//     slope divided by the Length slider (the reflection drawn longer), followed through the depth
//     texture; where it meets the city, the hit's place on the screen and its fade, with a = 1 on the
//     water (0 elsewhere) and how much of the texel the city covers (PackWater), for the sky it takes
//     the place of.
//   1 STREAKS (a Blit): a Gaussian up and down, the waves too short for the screen. Read from the
//     mip whose texels are half a sigma, 17 taps over +-3 sigma. Weighted by a: only water is
//     averaged (normalised convolution), so the shore does not darken the reflection's edge.
//   2 COMPOSITE (the cube, onto the frame): on the water, the streaked rgb / a, added (Blend One
//     One); where the sky layer does not draw, after the sky the city covers has left the pixel as a
//     share of the pixel itself (never as a colour: no orange under dark trees), at most 98% of it.
//   3 HISTORY: reprojected from the actual water surface, clamped to the current neighbourhood, its
//     weight falling with the water point's motion on the screen.
//   4 THE SAME for the share covered, which then goes through pass 1 as the light does.
//   5 THE LIGHT: the city's colour (a half-size copy of the frame after the fog) at each trace texel's
//     hit, x the rough sea's reflectance at the view's angle and the strength.
Shader "RealGlass/CityReflection"
{
    // Blit binds its source as the material's main texture, which needs the property declared
    // (without it the streak pass reads Unity's grey stand-in: RealLight's editor test).
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    #include "WaterCommon.cginc"
    #include "FullScreen.cginc"   // vert_full / v2f_full: vert_img with a float uv (Apple GPUs honour half)

    // 96 steps over 1 m .. the reach (the far plane's 0.9, ~20 km in the author's city) grow 11% each
    // (64 over 8 km, 15%, until 2026-10-08: the author found the night reflection's reach short). At 32
    // (34%) a step near a roof's edge jumped from in front of the facade to over and behind it, and the
    // reflection's top was eaten (RealLight's editor test, a 60 m wall).
    #define RG_MARCH 96
    #define RG_REFINE 6
    #define RG_STREAK 8

    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    // Full-resolution eye depth from the game's water meshes; zero means no visible water.
    sampler2D _RGWaterSurfaceDepth;

    // The frame as the game drew it, just before (a half-size copy): what the rays meet.
    sampler2D _RGWaterFrame;

    // The traced reflection after the streaks (half size).
    sampler2D _RGWaterStreaked;
    sampler2D _RGWaterStreaked2;   // the share of each texel the city covers, streaked as the light (pass 4, then 1)

    // The streaks' source (Blit).
    sampler2D _MainTex;

    // The theme's cube: the sky the water shows where no city is met.
    samplerCUBE _RGWaterTheme;

    // x: the water's level (world y); y: the city reflection's strength; z: the share of the theme's
    // sky the water's cube shows (the Sky slider while ours is on the water, 0 otherwise); w: the
    // theme cube's mip to read it at.
    float4 _RGWaterGeom;

    // x: how far a ray is followed (m); y: the least thickness of a surface (m); z: the screen's
    // edge fade (share); w: the reflection's length (1 = a mirror: WaterOptics.MirrorSlope; below 1,
    // unset included, read as 1).
    float4 _RGWaterMarch;

    // x: the streaks' sigma up and down (uv); y: a texel's height in the half-size buffers (uv; a
    // command buffer's Blit left _MainTex_TexelSize at 0 in RealLight's editor test); zw: the trace
    // buffer's size, texels.
    float4 _RGWaterStreak;

    // x: the gain on the ripples' slopes (WaterOptics.CityRippleGain; 0: the flat sea's mirror). y: the
    // footprint the ripples are read over, in pixels of the trace (below 1, unset included, read as 1).
    // (z, the ray cone, and w, the "distort" mode: gone with the camera-pan fix; the mod never set them.)
    float4 _RGWaterWave;

    // The ripple texture the water is shaded with (ours, or the game's own _WaterNormal).
    sampler2D _RGRipples;

    // The game's own clock (SimulationManager.Update): x = seconds into its minute. It runs with the
    // game's speed and stops while the game is paused, as the game's water does.
    float4 _SimulationTime;

    // rgb: the light the game's water lights its cube with (the sun on the level + the sky's
    // ambient, linear).
    float4 _RGWaterLight;

    // THE ONE MIRROR (the author's call 2026-11-21: the water's reflection with nothing subtracted;
    // WaterSky.shader's uniform of the same name). x: 0 -- the composite takes the sky out as an estimate
    // (our cube on the water); 1 -- the sky layer has left out the sky this pass stands in for, by THIS
    // frame's coverage (the camera-pan fix; until then last frame's, reprojected): the composite only adds.
    float4 _RGWaterOneMirror;

    // The facade cards (FacadeCards.cginc, a preview round): pass 0 compiled with RG_FACADE_CARDS walks
    // the city from above where the screen has nothing for a ray, and shows the card of the building
    // it meets. Without the keyword, every pass as before.
    #if defined(RG_FACADE_CARDS)
    #include "FacadeCards.cginc"
    #endif

    struct v2f
    {
        float4 pos : SV_POSITION;
        float3 dir : TEXCOORD0;
        float4 screen : TEXCOORD1;
    };

    v2f vert(float4 vertex : POSITION)
    {
        v2f o;
        float3 world = _WorldSpaceCameraPos + vertex.xyz * 500.0;
        o.pos = mul(UNITY_MATRIX_VP, float4(world, 1.0));
        #if defined(UNITY_REVERSED_Z)
        o.pos.z = 1.0e-6 * o.pos.w;
        #else
        o.pos.z = 0.999999 * o.pos.w;
        #endif
        o.dir = vertex.xyz;
        o.screen = ComputeScreenPos(o.pos);
        return o;
    }

    // A world point on the screen: uv, and its eye depth (clip w); w <= the near plane: behind.
    float3 Project(float3 q)
    {
        float4 clip = mul(UNITY_MATRIX_VP, float4(q, 1.0));
        float4 screen = ComputeScreenPos(clip);
        return float3(screen.xy / max(clip.w, 1e-6), clip.w);
    }

    bool OnScreen(float3 s)
    {
        return s.z > _ProjectionParams.y && s.x >= 0.0 && s.x <= 1.0 && s.y >= 0.0 && s.y <= 1.0;
    }

    // How far the world point q (on the screen at s) lies behind what the depth texture shows there
    // (m; negative: in front). -1e9 where that is the sky, or the water itself or its bed (at or
    // below the level: at a low angle the next pixel's water lies metres nearer, and a ray just
    // leaving the water would meet it).
    float Behind(float3 s, float3 q, float surfaceY)
    {
        float raw = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(s.xy, 0.0, 0.0));
        if (Linear01Depth(raw) > 0.9999)
            return -1e9;
        float eye = LinearEyeDepth(raw);
        float water = tex2Dlod(_RGWaterSurfaceDepth, float4(s.xy, 0.0, 0.0)).r;
        if (water > 0.0 && eye >= water - 0.5)
            return -1e9;
        // The scene height along the sample ray, relative to this water point's local level.
        // A distance-scaled height cutoff used to remove low buildings far across the water.
        float sceneY = _WorldSpaceCameraPos.y + (q.y - _WorldSpaceCameraPos.y) * eye / s.z;
        if (sceneY <= surfaceY + 0.5)
            return -1e9;
        return s.z - eye;
    }

    // Reconstruct the surface, not the terrain behind it. Point sampling preserves dry banks and
    // foreground silhouettes at the full-resolution composite as well as the half-size trace.
    bool Water(float3 d, float2 uv, out float tSurface)
    {
        float eye = tex2Dlod(_RGWaterSurfaceDepth, float4(uv, 0.0, 0.0)).r;
        float3 forward = -normalize(UNITY_MATRIX_V[2].xyz);
        tSurface = eye / max(dot(d, forward), 1e-3);
        return eye > 0.0 && d.y < -1e-4;
    }

    // The water's slope (dh/dx, dh/dz) at the world point xz, from its ripples as the game shades
    // them (Custom/Water/Default, disassembled 2026-10-05): the ripple texture read twice -- at
    // world xz x 0.0061, and at (z, x) x 0.0057 with its channels swapped -- crossfaded over a 2.5 s
    // cycle (phase = t x 0.4 + 0.2 sin(0.06 x) sin(0.06 z); cos x cos until 2026-11-20, weight 2 |0.5 - frac(phase)| on the
    // second), each decoded x 2 - 1 (x from .w, y from .y) and halved: the normal's x is world +x, its
    // y world -z, so the slope is (-x, y). Left out: the offset along the flow (the sea's water barely
    // flows), the foam's damping. dx, dy: the step to the next pixel (m), for the texture's mips --
    // ripples shorter than the screen can show average out, as on the game's water.
    float2 RippleSlope(float2 xz, float2 dx, float2 dy)
    {
        float phase = _SimulationTime.x * 0.4 + 0.2 * sin(0.06 * xz.x) * sin(0.06 * xz.y);
        float blend = 2.0 * abs(0.5 - frac(phase));
        float footprint = max(_RGWaterWave.y, 1.0);
        dx *= footprint;
        dy *= footprint;
        float4 a = tex2Dgrad(_RGRipples, xz * 0.0061, dx * 0.0061, dy * 0.0061);
        float4 b = tex2Dgrad(_RGRipples, xz.yx * 0.0057, dx.yx * 0.0057, dy.yx * 0.0057);
        float2 n = lerp(float2(a.w, a.y), float2(b.y, b.w), blend) - 0.5;
        return float2(-n.x, n.y) * _RGWaterWave.x;
    }

    // The sky the city's reflection takes the place of, before x rho (the trace's units), at the flat
    // sea's mirror ray. Above 100% the strength brightens the city only: the sky taken is the sky the
    // water shows (until 2026-11-04 it scaled that too and dark buildings went black with red edges at
    // 300%).
    // - The share: the sky layer's alpha is the share x each ripple's reflectance and never above 1
    //   (WaterSky.shader), so the water shows at most 1 / rho of the flat sea's sky here. Uncapped, a
    //   share of 300% took up to three times the sky the water showed (a player's report, 2026-11-13).
    // - Until 2.0.0 the estimate's COLOUR was taken from the pixel (cut by one scale to what the pixel
    //   held, and above 100% the rest in the colour of what was left: 2026-11-13).
    //
    // SINCE 2.0.0 NO COLOUR IS TAKEN FROM THE PIXEL (the author's screenshots: orange under dark trees
    // at 100% too). The sky here is the flat sea's MEAN: each ripple of the frame shows its own part
    // of the sky (paler toward the horizon, the sun's glitter on it, the fog over it), and a mean blue
    // taken from a paler pixel leaves orange, at any share. A picture of "the city's light less the
    // sky" cannot be put right afterwards either: once the two are one colour, the part that is sky
    // taken away cannot be told from the part that is a wall's light (tried: dark trees came out right,
    // brown walls at 300% did not). So the two travel apart:
    // - the trace's rgb is the city's light alone;
    // - beside it, how much of each water texel the city COVERS (the picture's fade at the screen's
    //   edges and far out; the cards' weight), through a history and the streaks of its own (passes 4
    //   and 1) exactly as the light goes;
    // - the composite takes the sky's light out of the pixel AS A SHARE OF THE PIXEL (covered x the
    //   estimate's brightness over the pixel's, at most 98%), then adds the city's light: the water
    //   left keeps the colour the frame gave it, only darker, whatever the estimate missed.
    float3 SkyEstimate(float3 mirror)
    {
        return Finite(RGSkyLight(texCUBElod(_RGWaterTheme, float4(mirror, _RGWaterGeom.w)).rgb * _RGWaterLight.rgb, mirror));
    }

    // THE TRACE'S ALPHA holds two things: a, the water's mark and the share of the way past it (1..2,
    // as it always did), and the share of the texel the city covers, in 15 steps: a + 2 x (15 - steps).
    // A texel covered whole holds a itself, to the bit; one the city misses, a + 30. Read back texel by
    // texel only (the two history passes, the composite's own read), never filtered: the buffers after
    // the history hold a alone.
    float PackWater(float a, float covered)
    {
        return a + 2.0 * (15.0 - floor(saturate(covered) * 15.0 + 0.5));
    }

    // x: a (0 off the water); y: the share covered.
    float2 UnpackWater(float packed)
    {
        // (Never below 0: a texture that is no trace -- unbound, a plain grey -- reads as its own a.)
        float steps = max(floor((packed - 1.0) * 0.5 + 1e-3), 0.0);
        float2 water = float2(packed - 2.0 * steps, saturate((15.0 - steps) / 15.0));
        return packed < 0.5 ? float2(0.0, 0.0) : water;
    }

    // 0 THE TRACE (the camera-pan fix: run BEFORE the water, at the final pass, so the sky layer leaves out THIS frame's
    // share of the sky the city covers -- until then it read last frame's, reprojected, and every tower's reflection
    // edge slid by a frame while the camera moved). Only geometry: the rays and where they meet the city, not its
    // light -- the frame here has no glass yet (the mod's glass draws after the water) and no fog, so the light is
    // fetched after the fog at the same hit points (pass 5). Out (two targets): 0 = (the hit's screen uv, its fade
    // (the screen's edge and far away, continuous), PackWater(1, the share covered)); 1 = the facade cards' light x
    // their weight (only with RG_FACADE_CARDS; else 0). Not water: all 0.
    struct TraceOut
    {
        float4 hit : SV_Target0;
        float4 cards : SV_Target1;
    };

    TraceOut MakeTrace(float2 hitUv, float screenFade, float covered, float3 cards)
    {
        TraceOut o;
        o.hit = float4(hitUv, screenFade, PackWater(1.0, covered));
        o.cards = float4(Finite(cards), 0.0);
        return o;
    }

    TraceOut fragTrace(v2f i)
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;

        float tFlat;
        bool water = Water(d, uv, tFlat);
        float3 p = _WorldSpaceCameraPos + d * tFlat;
        // Use the local surface plane for derivatives: a dry neighbour's zero depth must not create
        // a huge ripple footprint at a shoreline.
        float2 xz = p.xz;
        float3 dDx = ddx(d), dDy = ddy(d);
        float2 xzDx = tFlat * (dDx.xz - d.xz * dDx.y / min(d.y, -1e-4));
        float2 xzDy = tFlat * (dDy.xz - d.xz * dDy.y / min(d.y, -1e-4));
        float2 slope = float2(0.0, 0.0);
        if (_RGWaterWave.x > 0.0)
            slope = FiniteSigned(float3(RippleSlope(xz, xzDx, xzDy), 0.0)).xy;

        TraceOut none;
        none.hit = float4(0.0, 0.0, 0.0, 0.0);
        none.cards = float4(0.0, 0.0, 0.0, 0.0);
        if (!water)
            return none;

        // The ray the waves reflect (a ray turned below the level meets the next wave: mirrored back
        // up), and the one followed: its slope divided by the length (the reflection drawn that many
        // times as long). The sky the city replaces is the flat sea's mirror: the water's cube.
        float3 mirror = float3(d.x, -d.y, d.z);
        float3 bent = mirror;
        if (_RGWaterWave.x > 0.0)
            bent = reflect(d, normalize(float3(-slope.x, 1.0, -slope.y)));
        float3 r = normalize(float3(bent.x, abs(bent.y) / max(_RGWaterMarch.w, 1.0), bent.z));

        // The mirror ray through the depth texture: geometric steps, then bisection.
        float tMax = _RGWaterMarch.x;
        float t0 = max(1.0, tFlat * 0.002);
        float grow = pow(tMax / t0, 1.0 / (RG_MARCH - 1));
        float tPrev = 0.0;
        float t = t0;
        float hit = -1.0;
        [loop]
        for (int s = 0; s < RG_MARCH; s++)
        {
            float3 wq = p + r * t;
            float3 q = Project(wq);
            if (!OnScreen(q))
                break;
            float behind = Behind(q, wq, p.y);
            if (behind > 0.0 && behind < max(_RGWaterMarch.y, (t - tPrev) * 1.5))
            {
                hit = t;
                break;
            }
            tPrev = t;
            t *= grow;
        }

        // The screen's answer: the crossing refined by bisection, faded at the screen's edge and far away.
        float screenFade = 0.0;
        float2 hitUv = float2(0.0, 0.0);
        if (hit >= 0.0)
        {
            float lo = tPrev, hi = hit;
            [loop]
            for (int b = 0; b < RG_REFINE; b++)
            {
                float mid = 0.5 * (lo + hi);
                float3 wm = p + r * mid;
                if (Behind(Project(wm), wm, p.y) > 0.0)
                    hi = mid;
                else
                    lo = mid;
            }
            float3 at = Project(p + r * hi);
            screenFade = saturate(min(min(at.x, 1.0 - at.x), min(at.y, 1.0 - at.y)) / max(_RGWaterMarch.z, 1e-4));
            screenFade *= 1.0 - smoothstep(0.7 * tMax, tMax, hi);
            hitUv = at.xy;
        }

#if defined(RG_FACADE_CARDS)
        // With the cards: for the rest of the weight -- a ray that left the screen, ran out of steps, or met the
        // city inside the edge's fade -- the city walked from above from the water's point along the same ray,
        // and the card of the building met, lit as the frame. Blended with the screen's, never switched.
        float3 cardLight = float3(0.0, 0.0, 0.0);
        float cardWeight = 0.0, cardMet = 0.0;
        if (screenFade < 0.999 && _RGCityMapST.w > 0.5)
        {
            // Into the map's square first (a water point outside it starts the walk at its edge).
            float2 mapLo = _RGCityMapST.xy, mapHi = mapLo + _RGCityMapInfo.x * _RGCityMapST.z;
            float2 rxz = float2(abs(r.x) > 1e-6 ? r.x : 1e-6, abs(r.z) > 1e-6 ? r.z : 1e-6);
            float2 ta = (mapLo - p.xz) / rxz, tb = (mapHi - p.xz) / rxz;
            float tIn = max(max(min(ta.x, tb.x), min(ta.y, tb.y)), 0.0);
            float tOut = min(max(ta.x, tb.x), max(ta.y, tb.y));
            if (tIn < tOut && tIn < tMax)
            {
                float3 start = p + r * (tIn + 0.01);
                float tC, idC;
                float3 nC;
                if (CityHit(start, r, 0.0, p.y - 1.0, tC, idC, nC))
                {
                    // The footprint: a texel of the trace at the water, spread along the way.
                    float texelAngle = 2.0 * _RGWaterStreak.y / max(abs(UNITY_MATRIX_P[1][1]), 1e-4);
                    CardSample card;
                    CardLookup(start, r, tC, idC, texelAngle, texelAngle * tFlat, card);
                    float tCard = tIn + 0.01 + card.t;
                    if (card.found > 0.5 && tCard < tMax)
                    {
                        // Below the horizon the card's glass mirrors the frame's average (the mod's own ground
                        // fill is the glass's): last frame's copy here (this frame's is taken after the fog).
                        float3 below = Finite(tex2Dlod(_RGWaterFrame, float4(0.5, 0.5, 0.0, 12.0)).rgb);
                        float w = (1.0 - screenFade) * (1.0 - smoothstep(0.7 * tMax, tMax, tCard));
                        cardLight = CardRadiance(card, r, idC, 0.05, below) * CardGain(card.n, card.glass) * w;
                        cardWeight = w;
                        cardMet = 1.0;
                    }
                }
            }
        }
        // The preview's check (_RGCardMode.z): green where a card answered, red where the screen did (in the
        // cards' light; the screen's own share 0).
        if (_RGCardMode.z > 0.5)
            return MakeTrace(float2(0.0, 0.0), 0.0, 1.0, cardMet > 0.5 ? float3(0.0, 1.0, 0.0) : (hit >= 0.0 ? float3(1.0, 0.0, 0.0) : float3(0.0, 0.0, 0.0)));
        return MakeTrace(hitUv, screenFade, saturate(screenFade + cardWeight), cardLight);
#else
        return MakeTrace(hitUv, screenFade, screenFade, float3(0.0, 0.0, 0.0));
#endif
    }

    // 5 THE LIGHT (the camera-pan fix; after the fog, so the reflected city is the city as drawn -- its glass, its
    // fog): each trace texel's city light fetched at its hit point in this frame's copy (_RGWaterFrame), x its fade,
    // + the cards' light, x the strength x the rough sea's reflectance at the view's angle -- the trace's old rgb,
    // from the same rays and the same frame as the coverage the sky layer used (_RGWaterTraced, _RGWaterTracedCards).
    // Out: the light (premultiplied by a) and the trace's packed alpha, as the history pass reads them.
    sampler2D _RGWaterTraced;
    sampler2D _RGWaterTracedCards;

    float4 fragGather(v2f i) : SV_Target
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;
        float4 t = tex2Dlod(_RGWaterTraced, float4(uv, 0.0, 0.0));
        float2 own = UnpackWater(t.a);
        if (own.x <= 0.5)
            return float4(0.0, 0.0, 0.0, t.a);
        float3 cards = tex2Dlod(_RGWaterTracedCards, float4(uv, 0.0, 0.0)).rgb;
        float3 screenLight = t.b > 0.0 ? Finite(tex2Dlod(_RGWaterFrame, float4(t.rg, 0.0, 0.0)).rgb) * t.b : float3(0.0, 0.0, 0.0);
        float3 light = (screenLight + Finite(cards)) * (_RGWaterGeom.y * RGSeaRho(-d.y));
        return float4(Finite(light), t.a);
    }

    // The Gaussian up and down: 2 x RG_STREAK + 1 taps over +-3 sigma, from the mip whose texels are
    // half a sigma.
    float4 fragStreaks(v2f_full i) : SV_Target
    {
        float sigma = _RGWaterStreak.x;
        // Under a third of a texel the taps fall on one texel: sharp.
        float texels = max(sigma / max(_RGWaterStreak.y, 1e-6), 1e-4);
        float lod = max(0.0, log2(texels / 2.0));
        float2 step = float2(0.0, texels < 0.3 ? 0.0 : 3.0 * sigma / RG_STREAK);
        float4 sum = float4(0.0, 0.0, 0.0, 0.0);
        float weights = 0.0;
        [unroll]
        for (int k = -RG_STREAK; k <= RG_STREAK; k++)
        {
            float x = 3.0 * k / RG_STREAK;
            float w = exp(-0.5 * x * x);
            sum += w * tex2Dlod(_MainTex, float4(i.uv + step * k, 0.0, lod));
            weights += w;
        }
        return sum / weights;
    }

    // 3 HISTORY (against flicker, 2026-10-06: the trace's hit-or-miss edges crawl as the waves move):
    // this frame's trace blended with last frame's result where this water point was then (the flat
    // sea's point through last frame's view-projection, OpenGL convention as ComputeScreenPos), the
    // history clamped to the box of this frame's 3 x 3 neighbourhood (Karis 2014, "High-Quality Temporal
    // Supersampling"; Pedersen 2016). _RGWaterHistoryInfo: x the history's weight (0: none yet), y 1 /
    // the buffers' width, z 1 / height.
    // 4 THE SAME for the share of each texel the city covers (2.0.0): its own history, the light's
    // way. Both read texel by texel and write a alone in their alpha (UnpackWater). Since the camera-pan fix
    // pass 4 runs before the water, on the trace (_RGWaterTraced), and pass 3 after the fog, on the light
    // gathered at the trace's hits (_RGWaterGathered); _RGWaterPrevVP is the render before this one's (taken at
    // each render's start, not in a LateUpdate a frame apart from what was drawn).
    sampler2D _RGWaterGathered;
    sampler2D _RGWaterHistory;
    sampler2D _RGWaterHistory2;
    float4x4 _RGWaterPrevVP;
    float4 _RGWaterHistoryInfo;

    // The texel as a history pass blends it: the city's light (rgb, premultiplied by a), or the share
    // covered (r, premultiplied the same); a in the alpha.
    float4 Traced(float2 uv, bool covered)
    {
        float4 t = covered ? tex2Dlod(_RGWaterTraced, float4(uv, 0.0, 0.0)) : tex2Dlod(_RGWaterGathered, float4(uv, 0.0, 0.0));
        float2 own = UnpackWater(t.a);
        return covered ? float4(own.y * own.x, 0.0, 0.0, own.x) : float4(t.rgb, own.x);
    }

    float4 History(v2f i, bool covered)
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;
        float4 c = Traced(uv, covered);
        float tSurface;
#if defined(UNITY_COMPILER_HLSLCC)
        // No "not" here: Unity 5.6's HLSLcc (OpenGL Core, Metal) writes the "not" of a combined
        // condition as ~int(b) * int(0xffffffffu), never false -- this pass never ran there, from
        // 1.0.0 to 2026-10-22. Direct3D 11 keeps its own line, byte for byte.
        bool water = Water(d, uv, tSurface);
        if ((water ? c.a : 0.0) <= 0.5 || _RGWaterHistoryInfo.x <= 0.0)
#else
        if (c.a <= 0.5 || _RGWaterHistoryInfo.x <= 0.0 || !Water(d, uv, tSurface))
#endif
            return c;

        float3 p = _WorldSpaceCameraPos + d * tSurface;
        float4 clip = mul(_RGWaterPrevVP, float4(p, 1.0));
        if (clip.w <= 1e-4)
            return c;
        float2 prev = clip.xy / clip.w * 0.5 + 0.5;
        if (prev.x < 0.0 || prev.x > 1.0 || prev.y < 0.0 || prev.y > 1.0)
            return c;

        float4 lo = c, hi = c;
        [unroll]
        for (int y = -1; y <= 1; y++)
        {
            [unroll]
            for (int x = -1; x <= 1; x++)
            {
                float4 s = Traced(uv + float2(x * _RGWaterHistoryInfo.y, y * _RGWaterHistoryInfo.z), covered);
                lo = min(lo, s);
                hi = max(hi, s);
            }
        }
        float4 history = covered ? tex2Dlod(_RGWaterHistory2, float4(prev, 0.0, 0.0)) : tex2Dlod(_RGWaterHistory, float4(prev, 0.0, 0.0));
        if (history.a <= 0.5)
            return c;
        history.rgb = clamp(FiniteSigned(history.rgb), lo.rgb, hi.rgb);
        // THE WEIGHT FALLS WITH THE MOTION (the camera-pan fix: the reflections' edges trailed ~3 frames while the camera
        // moved). How far this water point moved on screen since the render before, in the buffers' texels; the
        // history kept whole up to half a texel (the camera's TAA jitter: at most a pixel of the full frame, so the
        // still flicker stays today's), none from 2 texels on, linear between -- the trust ramp of Pedersen 2016
        // (INSIDE's TemporalReprojection.shader: full to 2 px, none from 15 px), here on the history's weight
        // itself, as velocity-weighted feedback (Karis 2014). Moving, the reflection is this frame's own.
        float motion = length((uv - prev) / max(_RGWaterHistoryInfo.yz, 1e-6));
        float weight = _RGWaterHistoryInfo.x * (1.0 - saturate((motion - 0.5) / 1.5));
        return float4(FiniteSigned(lerp(c.rgb, history.rgb, weight)), c.a);
    }

    float4 fragHistory(v2f i) : SV_Target
    {
        return History(i, false);
    }

    float4 fragCovered(v2f i) : SV_Target
    {
        return History(i, true);
    }

    float4 fragComposite(v2f i) : SV_Target
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;
        float tFlat;
        bool water = Water(d, uv, tFlat);
        if (!water)
            return float4(0.0, 0.0, 0.0, 0.0);

        // c: the city's light (rgb, premultiplied by a) and a; k: the share covered, premultiplied the
        // same (r), from the streaks of its own. (The Waves row's "distort" mode, which read the picture
        // where the waves sent it here, never ran in the mod: gone with the camera-pan fix.)
        float4 c = tex2Dlod(_RGWaterStreaked, float4(uv, 0.0, 0.0));
        float k = tex2Dlod(_RGWaterStreaked2, float4(uv, 0.0, 0.0)).r;
        if (c.a <= 1e-3)
            return float4(0.0, 0.0, 0.0, 0.0);
        float3 added = Finite(c.rgb / c.a);

        // THE SKY LAYER LEFT OUT THE SKY (the camera-pan fix; _RGWaterOneMirror.x = 1): WaterSky.shader drew the
        // sky only where THIS frame's coverage (the pass before the water) leaves it; here only the city's light
        // goes in -- the same rays, the same frame, nothing taken away. (Until then the one mirror read last
        // frame's coverage and light, reprojected: a frame of error at every reflection's edge while the camera
        // moved.)
        if (_RGWaterOneMirror.x > 0.5)
            return float4(max(added, 0.0), 0.0);

        // THE SKY THE CITY TAKES THE PLACE OF, where the sky layer does not draw (our cube on the water;
        // 2.0.0, SkyEstimate has the why). How much light: the share of this pixel the city covers x the
        // sky layer's share (at most 1 / rho: its alpha stops at 1) x the flat sea's sky x rho -- the
        // strength up to 100% (below it the reflection fades toward the bare water; above it the city alone
        // brightens). Taken as a SHARE OF THE PIXEL, at most 98% of it: no colour of the estimate's ever
        // reaches the frame, so the water left is the frame's own, darker.
        float rho = RGSeaRho(-d.y);
        float3 base = Finite(tex2Dlod(_RGWaterFrame, float4(uv, 0.0, 0.0)).rgb);
        if (_RGWaterGeom.z > 0.0)
        {
            const float3 lum = float3(0.2126, 0.7152, 0.0722);
            float covered = saturate(k / c.a);
            float share = min(_RGWaterGeom.z, 1.0 / max(rho, 1e-4));
            float taken = covered * min(_RGWaterGeom.y, 1.0) * share * rho * dot(SkyEstimate(float3(d.x, -d.y, d.z)), lum);
            added -= min(taken / max(dot(base, lum), 1e-6), 0.98) * base;
        }

        return float4(added, 0.0);
    }
    ENDCG

    SubShader
    {
        // 0: trace, into the half-size buffer.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragTrace
            #pragma target 3.5
            #pragma multi_compile __ RG_FACADE_CARDS
            ENDCG
        }

        // 1: the streaks, up and down.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert_full
            #pragma fragment fragStreaks
            #pragma target 3.5
            ENDCG
        }

        // 2: onto the water.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One One, Zero One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragComposite
            #pragma target 3.5
            ENDCG
        }

        // 3: the history, into the accumulated buffer.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragHistory
            #pragma target 3.5
            ENDCG
        }

        // 4: the history of the share covered, into its own accumulated buffer.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragCovered
            #pragma target 3.5
            ENDCG
        }

        // 5: the city's light at the trace's hits (after the fog), into the gathered buffer.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragGather
            #pragma target 3.5
            ENDCG
        }
    }
}
