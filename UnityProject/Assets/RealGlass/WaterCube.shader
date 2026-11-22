// The sky the game's water reflects, with real water's reflectance (RealGlass/Water/WaterSurface.cs;
// ported from RealLight's SkyCube, 2026-10-03). Drawn as a hidden camera's skybox into a small cube
// (Camera.RenderToCubemap) that takes the place of the theme's _EnvironmentCubemap on the game's two
// water materials only. The game's water shader adds that cube x 0.5 at every angle, lit by the sun
// and the ambient (no Fresnel: a white cubemap gives white water). So the cube carries the law
// itself: along a reflected direction d the THEME'S OWN cube x rho(max(d.y, 0)) (2% looking down,
// about half at grazing; a ray below the level meets the next wave: the grazing value) x the gain,
// which divides out the shader's 0.5 and carries the Sky slider. The game's lighting is kept.
//
// THE MATCHED MAP (_RGWaterGain.w = 1; Glass/GlassSky.cs, 2026-11-20): the same skybox draws, for the
// water's per-ripple sky layer, the theme's cube as it is (no reflectance, no lighting: that layer
// adds its own Fresnel) x ONE gain: the live sky's mean luminance over the upper sky divided by the
// map's, both read at a coarse mip along the same nine directions. A cubemap mod's photograph (an
// 8-bit picture, the same at noon and at night) so takes the live sky's brightness, which follows the
// time of day and the weather. The gain is made here, on the GPU, every face: nothing is read back.
Shader "RealGlass/WaterCube"
{
    SubShader
    {
        Tags { "Queue" = "Background" "RenderType" = "Background" "PreviewType" = "Skybox" }

        Pass
        {
            ZWrite Off
            Cull Off

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            #include "WaterCommon.cginc"

            // The theme's cube, as the game set it (the global _EnvironmentCubemap).
            samplerCUBE _RGWaterTheme;

            // x: the Sky slider / 0.5; y: 1 when there is a theme's cube; z: the theme cube's mip
            // whose texels are this cube's; w: 1 for THE MATCHED MAP (else 0).
            float4 _RGWaterGain;

            // THE MATCHED MAP: the live sky (its cube), and x its coarse mip, y the theme cube's.
            samplerCUBE _RGWaterMatchRef;
            float4 _RGWaterMatch;

            // Mean luminance over the upper sky: the zenith and two rings of four (12 and 45 degrees up),
            // the second turned 45 degrees -- where the water's reflections look.
            float UpperSkyLuminance(samplerCUBE cube, float lod)
            {
                const float3 lum = float3(0.2126, 0.7152, 0.0722);
                float c1 = 0.9781, s1 = 0.2079;          // 12 degrees
                float c2 = 0.7071, s2 = 0.7071;          // 45 degrees
                float d2 = c2 * 0.7071;
                float sum = dot(Finite(texCUBElod(cube, float4(0.0, 1.0, 0.0, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(c1, s1, 0.0, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(-c1, s1, 0.0, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(0.0, s1, c1, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(0.0, s1, -c1, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(d2, s2, d2, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(-d2, s2, d2, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(d2, s2, -d2, lod)).rgb), lum);
                sum += dot(Finite(texCUBElod(cube, float4(-d2, s2, -d2, lod)).rgb), lum);
                return max(sum / 9.0, 0.0);
            }

            // rgb: the light the game's water lights its cube with (linear: the sun on the level +
            // the sky's ambient, floored). A real sky is light already: it is divided by this, so
            // that the game's lighting gives it back.
            float4 _RGWaterLight;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert(float4 vertex : POSITION)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(vertex);
                o.dir = vertex.xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 d = normalize(i.dir);
                float3 theme = _RGWaterGain.y > 0.5 ? texCUBElod(_RGWaterTheme, float4(d, _RGWaterGain.z)).rgb : float3(0.0, 0.0, 0.0);
                // In light (the theme's cube as the game lights it), then back to what the game lights.
                float3 sky = RGSkyLight(theme * _RGWaterLight.rgb, d) / _RGWaterLight.rgb;
                float rho = RGSeaRho(max(d.y, 0.0));
                float3 legacy = sky * (rho * _RGWaterGain.x);

                // THE MATCHED MAP: the theme's cube as it is, x the live sky's brightness over its own.
                float live = UpperSkyLuminance(_RGWaterMatchRef, _RGWaterMatch.x);
                float photo = UpperSkyLuminance(_RGWaterTheme, _RGWaterMatch.y);
                // At most x16: a map with a near-black upper sky (a night photograph) under a bright live sky
                // would otherwise multiply its few bright texels (a sun, lamps) by thousands.
                float gain = min(live / max(photo, 1e-4), 16.0);
                float3 matched = theme * gain;

                return float4(Finite(_RGWaterGain.w > 0.5 ? matched : legacy), 1.0);
            }
            ENDCG
        }
    }
}
