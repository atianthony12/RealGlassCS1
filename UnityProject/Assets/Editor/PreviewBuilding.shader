// GPU PREVIEW ONLY (not in the bundle): a stand-in for the game's Custom/Buildings/Building/Default,
// so the preview's "vanilla" looks like the game's buildings under the glass. From the game's
// disassembled final pass: the albedo lit by the light buffer, plus its only reflection --
// cube x 0.2 x spec x (albedo + 0.25), spec = 1 - _XYSMap.z, no Fresnel -- plus, at night, the
// windows the illumination channel (_ACIMap.b) marks, lit when their value passes the evening's
// threshold -- or, with RG_GAMELIGHTS=1 (_PreviewGameLights), by the game's own rule (read
// 2026-10-25: docs/NIGHT-MAP-PLAN.md). Lambert through the legacy deferred prepass, as the game's
// buildings are.
Shader "RealGlass/Preview/Building"
{
    Properties
    {
        _MainTex ("Diffuse", 2D) = "white" {}
        _XYSMap ("Normal XY, specular", 2D) = "gray" {}
        _ACIMap ("Alpha, colour, illumination", 2D) = "black" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" }

        CGPROGRAM
        #pragma surface surf Lambert addshadow
        #pragma target 3.5

        sampler2D _MainTex;
        sampler2D _XYSMap;
        sampler2D _ACIMap;
        samplerCUBE _RGSky;
        float _PreviewNight;        // 0 day .. 1 night
        float _PreviewLitShare;     // windows lit at night (their _ACIMap.b above 1 - share)
        float _PreviewGameLights;   // 1: the game's own rule below instead
        float4 _ObjectIndex;        // the building's colour index (its block)
        float4 _RGColorMap;         // the object colour map's size (GlassUniforms)
        float4 _SimulationTime2;    // the game's clock (w: seconds wrapping at 3600, in radians)
        float _GiantMarshmallow;    // the game's buildings' lights level (negative at night)
        float _PreviewGameShading;  // 1: the game's own final pass (below), RG_GAMESHADE=1; 0: the stand-in as before
        float4 _PreviewPalette;     // the game's _BuildingLightColorPalette colour stood in for (one for all)

        struct Input
        {
            float2 uv_MainTex;
            float3 worldRefl;
        };

        float Hash12(float2 p)
        {
            float3 p3 = frac(float3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return frac((p3.x + p3.y) * p3.z);
        }

        // The game's two bands (Custom/Buildings/Building/Default's final pass). Its palette's alpha
        // -- when the building switches on at dusk -- is the mod's stand-in for it (Interiors.cginc,
        // GameWindowLit), so the two agree at dusk as they do in the game.
        float GameBands(float b, float2 ci)
        {
            float2 threshold = sin(ci * 31.3 + _SimulationTime2.w * float2(11.0, 7.0)) * 0.125 + float2(0.615, 0.885);
            float2 band = saturate(float2(threshold.x - b, b - threshold.y) * 5.0);
            band = band * band * (3.0 - 2.0 * band);
            float2 cell = floor(ci * _RGColorMap.xy);
            float id = cell.x + cell.y * _RGColorMap.x;
            float alpha = Hash12(float2(fmod(id, 997.0) + 0.5, floor(id / 997.0) + 0.71));
            float on = saturate((alpha + 0.01 - _GiantMarshmallow) * 50.0);
            on = on * on * (3.0 - 2.0 * on);
            return (band.x + band.y) * on;
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            float4 c = tex2D(_MainTex, IN.uv_MainTex);
            float4 xys = tex2D(_XYSMap, IN.uv_MainTex);
            float4 aci = tex2D(_ACIMap, IN.uv_MainTex);
            float spec = 1.0 - xys.z;
            if (_PreviewGameShading > 0.5)
            {
                // The game's own final pass (Custom/Buildings/Building/Default, UNITY_HDR_ON
                // SINGLE_INSTANCE INFOMODE_OFF SHADING, read from its GLCore program 2026-11-12), a
                // building built, not burned or abandoned, dry, its colour variation and the interior
                // floor left out: out = (light + ambient) x (albedo + sheen) + sheen + glow + palette x 3
                // x bands, where sheen = cube(R, mip 8 (1 - spec)) x 0.2 spec x (albedo + 0.25), and
                // _ACIMap is read ONCE, filtered, with a mip bias of -1: b <= 0.48 glows albedo x 20 b,
                // above it the two bands light the palette's colour x 3. (Its light buffer's specular
                // term x 0.5 spec is left out: Lambert here.)
                float4 aciB = tex2Dbias(_ACIMap, float4(IN.uv_MainTex, 0.0, -1.0));
                clip(0.5 - aciB.r);   // the asset's cut-out, as the game tests it
                float b = aciB.b;
                float3 sheen = texCUBElod(_RGSky, float4(IN.worldRefl, 8.0 * (1.0 - spec))).rgb * 0.2 * spec * (c.rgb + 0.25);
                float bands = b > 0.48 ? GameBands(b, _ObjectIndex.xy) : 0.0;
                float3 glowGame = b > 0.48 ? float3(0.0, 0.0, 0.0) : c.rgb * 20.0 * b;
                o.Albedo = c.rgb + sheen;
                o.Emission = sheen + glowGame + _PreviewPalette.rgb * 3.0 * bands;
                return;
            }
            o.Albedo = c.rgb;
            float3 refl = texCUBElod(_RGSky, float4(IN.worldRefl, 8.0 * (1.0 - spec))).rgb * 0.2 * spec * (c.rgb + 0.25);
            float lit = step(1.0 - _PreviewLitShare, aci.b) * step(0.48, aci.b) * _PreviewNight;
            float3 glow = float3(0.0, 0.0, 0.0);
            if (_PreviewGameLights > 0.5)
            {
                float b = tex2Dbias(_ACIMap, float4(IN.uv_MainTex, 0.0, -1.0)).b;
                lit = b > 0.48 ? GameBands(b, _ObjectIndex.xy) : 0.0;
                glow = b > 0.48 ? glow : c.rgb * 20.0 * b;   // day and night
            }
            float3 lamp = float3(1.0, 0.80, 0.56) * 1.8 * (0.6 + 0.4 * aci.b);
            o.Emission = refl * (1.0 - 0.7 * _PreviewNight) + lamp * lit + glow;
        }
        ENDCG
    }

    Fallback "Legacy Shaders/VertexLit"
}
