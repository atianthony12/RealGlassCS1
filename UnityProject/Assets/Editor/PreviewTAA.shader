// The water preview's stand-in for the temporal anti-aliasing Render It! runs (Unity's Post-processing
// Stack v1, TemporalAntialiasing: Halton(2, 3) jitter of the projection, the current frame read
// un-jittered and sharpened, the history clipped to the box of two half-texel corner reads widened by
// a luminance nudge, then blended), for a camera that does not move: no motion vectors, the
// stationary blending throughout. Its job is to measure flicker, not to ship.
Shader "Hidden/RealGlassPreviewTAA"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D _HistoryTex;
            // xy: this frame's jitter (uv).
            float4 _Jitter;
            // x: sharpen; y: stationary blending; z: 1 on the first frame (no history yet).
            float4 _TAA;

            float Max3(float3 c)
            {
                return max(c.x, max(c.y, c.z));
            }

            float4 FastToneMap(float4 c)
            {
                return float4(c.rgb / (Max3(c.rgb) + 1.0), c.a);
            }

            float4 FastToneMapInverse(float4 c)
            {
                return float4(c.rgb / max(1.0 - Max3(c.rgb), 1e-4), c.a);
            }

            float4 ClipToAABB(float4 color, float3 minimum, float3 maximum)
            {
                float3 center = 0.5 * (maximum + minimum);
                float3 extents = 0.5 * (maximum - minimum);
                float3 offset = color.rgb - center;
                float3 ts = abs(extents / (offset + 0.0001));
                float t = saturate(min(ts.x, min(ts.y, ts.z)));
                color.rgb = center + offset * t;
                return color;
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 k = _MainTex_TexelSize.xy;
                float2 uv = i.uv - _Jitter.xy;
                float4 color = tex2D(_MainTex, uv);
                float4 topLeft = tex2D(_MainTex, uv - k * 0.5);
                float4 bottomRight = tex2D(_MainTex, uv + k * 0.5);
                float4 corners = 4.0 * (topLeft + bottomRight) - 2.0 * color;
                color += (color - corners * 0.166667) * 2.718282 * _TAA.x;
                color = max(0.0, color);
                if (_TAA.z > 0.5)
                    return color;

                float4 average = FastToneMap((corners + color) * 0.142857);
                topLeft = FastToneMap(topLeft);
                bottomRight = FastToneMap(bottomRight);
                color = FastToneMap(color);
                float4 history = FastToneMap(tex2D(_HistoryTex, i.uv));
                float nudge = 4.0 * abs(Luminance(average.rgb) - Luminance(color.rgb));
                float4 minimum = min(bottomRight, topLeft) - nudge;
                float4 maximum = max(topLeft, bottomRight) + nudge;
                history = ClipToAABB(history, minimum.xyz, maximum.xyz);
                return FastToneMapInverse(lerp(color, history, _TAA.y));
            }
            ENDCG
        }

        // 1 A MOVING camera (the glass harness's pan, 2026-11-08): as pass 0, but the history is
        // fetched where this pixel's depth point was last frame -- Post-processing Stack v1's camera
        // motion vectors for static geometry -- and blended by its rule, between the stationary and
        // the motion blending by the motion's length x 6000 (its motion amplification).
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D _HistoryTex;
            sampler2D _RGHiZ;            // level 0: this frame's eye depth (m)
            float4 _Jitter;
            float4 _TAA;                 // x sharpen, y stationary blending, z first frame, w motion blending
            float4x4 _CurInvP;           // this frame's un-jittered projection (OpenGL convention), inverted
            float4x4 _CurCamToWorld;     // camera (view space, -z ahead) -> world
            float4x4 _PrevVP;            // last frame's un-jittered projection x view

            float Max3(float3 c) { return max(c.x, max(c.y, c.z)); }
            float4 FastToneMap(float4 c) { return float4(c.rgb / (Max3(c.rgb) + 1.0), c.a); }
            float4 FastToneMapInverse(float4 c) { return float4(c.rgb / max(1.0 - Max3(c.rgb), 1e-4), c.a); }
            float4 ClipToAABB(float4 color, float3 minimum, float3 maximum)
            {
                float3 center = 0.5 * (maximum + minimum);
                float3 extents = 0.5 * (maximum - minimum);
                float3 offset = color.rgb - center;
                float3 ts = abs(extents / (offset + 0.0001));
                float t = saturate(min(ts.x, min(ts.y, ts.z)));
                color.rgb = center + offset * t;
                return color;
            }

            float4 frag(v2f_img i) : SV_Target
            {
                float2 k = _MainTex_TexelSize.xy;
                float2 uv = i.uv - _Jitter.xy;
                float4 color = tex2D(_MainTex, uv);
                float4 topLeft = tex2D(_MainTex, uv - k * 0.5);
                float4 bottomRight = tex2D(_MainTex, uv + k * 0.5);
                float4 corners = 4.0 * (topLeft + bottomRight) - 2.0 * color;
                color += (color - corners * 0.166667) * 2.718282 * _TAA.x;
                color = max(0.0, color);
                if (_TAA.z > 0.5)
                    return color;

                float eye = tex2Dlod(_RGHiZ, float4(i.uv, 0.0, 0.0)).r;
                float4 view = mul(_CurInvP, float4(i.uv * 2.0 - 1.0, 1.0, 1.0));
                float3 ray = view.xyz / view.w;
                float3 world = mul(_CurCamToWorld, float4(ray * (eye / max(-ray.z, 1e-4)), 1.0)).xyz;
                float4 clip = mul(_PrevVP, float4(world, 1.0));
                float2 prev = clip.xy / max(clip.w, 1e-4) * 0.5 + 0.5;
                float2 motion = i.uv - prev;

                float4 average = FastToneMap((corners + color) * 0.142857);
                topLeft = FastToneMap(topLeft);
                bottomRight = FastToneMap(bottomRight);
                color = FastToneMap(color);
                float4 history = FastToneMap(tex2D(_HistoryTex, prev));
                float nudge = 4.0 * abs(Luminance(average.rgb) - Luminance(color.rgb));
                float4 minimum = min(bottomRight, topLeft) - nudge;
                float4 maximum = max(topLeft, bottomRight) + nudge;
                history = ClipToAABB(history, minimum.xyz, maximum.xyz);
                float weight = clamp(lerp(_TAA.y, _TAA.w, length(motion) * 6000.0), _TAA.w, _TAA.y);
                return FastToneMapInverse(lerp(color, history, weight));
            }
            ENDCG
        }
    }
}
