// The city in the map's water, by day and by night (RealGlass/Water/WaterCityReflection.cs has the
// model and its sources; ported from RealLight's CityReflection, 2026-10-03, which checked it against
// the CPU in an editor test). Drawn from a command buffer after the opaque image effects, before
// Bloom, so reflected lights glow as the lights do. Only on visible simulated water: the separate
// surface-depth pass records the water meshes, independently of the terrain behind them. Four passes:
//   0 TRACE (a cube round the camera, into a half-size buffer with mips): the ray reflected by the
//     ripples at the water's point (RippleSlope: the game's own ripple texture, or ours, read as
//     the game reads it; the flat sea's mirror ray with the gain at 0), its
//     slope divided by the Length slider (the reflection drawn longer), followed through the depth
//     texture; where it meets the city, the city's colour (a half-size copy of the frame) in place
//     of the sky the water's cube already shows, x the rough sea's reflectance at the view's angle.
//     rgb = that x a, a = 1 on the water (0 elsewhere).
//   1 STREAKS (a Blit): a Gaussian up and down, the waves too short for the screen. Read from the
//     mip whose texels are half a sigma, 17 taps over +-3 sigma. Weighted by a: only water is
//     averaged (normalised convolution), so the shore does not darken the reflection's edge.
//   2 COMPOSITE (the cube, onto the frame): on the water, the streaked rgb / a. Added (Blend One
//     One); never taking a pixel below black.
//   3 HISTORY: reprojected from the actual water surface, clamped to the current neighbourhood.
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
    // z: the ray cone (bent rays only). w: 1 = the waves DISTORT the flat mirror's picture at the
    // composite; 0 = each pixel's ray is bent by the waves in the trace.
    float4 _RGWaterWave;

    // The trace as drawn (the composite reads its own pixel's distance share there).
    sampler2D _RGWaterTrace;

    // The ripple texture the water is shaded with (ours, or the game's own _WaterNormal).
    sampler2D _RGRipples;

    // The game's own clock (SimulationManager.Update): x = seconds into its minute. It runs with the
    // game's speed and stops while the game is paused, as the game's water does.
    float4 _SimulationTime;

    // rgb: the light the game's water lights its cube with (the sun on the level + the sky's
    // ambient, linear).
    float4 _RGWaterLight;

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
    // cycle (phase = t x 0.4 + 0.2 cos(0.06 x) cos(0.06 z), weight 2 |0.5 - frac(phase)| on the
    // second), each decoded x 2 - 1 (x from .w, y from .y) and halved: the normal's x is world +x, its
    // y world -z, so the slope is (-x, y). Left out: the offset along the flow (the sea's water barely
    // flows), the foam's damping. dx, dy: the step to the next pixel (m), for the texture's mips --
    // ripples shorter than the screen can show average out, as on the game's water.
    float2 RippleSlope(float2 xz, float2 dx, float2 dy)
    {
        float phase = _SimulationTime.x * 0.4 + 0.2 * cos(0.06 * xz.x) * cos(0.06 * xz.y);
        float blend = 2.0 * abs(0.5 - frac(phase));
        float footprint = max(_RGWaterWave.y, 1.0);
        dx *= footprint;
        dy *= footprint;
        float4 a = tex2Dgrad(_RGRipples, xz * 0.0061, dx * 0.0061, dy * 0.0061);
        float4 b = tex2Dgrad(_RGRipples, xz.yx * 0.0057, dx.yx * 0.0057, dy.yx * 0.0057);
        float2 n = lerp(float2(a.w, a.y), float2(b.y, b.w), blend) - 0.5;
        return float2(-n.x, n.y) * _RGWaterWave.x;
    }

    float4 fragTrace(v2f i) : SV_Target
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
        bool distort = _RGWaterWave.w > 0.5;
        float2 slope = float2(0.0, 0.0);
        if (_RGWaterWave.x > 0.0 && !distort)
            slope = FiniteSigned(float3(RippleSlope(xz, xzDx, xzDy), 0.0)).xy;
        // How far the waves turn the reflected ray from one pixel to the next (radians: a tilt turns
        // it twice).
        float turn = 2.0 * max(length(ddx(slope)), length(ddy(slope)));

        if (!water)
            return float4(0.0, 0.0, 0.0, 0.0);

        float rho = RGSeaRho(-d.y);
        float k = rho * _RGWaterGeom.y;
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

        // Water that meets nothing: the cube's sky stays (nothing added), but it is water (distorted:
        // a = 1 + the share of the way that lies past the water, all of it).
        if (hit < 0.0 || k <= 0.0)
            return float4(0.0, 0.0, 0.0, distort ? 2.0 : 1.0);

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
        float fade = saturate(min(min(at.x, 1.0 - at.x), min(at.y, 1.0 - at.y)) / max(_RGWaterMarch.z, 1e-4));
        fade *= 1.0 - smoothstep(0.7 * tMax, tMax, hi);

        // The city read as wide as the rays of one pixel spread by the time they meet it (a ray
        // cone): the waves' turn over the distance travelled, in texels of the frame there.
        float lod = 0.0;
        if (_RGWaterWave.z > 0.0)
        {
            float texelAngle = 2.0 * _RGWaterStreak.y / max(abs(UNITY_MATRIX_P[1][1]), 1e-4);
            float spread = hi * turn / max(at.z * texelAngle, 1e-6);
            lod = 0.5 * log2(1.0 + spread * spread);
        }
        // Above 100% the strength brightens the city only: the sky it takes the place of is the sky the
        // water shows (the flat sea's reflectance, x 1). Scaled with it (until 2026-11-04), a strength of
        // 300% (the night's default) took three times that sky away: dark buildings went below black
        // and, floored per channel, red (a player's screenshots: dusk and night). Below 100% both
        // scale, as before: the reflection fades toward the bare water (the city alone scaled, a dark
        // building's reflection would darken as the slider goes down). The same at 100% either way.
        float strength = _RGWaterGeom.y;
        float3 c = Finite(tex2Dlod(_RGWaterFrame, float4(at.xy, 0.0, lod)).rgb) * strength;
        if (_RGWaterGeom.z > 0.0)
        {
            // Deep water: the sky the layer drew here is that much less, so that much less is replaced.
            float3 sky = _RGWaterGeom.z * RGDeepKeep(rho) * Finite(RGSkyLight(texCUBElod(_RGWaterTheme, float4(mirror, _RGWaterGeom.w)).rgb * _RGWaterLight.rgb, mirror));
            // Never more sky than this water pixel holds, one scale for every channel: the sky layer
            // shades each ripple (its own Fresnel, its tilted rays), this the flat sea's mean, and at a
            // grazing view toward a far shore the two part (the game's fog has covered some of it too).
            // Taken past the pixel, the composite's floor stopped each channel apart: black, red edges.
            float3 base = Finite(tex2Dlod(_RGWaterFrame, float4(uv, 0.0, 0.0)).rgb);
            float3 room = 0.98 * base / max(rho * sky, 1e-6);
            c -= min(strength, 1.0) * saturate(min(min(room.x, room.y), room.z)) * sky;
        }
        if (distort)
        {
            // The flat mirror's picture, premultiplied by a = 1 + the share of the way from the eye to
            // what was met that lies past the water (the composite's parallax); rho at the composite.
            float a = 1.0 + saturate(hi / max(tFlat + hi, 1e-3));
            return float4(FiniteSigned(c * fade) * a, a);
        }
        return float4(FiniteSigned(c * (rho * fade)), 1.0);
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
    sampler2D _RGWaterTraced;
    sampler2D _RGWaterHistory;
    float4x4 _RGWaterPrevVP;
    float4 _RGWaterHistoryInfo;

    float4 fragHistory(v2f i) : SV_Target
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;
        float4 c = tex2Dlod(_RGWaterTraced, float4(uv, 0.0, 0.0));
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
                float4 s = tex2Dlod(_RGWaterTraced, float4(uv + float2(x * _RGWaterHistoryInfo.y, y * _RGWaterHistoryInfo.z), 0.0, 0.0));
                lo = min(lo, s);
                hi = max(hi, s);
            }
        }
        float4 history = tex2Dlod(_RGWaterHistory, float4(prev, 0.0, 0.0));
        if (history.a <= 0.5)
            return c;
        history.rgb = clamp(FiniteSigned(history.rgb), lo.rgb, hi.rgb);
        return float4(FiniteSigned(lerp(c.rgb, history.rgb, _RGWaterHistoryInfo.x)), c.a);
    }

    float4 fragComposite(v2f i) : SV_Target
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;

        // DISTORTED (w = 1; Sousa 2005, GPU Gems 2 ch. 19: the reflection's picture read where the
        // waves' normal sends it). The ray the waves reflect, mirrored below the level, is the view
        // whose flat mirror ray it is: what it meets far away is on the screen there (uvFar); what it
        // meets at the water's edge is here (uv). Between them by the share of the way past the water.
        // Before any branch: the derivatives need every pixel of the quad. The composite is drawn at
        // the full size, the footprint counted in the trace's (half-size) pixels.
        float tFlat;
        bool water = Water(d, uv, tFlat);
        float2 xz = _WorldSpaceCameraPos.xz + d.xz * tFlat;
        float3 dDx = ddx(d), dDy = ddy(d);
        float2 xzDx = 2.0 * tFlat * (dDx.xz - d.xz * dDx.y / min(d.y, -1e-4));
        float2 xzDy = 2.0 * tFlat * (dDy.xz - d.xz * dDy.y / min(d.y, -1e-4));
        bool distort = _RGWaterWave.w > 0.5;
        float2 slope = float2(0.0, 0.0);
        if (_RGWaterWave.x > 0.0 && distort)
            slope = FiniteSigned(float3(RippleSlope(xz, xzDx, xzDy), 0.0)).xy;
        float3 bent = reflect(d, normalize(float3(-slope.x, 1.0, -slope.y)));
        float3 seen = float3(bent.x, -abs(bent.y), bent.z);
        float2 uvFar = Project(_WorldSpaceCameraPos + seen * 1000.0).xy;
        float2 uvDx = ddx(uv), uvDy = ddy(uv), farDx = ddx(uvFar), farDy = ddy(uvFar);

        if (!water)
            return float4(0.0, 0.0, 0.0, 0.0);

        float4 c;
        float gain = 1.0;
        if (distort)
        {
            float2 texels = max(_RGWaterStreak.zw, 1.0);
            float own = tex2Dlod(_RGWaterTrace, float4((floor(uv * texels) + 0.5) / texels, 0.0, 0.0)).a;
            float past = saturate(own - 1.0);
            float2 at = Project(_WorldSpaceCameraPos + (d * (1.0 - past) + seen * past) * 1000.0).xy;
            // Read as wide as the neighbouring pixels' reads spread (the rays' cone, as the GPU picks
            // a texture's level), so the picture's edges soften where the waves scatter it.
            c = tex2Dgrad(_RGWaterStreaked, at, lerp(uvDx, farDx, past), lerp(uvDy, farDy, past));
            // Off the screen there is no picture: faded out over 2% of it.
            // The strength is in the trace's colour already (the city only, not the sky it replaces).
            gain = RGSeaRho(-d.y) * saturate(1.0 + min(min(at.x, 1.0 - at.x), min(at.y, 1.0 - at.y)) / 0.02);
        }
        else
            c = tex2Dlod(_RGWaterStreaked, float4(uv, 0.0, 0.0));
        if (c.a <= 1e-3)
            return float4(0.0, 0.0, 0.0, 0.0);
        float3 added = FiniteSigned(c.rgb / c.a) * gain;

        // Never below black (by day a dark building over a bright sky takes light away).
        float3 base = Finite(tex2Dlod(_RGWaterFrame, float4(uv, 0.0, 0.0)).rgb);
        return float4(max(added, -0.98 * base), 0.0);
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
    }
}
