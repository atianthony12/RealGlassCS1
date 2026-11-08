// GPU PREVIEW ONLY (not in the bundle): a stand-in for the game's Custom/Vehicles/Vehicle/Rotors as the
// see-through props borrow it (glass panes, glass roofs, pool and pond surfaces), so the preview's
// "vanilla" props look like the game's under the mod's glass and water. From the game's disassembled
// forward pass (2026-11-08): transparent, its OPACITY the specular map (1 - _XYSMap.z) -- the clear
// parts are the dull ones -- the albedo lit, and the game's reflection cube x 0.2 x spec x
// (albedo + 0.25). Lambert from the main light and the sky's ambient; no shadows.
Shader "RealGlass/Preview/Rotors"
{
    Properties
    {
        _MainTex ("Diffuse", 2D) = "white" {}
        _XYSMap ("Normal XY, specular", 2D) = "gray" {}
        _Cube ("The game's reflection cube", CUBE) = "" {}
    }

    SubShader
    {
        Tags { "Queue" = "Transparent" "RenderType" = "Transparent" "IgnoreProjector" = "True" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;
            sampler2D _XYSMap;
            samplerCUBE _Cube;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 normal : TEXCOORD1;
                float3 world : TEXCOORD2;
            };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.normal = UnityObjectToWorldNormal(v.normal);
                o.world = mul(unity_ObjectToWorld, v.vertex).xyz;
                return o;
            }

            float4 frag(v2f i, float face : VFACE) : SV_Target
            {
                float3 albedo = tex2D(_MainTex, i.uv).rgb;
                float spec = 1.0 - tex2D(_XYSMap, i.uv).z;
                float3 n = normalize(i.normal) * (face > 0.0 ? 1.0 : -1.0);
                float3 light = _LightColor0.rgb * saturate(dot(n, _WorldSpaceLightPos0.xyz)) + unity_AmbientSky.rgb;
                float3 V = normalize(i.world - _WorldSpaceCameraPos);
                float3 cube = texCUBE(_Cube, reflect(V, n)).rgb;
                return float4(albedo * light + cube * 0.2 * spec * (albedo + 0.25), spec);
            }
            ENDCG
        }
    }
}
