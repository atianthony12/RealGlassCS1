// The preview's stand-in for Volumetric Weather's clouds, with the interface the mod relies on: a
// mesh renderer named "VolumetricCloudsVolume" round the camera, a Transparent+100 material that
// draws the sky's clouds in premultiplied alpha (Blend One OneMinusSrcAlpha, as VW's raymarch),
// hidden behind buildings by the depth texture while _UseDepth is 1 -- which Real Glass's sky
// camera turns off for its own draw. Not VW's clouds: a flat layer of fractal noise at 1500 m.
Shader "RealGlass/Preview/Clouds"
{
    Properties
    {
        _UseDepth ("Use the depth texture", Float) = 1
    }

    SubShader
    {
        Tags { "Queue" = "Transparent+100" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float _UseDepth;
            float4 _PreviewCloudLight;   // rgb: the clouds' sunlit colour
            float _PreviewOvercast;      // 0 the broken layer .. 1 a closed deck (the preview's "Overcast=")
            float _PreviewCloudFade;     // the horizon fade's slope (0 unset: 10)
            float _PreviewBank;          // the bank's light, x _PreviewCloudLight (0 unset: no bank)
            float _PreviewBankTop;       // the bank's top, degrees
            float _PreviewCloudShade;    // the layer above it x this (0 unset: 1)
            sampler2D_float _CameraDepthTexture;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldPos : TEXCOORD0;
                float4 screenPos : TEXCOORD1;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.screenPos = ComputeScreenPos(o.pos);
                return o;
            }

            float Hash(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            float Noise(float2 p)
            {
                float2 i = floor(p), f = frac(p);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + float2(1, 1)), f.x), f.y);
            }

            float Fbm(float2 p)
            {
                float s = 0.0, a = 0.5;
                for (int k = 0; k < 5; k++)
                {
                    s += a * Noise(p);
                    p = p * 2.03 + 17.1;
                    a *= 0.5;
                }
                return s;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 origin = _WorldSpaceCameraPos;
                float3 dir = normalize(i.worldPos - origin);
                if (dir.y < 0.0)
                    return float4(0, 0, 0, 0);

                float eye = 1e9;
                float3 forward = -UNITY_MATRIX_V[2].xyz;
                if (_UseDepth > 0.5)
                {
                    float raw = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screenPos)));
                    if (raw < _ProjectionParams.z * 0.98)
                        eye = raw / max(dot(dir, forward), 1e-4);
                }

                // "cloudbank" (FoamPreview, 2026-11-21): a bright bank of cloud low over the horizon, full up to 0.6 x
                // _PreviewBankTop degrees and gone by it, x _PreviewBank the clouds' light (0 unset: none), 20 km
                // away; the layer above it x _PreviewCloudShade (0 unset: 1; below 1: grey bases).
                float elevation = degrees(asin(saturate(dir.y)));
                float top = max(_PreviewBankTop, 0.5);
                float bankA = _PreviewBank > 0.0 && eye > 20000.0 ? 1.0 - smoothstep(0.6 * top, top, elevation) : 0.0;
                float bankShade = lerp(0.88, 1.0, Noise(float2(atan2(dir.z, dir.x) * 9.0, elevation * 0.6)));
                float3 bank = _PreviewCloudLight.rgb * _PreviewBank * bankShade;

                float4 layer = float4(0, 0, 0, 0);
                float t = (1500.0 - origin.y) / max(dir.y, 1e-4);
                if (dir.y >= 0.01 && t > 0.0 && eye >= t)
                {
                    float2 p = (origin.xz + dir.xz * t) / 2200.0;
                    // The layer fades out toward the horizon (below ~6 deg at 10; _PreviewCloudFade, FoamPreview's
                    // "cloudfade", set higher keeps it down to a lower angle; unset: 10, as before).
                    float fade = _PreviewCloudFade > 0.0 ? _PreviewCloudFade : 10.0;
                    float cover = saturate((Fbm(p) - 0.47 + _PreviewOvercast * 0.6) * 3.5) * saturate(dir.y * fade);
                    float shade = lerp(0.62, 1.0, saturate(Fbm(p * 1.7 + 5.3) * 1.3)) * (_PreviewCloudShade > 0.0 ? _PreviewCloudShade : 1.0);
                    layer = float4(_PreviewCloudLight.rgb * shade * cover, cover);
                }
                return float4(bank * bankA + layer.rgb * (1.0 - bankA), bankA + layer.a * (1.0 - bankA));
            }
            ENDCG
        }
    }
}
