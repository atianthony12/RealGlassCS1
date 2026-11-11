// A stand-in for the game's water (Custom/Water/Default) in the foam preview, on the simulation's own
// data (WaterGame.cginc: the same surface heights, terrain, flow and depth the game's shader reads, so
// the water lands where the foam layer lands). Its look as PreviewWater.shader's: the game's ripple
// normal from _WaterNormal, carried along the flow exactly as the game's pixel program does
// (WaterGame.cginc's RGGameWaterNormal, which the sky layer shares), plus the surface's own slope; the
// sky as the mod's cube had it (x _SkyGain; 0 under the sky layer); the sun's highlight (exponent 512,
// x _SpecGain: the game's x its material's _SpecColor, which the mod turns off under the sun's glitter).
// The shore as the game's (since 2026-11-11; until then an alpha 1 - 10 / (3 t + 10) over the frame):
// its program draws Blend One Zero over a named grab of the frame before the water, _BackgroundTexture,
// and composites the bed itself -- its opacity by the thickness along the view, the bed read where its
// ripple bends it, tinted by the game's _WaterColorUnder (a global; the preview sets it per shot) --
// WaterGame.cginc's RGGameBedShown, which the sky layer's clear shallows take back out. The stand-in's
// own colour, sky and highlight x that opacity, as before. Its depth state is the game's
// (Custom/Water/Default's ForwardBase, read from its m_ParsedForm 2026-10-23): ZWrite On, ZTest LEqual,
// Cull Back, Offset -3, 0 -- what the layers on the water are tested against. No pollution, rain rings
// or foam of the game's own.
Shader "Hidden/RealGlassPreviewGameWater"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
        _WaterNormal ("", 2D) = "bump" {}
        _Sky ("", Cube) = "" {}
        _BodyColour ("", Color) = (0.03, 0.06, 0.08, 1)
        _SkyGain ("", Float) = 1
        _SpecGain ("", Float) = 0.6
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry+1" }
        // Once a frame, before the first water drawn (a named grab), as the game's.
        GrabPass { "_BackgroundTexture" }
        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite On
            ZTest LEqual
            Cull Back
            Offset -3, 0
            Blend One Zero
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "../RealGlass/WaterGame.cginc"
            #include "../RealGlass/WaterCommon.cginc"

            samplerCUBE _Sky;
            float _SkyGain;
            float _SpecGain;
            float4 _BodyColour;
            float4 _PreviewSun;      // xyz: toward the sun; w: its intensity
            float4 _PreviewSunColour;
            float4 _PreviewAmbient;
            // The rounding the game's own program differs from ours by (m, a global; 0 = none): in the
            // game the GPU's driver compiles the game's water and our layers apart, so a vertex can land
            // a hair nearer or farther along its eye ray than ours. Each vertex here moves a pseudo-random
            // share of this along its eye ray (its screen place unchanged), as the game's might.
            float _PreviewDepthSkew;
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
            sampler2D _BackgroundTexture;
            // Preview checks (globals, 0 = off): 1 shows _WaterColorUnder as the shader sees it; 2 leaves
            // the bed out (its own colour, sky and highlight x the opacity only).
            float _PreviewGameWaterCheck;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                float water : TEXCOORD1;
                float4 screen : TEXCOORD2;
            };

            v2f vert(appdata_base v)
            {
                RGGameWaterVertex g = RGGameWaterAt(v.vertex);
                v2f o;
                o.world = g.world;
                float2 key = mul(unity_ObjectToWorld, v.vertex).xz;
                float skew = (frac(sin(dot(key, float2(12.9898, 78.233))) * 43758.5453) * 2.0 - 1.0) * _PreviewDepthSkew;
                o.pos = mul(UNITY_MATRIX_VP, float4(g.world + normalize(_WorldSpaceCameraPos - g.world) * skew, 1.0));
                o.water = g.waterHeight;
                o.screen = ComputeScreenPos(o.pos);
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = RGGameSurfaceUV(i.world);
                float4 sa, sb;
                RGGameSurface(uv, sa, sb);
                float depth = i.water - RGGameTerrainPixel(uv);
                float wet = min(depth, sb.y - 0.001);

                RGGameRippleReads reads = RGGameRipplePlaces(i.world.xz, sb);
                float3 n = RGGameWaterNormalAt(reads, sa);
                float dirt = RGGameDirt(sa);
                float2 ripple = RGGameBedRipple(reads, dirt);

                float3 v = normalize(_WorldSpaceCameraPos - i.world);
                float3 r = reflect(-v, n);
                // The mod's cube until 2026-10-08 (RealGlass/WaterCube): the sky x the rough sea's
                // reflectance by the reflected ray's height (a ray below the level: the grazing value),
                // x the Sky slider; 0 when the sky layer (RealGlass/WaterSky) draws the sky instead.
                float3 sky = texCUBE(_Sky, r).rgb * RGSeaRho(max(r.y, 0.0)) * _SkyGain;
                float3 l = normalize(_PreviewSun.xyz);
                float3 h = normalize(v + l);
                float sun = _PreviewSun.w;
                float3 body = _BodyColour.rgb * (_PreviewAmbient.rgb + _PreviewSunColour.rgb * sun * saturate(dot(n, l)));
                float3 glint = _PreviewSunColour.rgb * sun * pow(saturate(dot(n, h)), 512.0) * _SpecGain;

                float scene = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_PROJ(_CameraDepthTexture, UNITY_PROJ_COORD(i.screen)));
                float opacity;
                float3 bed = RGGameBedShown(_BackgroundTexture, i.screen, scene, ripple, dirt, opacity);

                clip(wet);
                float keepBed = 1.0 - step(1.5, _PreviewGameWaterCheck);
                float showUnder = step(0.5, _PreviewGameWaterCheck) * keepBed;
                float3 colour = bed * keepBed + (body + sky + glint) * opacity;
                return float4(lerp(colour, _WaterColorUnder.rgb, showUnder), 1.0);
            }
            ENDCG
        }
    }
}
