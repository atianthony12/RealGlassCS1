// Real Glass: what the glass MIRRORS, as full-screen passes at the end of the deferred frame
// (GlassPrePass.cs), drawn on a cube round the camera (as CityReflection's: the camera's own
// matrices stay bound) into targets the size of the screen.
//   0 REFLECT: per glass pixel of the pre-pass's G-buffer (GlassPrePass.shader), the reflection
//     Glass.shader used to trace itself, moved here unchanged -- the ground at the building's feet or
//     the sky, the buildings round from above (the city walk), the screen-space trace into last
//     frame's finished picture. rgb that light, a the reflection's VIRTUAL distance: how far behind
//     the pane the mirrored thing seems (|P - eye| + its distance along the reflected ray; 20 km for
//     the sky); 0 where there is no glass. The point the glass stands on is the scene's depth here
//     (the glass is drawn over the building's own surface).
//   1 HISTORY (against TAA's flicker): this frame's reflection blended with last frame's, fetched
//     where the reflection's virtual point -- eye + view ray x the virtual distance, where a mirror
//     image seems to be -- was on last frame's screen, clamped to the box of this frame's 3 x 3 glass
//     neighbours (Karis 2014, "High-Quality Temporal Supersampling", as CityReflection's pass 3 does
//     for the water). By the virtual point, not by the pane it is seen on: TAA reprojects the pane,
//     so a moving camera's history for the reflection came from the wrong place. (A mirror image
//     lies on the extended view ray at the summed distance: plain geometric optics.)
Shader "RealGlass/GlassReflection"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "GlassCommon.cginc"

    sampler2D _RGGlassG0;          // GlassPrePass.shader's targets
    sampler2D _RGGlassG1;
    sampler2D _RGReflect;          // pass 1: this frame's reflection (pass 0's)
    sampler2D _RGReflectionPrev;   // pass 1: last frame's result
    float4 _RGReflectionInfo;      // x the history's weight (0: none yet), y 1 / width, z 1 / height

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

    float3 OctDecode(float2 e)
    {
        float3 n = float3(e.x, e.y, 1.0 - abs(e.x) - abs(e.y));
        float t = saturate(-n.z);
        n.x += n.x >= 0.0 ? -t : t;
        n.y += n.y >= 0.0 ? -t : t;
        return SafeNormalize(n, float3(0.0, 0.0, 1.0));
    }
    ENDCG

    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "Reflect"

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float2 suv = i.screen.xy / i.screen.w;
                float4 g0 = tex2Dlod(_RGGlassG0, float4(suv, 0.0, 0.0));
                float4 g1 = tex2Dlod(_RGGlassG1, float4(suv, 0.0, 0.0));
                float4 result = float4(0.0, 0.0, 0.0, 0.0);
                if (FiniteIn(g0.w, 0.0, 1.0) > 0.0)
                {
                    float3 d = SafeNormalize(i.dir, float3(0.0, 0.0, 1.0));
                    float3 forward = -normalize(UNITY_MATRIX_V[2].xyz);
                    float eye = -SceneZ(suv * _ScreenParams.xy);
                    float3 P = _WorldSpaceCameraPos + d * (eye / max(dot(d, forward), 1e-4));
                    float3 np = OctDecode(g0.xy);
                    float rough = g0.z;
                    float id = floor(g1.x + 0.5) * 256.0 + floor(g1.y + 0.5);
                    float baseY = P.y - g1.z;
                    float footprint = max(g1.w, 1e-4);
                    float3 toP = P - _WorldSpaceCameraPos;
                    float dist = length(toP);
                    float3 V = toP / max(dist, 1e-3);
                    float3 R = reflect(V, np);

                    // What the glass reflects: the screen where the ray finds something, else the
                    // ground at the building's feet (below the horizon) or the sky.
                    float hz = smoothstep(-0.03, 0.03, R.y);
                    float3 sky = SkyRadiance(R, rough);
                    float3 fallback = sky;
                    float behind = 20000.0;   // how far the mirrored thing is along R
                    if (hz < 1.0)
                    {
                        fallback = lerp(GroundFill(P, R, rough, baseY), sky, hz);
                        float tGround = min(max(P.y - baseY, 1.0) / max(-R.y, 0.02), 4000.0);
                        behind = hz < 0.5 ? tGround : behind;
                    }

                    // The buildings round: where one stands in the reflected ray's way, above the
                    // horizon or below it, the glass mirrors that building, not the sky or the street.
                    float cityState = 0.0;   // 0 no map, 1 a building met, 2 none
                    if (_RGFeatures.x > 0.5 && _RGCityMapST.w > 0.5)
                    {
                        float3 cityOrigin = P + np * max(0.3, dist * 0.003);
                        float tCity, hitId;
                        float3 hitN;
                        cityState = 2.0;
                        if (CityHit(cityOrigin, R, id, baseY - 1.0, tCity, hitId, hitN))
                        {
                            // How many pixels a storey of it covers in the reflection: a storey at the
                            // ray's whole length, over the pixel's angle, blurred by the roughness.
                            float span = _RGPane2.z * dist / max((dist + tCity) * footprint, 1e-4) / (1.0 + rough * 8.0);
                            float3 hitPoint = cityOrigin + R * tCity;

                            // A stand-in: the building as the screen shows it there (its colours, its
                            // haze), blurred; the city's average where it is off screen.
                            bool finishedPicture = _RGFinished.x > 0.5;
                            float2 hitScreen;
                            float3 seen = CityColour();
                            if (ScreenUV(hitPoint, finishedPicture, hitScreen) && all(hitScreen > 0.0) && all(hitScreen < 1.0))
                                seen = MirroredPicture(hitScreen, 2.5 + rough * 3.0, finishedPicture);
                            fallback = NeighbourRadiance(hitPoint, R, hitN, span, rough, seen);
                            behind = tCity;
                            cityState = 1.0;
                        }
                    }

                    float3 refl = fallback;
                    float traceState = 0.0;   // 1 hit, 2 left the screen, 3 ran out of steps
                    if (_RGTrace.x > 0.5)
                    {
                        float3 origin = P + np * max(0.3, dist * 0.003);
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
                            // The point hit, read in the finished picture where it was last frame
                            // (the copy before the fog at the hit itself when it was off screen).
                            float3 hitWorld = origin + R * hitDistance;
                            float2 uvLast;
                            bool last = _RGFinished.x > 0.5 && ScreenUV(hitWorld, true, uvLast) &&
                                        all(uvLast >= 0.0) && all(uvLast <= 1.0);
                            float3 onScreen = last ? MirroredPicture(uvLast, lod, true) : MirroredPicture(hitUV, lod, false);
                            refl = lerp(fallback, onScreen, edge);
                            behind = edge > 0.5 ? hitDistance : behind;
                            traceState = 1.0;
                        }
                        else
                        {
                            traceState = clipped ? 2.0 : 3.0;
                        }
                    }

                    result = float4(Finite(refl), FiniteIn(dist + behind, 1.0, 1e6));

                    // The check views (the GPU preview's; the mod never sets _RGDebug), shown by
                    // Glass.shader where it reads this.
                    if (_RGDebug.x > 0.5 && _RGDebug.x < 1.5)
                        result.rgb = traceState == 1.0 ? float3(0.0, 1.0, 0.0) : (traceState == 2.0 ? float3(1.0, 0.0, 0.0) : float3(0.0, 0.2, 1.0));
                    if (_RGDebug.x > 1.5 && _RGDebug.x < 2.5)
                    {
                        // 2: the depth texture here, in grey bands every 50 m, red where the point
                        // rebuilt from it is not where it says (the rebuild itself).
                        float own2 = -mul(UNITY_MATRIX_V, float4(P, 1.0)).z;
                        result.rgb = frac(eye / 50.0).xxx * (abs(eye - own2) > 2.0 ? float3(1.0, 0.2, 0.2) : float3(1.0, 1.0, 1.0));
                    }
                    if (_RGDebug.x > 4.5 && _RGDebug.x < 5.5)
                    {
                        // 5: the finished picture where this pane's point was last frame.
                        float2 uvLast;
                        result.rgb = ScreenUV(P, true, uvLast) ? tex2Dlod(_RGSceneFinished, float4(uvLast, 0.0, 0.0)).rgb : float3(1.0, 0.0, 1.0);
                    }
                    if (_RGDebug.x > 5.5 && _RGDebug.x < 6.5)
                        result.rgb = cityState > 1.5 ? float3(0.05, 0.1, 1.0) : (cityState > 0.5 ? float3(0.1, 1.0, 0.1) : float3(1.0, 0.0, 0.0));
                    if (_RGDebug.x > 8.5 && _RGDebug.x < 9.5)
                    {
                        // 9: the hierarchical depth at level _RGDebug.y, its nearest in grey bands every
                        // 50 m; red where the level does not hold the depth texture here between its
                        // nearest and farthest (level 0: the depth itself), blue where there is none.
                        if (_RGHiZInfo.w < 0.5)
                        {
                            result.rgb = float3(0.1, 0.2, 1.0);
                        }
                        else
                        {
                            float lvl = min(_RGDebug.y, _RGHiZInfo.x - 1.0);
                            float2 pix = suv * _RGHiZInfo.yz;
                            float2 hzr = HiZRange(floor(pix / exp2(lvl)), lvl);
                            float tol = max(0.05, eye * 0.002);
                            bool wrong = hzr.x > eye + tol || hzr.y < eye - tol;
                            result.rgb = frac(hzr.x / 50.0).xxx * (wrong ? float3(1.0, 0.1, 0.1) : float3(1.0, 1.0, 1.0));
                        }
                    }
                }
                return result;
            }
            ENDCG
        }

        Pass
        {
            Name "History"

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                float4 c = tex2Dlod(_RGReflect, float4(uv, 0.0, 0.0));
                float4 result = c;
                if (c.a > 0.0 && _RGReflectionInfo.x > 0.0)
                {
                    // Where the mirror image seemed to be last frame.
                    float3 d = SafeNormalize(i.dir, float3(0.0, 0.0, 1.0));
                    float3 Pv = _WorldSpaceCameraPos + d * c.a;
                    float4 clip = mul(_RGPrevVP, float4(Pv, 1.0));
                    float2 prev = clip.xy / max(clip.w, 1e-4) * 0.5 + 0.5;
                    bool inside = clip.w > 1e-4 && prev.x >= 0.0 && prev.x <= 1.0 && prev.y >= 0.0 && prev.y <= 1.0;
                    float4 history = tex2Dlod(_RGReflectionPrev, float4(prev, 0.0, 0.0));
                    if (inside && history.a > 0.0)
                    {
                        // The box of this frame's glass neighbours (a pixel with no glass is no neighbour).
                        float3 lo = c.rgb, hi = c.rgb;
                        [unroll]
                        for (int y = -1; y <= 1; y++)
                        {
                            [unroll]
                            for (int x = -1; x <= 1; x++)
                            {
                                float4 s = tex2Dlod(_RGReflect, float4(uv + float2(x * _RGReflectionInfo.y, y * _RGReflectionInfo.z), 0.0, 0.0));
                                if (s.a > 0.0)
                                {
                                    lo = min(lo, s.rgb);
                                    hi = max(hi, s.rgb);
                                }
                            }
                        }
                        float3 held = clamp(FiniteSigned(history.rgb), lo, hi);
                        result = float4(Finite(lerp(c.rgb, held, _RGReflectionInfo.x)), c.a);
                    }
                }
                return result;
            }
            ENDCG
        }
    }
}
