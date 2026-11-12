// Real Glass: glass or water on a prop the player chose (1.1.9, the prop window), drawn a second time
// over the game's own draw of it (Props/PropGlassDraw.cs), in two passes:
//
// 1. THROUGH (Blend DstColor Zero): what is seen through the surface -- the prop as the game drew it,
//    or, for a see-through prop, the world behind its sheet -- times what the surface lets through:
//    its clarity, its color (scaled to luminance 1, as the buildings' glass: clarity means what the
//    data sheets mean) and 1 - its reflectance.
// 2. REFLECTED (Blend One One): what the surface reflects, added: the screen where the trace finds it
//    (McGuire & Mara's DDA or the Hi-Z walk, GlassCommon), else a building of the city walk, the ground
//    below the horizon or the sky -- as the vehicles' glass does -- and the sun's glint. Its color:
//    4% untinted, the rest of the reflectance through the tint twice (EN 410 / ISO 9050, the buildings'
//    coat).
// Both times the prop's strength: 0 leaves the game's prop as it was.
//
// GLASS (_RGPropMode 1): the reflectance head-on from the prop's Reflectivity, to a mirror edge-on
// (Schlick 1994), over all of the prop -- or, Shininess needed above 0, over its parts at least that
// shiny (1 - _XYSMap.z, the game's glass channel; on a see-through prop the borrowed vehicle shader
// reads the same channel as its opacity). WATER (_RGPropMode 2): the same over the faces that look up,
// the normal rippled by the water's own ripple texture (the game's, or the Water tab's moving one)
// read as the game's water shader decodes it (.w and .y x 2 - 1), twice, drifting apart (Vlachos 2010).
//
// Drawn with GPU instancing (one draw per prop model in view) or one by one; queue Geometry+499 like
// the buildings' glass for an opaque prop, the transparent queue + 1 for a see-through one (the
// material's queue, set by PropGlassDraw).
//
// A prop's mesh where it stands, only: decals (a box the game projects onto what lies in it) and the
// props the game bends over the ground or the water get none of this (PropGlassDraw.CanDrawShape).
//
// REFLECTIVE (_RGPropMode 3, round 3): a finish ON the prop, nothing seen through it -- a gloss coat
// or a bare metal (PropFinishes: measured reflectance head-on per channel, Hoffman 2013). The prop as
// the game drew it times 1 - F (what reflects is not diffused), then the reflection times F, F per
// channel (Schlick from the finish's color x its reflectivity). Polish sets the roughness the
// reflection is blurred with (glass: Frosted, the same); _RGPropLook.y the sun's glint.
//
// WATER's own color (round 3): what clarity does not let through is the water's own color lit by the
// sun and the sky (its albedo: the Water tab's color unless it has its own, x the tab's brightness),
// and its reflection x its Strength (_RGPropLook.w, 1 = real still water).
Shader "RealGlass/PropGlass"
{
    Properties
    {
        _XYSMap ("Normal XY, specular", 2D) = "gray" {}
        _RGPropRipple ("The water's ripples", 2D) = "bump" {}
        _RGPropST ("The game material's _MainTex_ST", Vector) = (1, 1, 0, 0)
        _RGPropMode ("1 glass, 2 water, 3 reflective", Float) = 1
        _RGPropOptics ("x reflectivity head-on, y clarity, z strength, w shininess needed", Vector) = (0.04, 1, 1, 0)
        _RGPropTint ("Color (rgb linear; w 0: the Water tab's)", Vector) = (1, 1, 1, 1)
        _RGPropLook ("x roughness, y sun glint, z unused, w reflection gain", Vector) = (0.02, 1, 0, 1)
        _RGPropRipples ("Ripples: x strength, y size, z speed", Vector) = (1, 1, 1, 0)
    }

    CGINCLUDE
    #include "GlassCommon.cginc"

    sampler2D _XYSMap;
    sampler2D _RGPropRipple;
    float4 _RGPropST;
    float _RGPropMode;
    float4 _RGPropOptics;
    float4 _RGPropTint;
    float4 _RGPropLook;
    float4 _RGPropRipples;
    float4 _RGPropWaterTab;     // the Water tab's color (linear, x its brightness), a global: water's without one of its own
    float4 _SimulationTime2;    // the game's hour-long clock (y 0..1 an hour, runs with the game, stops paused)

    bool PropIsWater() { return _RGPropMode > 1.5 && _RGPropMode < 2.5; }
    bool PropIsFinish() { return _RGPropMode > 2.5; }

    // Its color, linear: its own, or the Water tab's.
    float3 PropColour()
    {
        return max(_RGPropTint.w > 0.5 ? _RGPropTint.rgb : _RGPropWaterTab.rgb, 0.0);
    }

    struct appdata
    {
        float4 vertex : POSITION;
        float3 normal : NORMAL;
        float4 texcoord : TEXCOORD0;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct v2f
    {
        float4 pos : SV_POSITION;
        float4 worldPos : TEXCOORD0;     // xyz position, w the prop's ground (its origin's world y)
        float3 worldNormal : TEXCOORD1;
        float2 uv : TEXCOORD2;
        float4 screenPos : TEXCOORD3;
    };

    v2f vert(appdata v)
    {
        UNITY_SETUP_INSTANCE_ID(v);
        v2f o;
        float4 world = mul(unity_ObjectToWorld, v.vertex);
        o.pos = mul(UNITY_MATRIX_VP, world);
        o.worldPos = float4(world.xyz, mul(unity_ObjectToWorld, float4(0.0, 0.0, 0.0, 1.0)).y);
        o.worldNormal = UnityObjectToWorldNormal(v.normal);
        o.uv = v.texcoord.xy * _RGPropST.xy + _RGPropST.zw;
        o.screenPos = ComputeScreenPos(o.pos);
        return o;
    }

    // Where the surface is at this pixel: its point, its face's normal, its texture coordinates and
    // the ground under it (the off-screen fill's).
    struct Geo
    {
        float3 P;
        float3 faceN;
        float2 uv;
        float groundY;
    };

    Geo GeoAt(v2f i)
    {
        Geo g;
        g.P = i.worldPos.xyz;
        g.faceN = SafeNormalize(i.worldNormal, float3(0.0, 1.0, 0.0));
        g.uv = i.uv;
        g.groundY = i.worldPos.w;
        return g;
    }

    // Where ours goes: water on the faces that look up; glass on all of it, or -- Shininess needed
    // above 0 -- on its parts at least that shiny, the edge a few per cent of shine wide (a part's edge
    // on the map is a texel or two: no flicker of a hard step under TAA).
    float Cover(float2 uv, float3 faceN, bool water)
    {
        if (water)
            return smoothstep(0.6, 0.85, faceN.y);
        if (_RGPropOptics.w < 0.005)
            return 1.0;
        float spec = 1.0 - tex2D(_XYSMap, uv).z;
        return saturate((spec - _RGPropOptics.w) / 0.06 + 0.5);
    }

    // Still water's normal at world xz: the ripple texture read twice at ~40 m (x Ripple size),
    // drifting apart with the game's clock (x Ripple speed), decoded as the game's water does and laid
    // at half its slope (the game's own gain on water that does not flow; x Ripple strength).
    float3 WaterNormal(float2 xz)
    {
        // The drift in whole turns of the texture over the game's hour-long clock (_SimulationTime2.y:
        // 0..1 an hour), so it comes round without a jump when the clock starts over: at speed 1 that is
        // 40, 25 and 29, 43 turns an hour (0.011, 0.007 and 0.008, 0.012 of the tile a second).
        float4 turns = round(3600.0 * max(_RGPropRipples.z, 0.0) * float4(0.011, 0.007, 0.008, 0.012));
        float4 drift = frac(_SimulationTime2.y * turns);
        float f = 1.0 / max(_RGPropRipples.y, 0.05);
        float4 a = tex2D(_RGPropRipple, xz * (0.025 * f) + drift.xy);
        float4 b = tex2D(_RGPropRipple, xz.yx * (0.023 * f) - drift.zw);
        float2 slope = (float2(a.w, a.y) + float2(b.y, b.w) - 1.0) * (0.25 * max(_RGPropRipples.x, 0.0));
        return normalize(float3(slope.x, 1.0, slope.y));
    }

    float3 Schlick3(float cosTheta, float3 F0)
    {
        float m = 1.0 - saturate(cosTheta);
        float m2 = m * m;
        return F0 + (1.0 - F0) * (m2 * m2 * m);
    }

    // The color at luminance 1 (a filter: what clarity lets through keeps its brightness).
    float3 Filter()
    {
        float3 tint = PropColour();
        return tint / max(Luma(tint), 0.05);
    }

    // The reflectance head-on per channel: glass and water the one number, a finish its color (its
    // brightest channel 1) times it.
    float3 PropF0()
    {
        float r = saturate(_RGPropOptics.x);
        if (PropIsFinish())
        {
            float3 c = PropColour();
            return r * c / max(max(c.r, max(c.g, c.b)), 1e-3);
        }
        return float3(r, r, r);
    }

    // The surface at this pixel: where ours goes (x strength), the normal, the view, the reflectance
    // (water's x its Strength, capped at a mirror).
    struct Surface
    {
        float cover;
        float3 n;
        float3 V;
        float dist;
        float3 F;
    };

    Surface SurfaceAt(Geo g)
    {
        Surface s;
        bool water = PropIsWater();
        float3 faceN = g.faceN;
        s.cover = Cover(g.uv, faceN, water) * saturate(_RGPropOptics.z);
        float3 toP = g.P - _WorldSpaceCameraPos;
        s.dist = length(toP);
        s.V = toP / max(s.dist, 1e-3);
        float3 n = water ? WaterNormal(g.P.xz) : faceN;
        float facing = dot(n, -s.V);
        if (facing < 0.01)
            n = normalize(n + s.V * (facing - 0.01));   // a ripple or a smoothed normal never faces away
        s.n = n;
        s.F = min(Schlick3(saturate(dot(n, -s.V)), PropF0()) * (water ? max(_RGPropLook.w, 0.0) : 1.0), 1.0);
        return s;
    }

    // The sun's glint (GGX, Walter et al. 2007), capped as the buildings' (GlassCommon SunGlint).
    float3 PropGlint(float3 n, float3 V, float3 F, float roughness)
    {
        float3 L = _RGSunDir.xyz;
        float nl = saturate(dot(n, L));
        float3 H = SafeNormalize(L - V, L);
        float nh = saturate(dot(n, H));
        float nv = max(dot(n, -V), 0.05);
        float a = max(roughness * roughness, 0.0016);
        float a2 = a * a;
        float d = nh * nh * (a2 - 1.0) + 1.0;
        float D = a2 / (3.14159265 * d * d);
        float3 spec = min(D * F * 0.25 / nv, 60.0);
        return _RGSunColor.rgb * spec * nl * saturate(_RGSunDir.w) * SunThrough();
    }
    ENDCG

    SubShader
    {
        Tags { "Queue" = "Geometry+499" "IgnoreProjector" = "True" "RenderType" = "Transparent" }

        // 1. What is seen through it: times its clarity, color and 1 - its reflectance.
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1
            Blend DstColor Zero

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            float4 frag(v2f i) : SV_Target
            {
                Surface s = SurfaceAt(GeoAt(i));
                clip(s.cover - 0.004);
                if (_RGMask.z > 0.5 || (_RGDebug.x > 0.5 && _RGDebug.x < 6.5))
                    return float4(1.0, 1.0, 1.0, 1.0);   // the check views: the second pass shows them
                // A finish: the prop's own paint under it, less what reflects. Glass and water: what
                // their clarity and color let through, less what reflects.
                float3 through = PropIsFinish() ? 1.0 - s.F : min(saturate(_RGPropOptics.y) * Filter(), 1.0) * (1.0 - s.F);
                return float4(Finite(lerp(float3(1.0, 1.0, 1.0), through, s.cover)), 1.0);
            }
            ENDCG
        }

        // 2. What it reflects, added.
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1
            Blend One One

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            float4 frag(v2f i) : SV_Target
            {
                Geo g = GeoAt(i);
                Surface s = SurfaceAt(g);
                clip(s.cover - 0.004);
                bool water = PropIsWater();
                bool finish = PropIsFinish();

                if (_RGMask.z > 0.5)   // the mask view, as the buildings': glass magenta, water cyan, a finish yellow
                    return float4((water ? float3(0.0, 1.0, 1.0) : (finish ? float3(1.0, 1.0, 0.0) : float3(1.0, 0.0, 1.0))) * max(s.cover, 0.35), 1.0);

                float3 P = g.P;
                float groundY = g.groundY;
                float3 n = s.n;
                float3 V = s.V;
                float dist = s.dist;
                float3 R = reflect(V, n);

                // Its color. Glass: 4% untinted, the rest of the reflectance through the tint twice.
                // Water: untinted (its surface reflects, its color is below it). A finish: F per channel.
                float3 F = s.F;
                if (!water && !finish)
                {
                    float R0 = max(saturate(_RGPropOptics.x), 1e-3);
                    float3 tint2 = Filter() * Filter();
                    tint2 /= max(Luma(tint2), 0.05);
                    float3 coat = (min(R0, 0.04) + max(R0 - 0.04, 0.0) * tint2) / R0;
                    F *= coat;
                }

                // Steady glints, as the buildings' (Kaplanyan et al. 2016): a normal that turns within a
                // pixel (ripples far away, a curved pane) widens the reflection instead of flickering.
                float3 dnx = ddx(n), dny = ddy(n);
                float kernel = min(2.0 * 0.15 * (dot(dnx, dnx) + dot(dny, dny)), 0.2) * _RGFeatures.w;
                float rough = clamp(_RGPropLook.x, 0.02, 0.6);   // Polish / Frosted (still water 0.05)
                float alpha = rough * rough;
                rough = sqrt(sqrt(saturate(alpha * alpha + kernel)));

                // What it mirrors when the screen has nothing: the ground below the horizon, the sky
                // above it -- or a building standing in the ray's way (the city walk).
                float hz = smoothstep(-0.03, 0.03, R.y);
                float3 sky = SkyRadiance(R, rough);
                float3 fallback = sky;
                if (hz < 1.0)
                    fallback = lerp(GroundFill(P, R, rough, groundY), sky, hz);
                float cityState = 0.0;
                if (_RGFeatures.x > 0.5 && _RGCityMapST.w > 0.5)
                {
                    float3 cityOrigin = P + n * max(0.05, dist * 0.002);
                    float tCity, hitId;
                    float3 hitN;
                    cityState = 2.0;
                    if (CityHit(cityOrigin, R, -1.0, groundY - 1.0, tCity, hitId, hitN))
                    {
                        cityState = 1.0;
                        float footprint = max(length(fwidth(P)), 1e-4);
                        float span = _RGPane2.z * dist / max((dist + tCity) * footprint, 1e-4) / (1.0 + rough * 8.0);
                        float3 hitPoint = cityOrigin + R * tCity;
                        bool finishedPicture = _RGFinished.x > 0.5;
                        float2 hitScreen;
                        float3 seen = CityColour();
                        if (ScreenUV(hitPoint, finishedPicture, hitScreen) && all(hitScreen > 0.0) && all(hitScreen < 1.0))
                            seen = MirroredPicture(hitScreen, 2.5 + rough * 3.0, finishedPicture);
                        fallback = NeighbourRadiance(hitPoint, R, hitN, span, rough, seen);
                    }
                }

                float3 refl = fallback;
                float traceState = 0.0;
                if (_RGTrace.x > 0.5)
                {
                    float3 origin = P + n * max(0.05, dist * 0.002);
                    float3 csOrig = mul(UNITY_MATRIX_V, float4(origin, 1.0)).xyz;
                    float3 csDir = normalize(mul((float3x3)UNITY_MATRIX_V, R));
                    float2 hitUV;
                    float hitDistance;
                    bool clipped;
                    if (TraceReflection(csOrig, csDir, Ign(i.pos.xy), hitUV, hitDistance, clipped))
                    {
                        float2 e = min(hitUV, 1.0 - hitUV);
                        float edge = saturate(min(e.x, e.y) / max(_RGRefl.w, 1e-3));
                        float lod = rough * 5.0 * saturate(hitDistance / 150.0);
                        float3 hitWorld = origin + R * hitDistance;
                        float2 uvLast;
                        bool last = _RGFinished.x > 0.5 && ScreenUV(hitWorld, true, uvLast) &&
                                    all(uvLast >= 0.0) && all(uvLast <= 1.0);
                        float3 onScreen = last ? MirroredPicture(uvLast, lod, true) : MirroredPicture(hitUV, lod, false);
                        refl = lerp(fallback, onScreen, edge);
                        traceState = 1.0;
                    }
                    else
                    {
                        traceState = clipped ? 2.0 : 3.0;
                    }
                }

                // The preview's checks (_RGDebug.x; the mod never sets it): 1 the trace, 6 the city walk.
                if (_RGDebug.x > 0.5 && _RGDebug.x < 1.5)
                    return float4(traceState == 1.0 ? float3(0.0, 1.0, 0.0) : (traceState == 2.0 ? float3(1.0, 0.0, 0.0) : float3(0.0, 0.2, 1.0)), 1.0);
                if (_RGDebug.x > 5.5 && _RGDebug.x < 6.5)
                    return float4(cityState > 1.5 ? float3(0.05, 0.1, 1.0) : (cityState > 0.5 ? float3(0.1, 1.0, 0.1) : float3(1.0, 0.0, 0.0)), 1.0);

                float3 front = F * refl + PropGlint(n, V, F, rough) * max(_RGPropLook.y, 0.0);

                // Water's own color where its clarity lets nothing through: its albedo lit by the sun
                // and the sky, as the game lights a surface (N.L, the trilight's sky), less what reflects.
                if (water)
                {
                    float3 light = _RGSunColor.rgb * saturate(dot(n, _RGSunDir.xyz)) * saturate(_RGSunDir.w) * SunThrough() + _RGAmbSky.rgb;
                    front += (1.0 - F) * (1.0 - saturate(_RGPropOptics.y)) * PropColour() * light;
                }
                return float4(Finite(front * s.cover), 1.0);
            }
            ENDCG
        }
    }
}
