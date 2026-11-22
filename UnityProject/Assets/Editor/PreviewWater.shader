// A stand-in for the game's water (Custom/Water/Default, disassembled 2026-10-05) in the water
// preview: the parts the ripples feed. The ripple normal exactly as the game makes it -- _WaterNormal
// read at world xz x 0.0061 and at (z, x) x 0.0057 with its channels swapped, crossfaded over a 2.5 s
// cycle, decoded x 2 - 1 and halved, the tangent frame flat (x = world +x, y = world -z) -- the sky
// cube along the ray it reflects, and the sun's highlight (Blinn-Phong, exponent 512, as the game's).
// The sky comes back x real water's reflectance (what the mod's own cube makes of it); the body is
// the water colour lit by the sun and the ambient. No flow, foam, rain or pollution here.
Shader "Hidden/RealGlassPreviewWater"
{
    Properties
    {
        _WaterNormal ("", 2D) = "bump" {}
        _Sky ("", Cube) = "" {}
        _BodyColour ("", Color) = (0.03, 0.06, 0.08, 1)
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"

            sampler2D _WaterNormal;
            samplerCUBE _Sky;
            float4 _BodyColour;
            float4 _SimulationTime;
            float4 _PreviewSun;      // xyz: toward the sun; w: its intensity
            float4 _PreviewSunColour;
            float4 _PreviewAmbient;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            // Real water's reflectance of the sky by how far above the horizon the ray goes (Schlick,
            // n 1.333), roughly what the mod's cube bakes in.
            float Rho(float up)
            {
                float c = 1.0 - saturate(up);
                return 0.0204 + 0.9796 * c * c * c * c * c;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 xz = i.world.xz;
                float phase = _SimulationTime.x * 0.4 + 0.2 * sin(0.06 * xz.x) * sin(0.06 * xz.y);
                float blend = 2.0 * abs(0.5 - frac(phase));
                float4 a = tex2D(_WaterNormal, xz * 0.0061);
                float4 b = tex2D(_WaterNormal, xz.yx * 0.0057);
                float2 n2 = (lerp(float2(a.w, a.y), float2(b.y, b.w), blend) * 2.0 - 1.0) * 0.5;
                float l2 = dot(n2, n2);
                n2 /= max(sqrt(l2), 1.0);
                float3 nt = float3(n2, sqrt(max(1.0 - dot(n2, n2), 0.0)));
                float3 n = normalize(float3(nt.x, nt.z, -nt.y));

                float3 v = normalize(_WorldSpaceCameraPos - i.world);
                float3 r = reflect(-v, n);
                float3 sky = texCUBE(_Sky, r).rgb * Rho(abs(r.y));
                float3 l = normalize(_PreviewSun.xyz);
                float3 h = normalize(v + l);
                float sun = _PreviewSun.w;
                float3 body = _BodyColour.rgb * (_PreviewAmbient.rgb + _PreviewSunColour.rgb * sun * saturate(dot(n, l)));
                float3 glint = _PreviewSunColour.rgb * sun * pow(saturate(dot(n, h)), 512.0) * 0.6;
                return float4(body + sky + glint, 1.0);
            }
            ENDCG
        }
    }
}
