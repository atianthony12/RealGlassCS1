// The foam preview's seabed scene (FoamPreview's s_seabed, foam-render.ps1 -Seabed): the ground as
// Legacy Shaders/Diffuse draws it (deferred, in the depth texture as the game's terrain is), its albedo the
// pale bed's texture with
//   - a HARD wavy edge to a dark bed, crossing the scene's slope from 5 m of water (z = -200) to 30 m
//     (z = 110) -- the line a sand's edge draws far out on a theme's seabed;
//   - metre-scale detail on it all: patches 6 m across, sand ripples 0.7 m apart, pebbles of 0.8 m --
//     what the realistic depth's bend and sunlight need to be seen; faded where a pixel spans them.
// The edge is FoamPreview's Coast.ShoreX in metres from the waterline.
Shader "Hidden/RealGlassPreviewSeabed"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert
        #pragma target 3.5

        sampler2D _MainTex;

        struct Input
        {
            float2 uv_MainTex;
            float3 worldPos;
        };

        float Hash(float2 p)
        {
            return frac(sin(dot(p, float2(127.1, 311.7))) * 43758.5453);
        }

        // Value noise, 0..1, cells of 1.
        float Noise(float2 p)
        {
            float2 i = floor(p), f = frac(p);
            float2 u = f * f * (3.0 - 2.0 * f);
            float a = Hash(i), b = Hash(i + float2(1.0, 0.0)), c = Hash(i + float2(0.0, 1.0)), d = Hash(i + float2(1.0, 1.0));
            return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
        }

        void surf(Input IN, inout SurfaceOutput o)
        {
            float3 c = tex2D(_MainTex, IN.uv_MainTex).rgb;
            float x = IN.worldPos.x, z = IN.worldPos.z;
            float shore = 30.0 * sin(z / 140.0) + 14.0 * sin(z / 53.0 + 1.0);
            float s = x - shore;
            float edge = -50.0 - 0.8 * (z + 200.0) + 12.0 * sin(z / 23.0) + 5.0 * sin(z / 7.3);
            float region = step(-260.0, z) * step(z, 320.0) * step(IN.worldPos.y, 39.0);
            float dark = step(s, edge) * region;
            c = lerp(c, float3(0.16, 0.18, 0.15), dark);

            // The detail: x 0.72..1.28 of the albedo.
            float2 xz = IN.worldPos.xz;
            float fw = length(fwidth(xz));
            float patches = Noise(xz / 6.0);
            float ripples = 0.5 + 0.5 * sin(6.2831853 * (0.8 * x + 0.6 * z) / 0.7 + 3.0 * Noise(xz / 4.0));
            float pebbles = Noise(xz / 0.8);
            float fine = saturate(1.5 - fw / 0.35);
            float mid = saturate(1.5 - fw / 3.0);
            float n = 0.5 + mid * 0.4 * (patches - 0.5) + fine * (0.35 * (ripples - 0.5) + 0.25 * (pebbles - 0.5));
            o.Albedo = c * (0.72 + 0.56 * n);
        }
        ENDCG
    }

    Fallback "Legacy Shaders/Diffuse"
}
