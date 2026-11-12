// The sky in the map's water, as real water reflects it (RealGlass/Water/WaterSkyLayer.cs has the
// model and its sources; the author, 2026-10-08, after a Battlefield 6 screenshot of Lake Mead:
// "that's EXACTLY how we want ours to look"). Drawn on the game's own water meshes right after each of
// the game's water draws, with the game's per-patch property block (WaterGame.cginc), as the foam is;
// meanwhile the game's own water reflects no sky (its cube is black while this layer draws:
// WaterSurface).
//
// WHY A LAYER: the game reads its sky cube along each ripple's reflected ray and adds it with no
// Fresnel of its own, so a cube can only carry a reflectance by that ray's height -- which counts each
// ripple's tilt twice (for a view 35 deg down and a facet tilted a toward the eye, the true incidence is
// 55 - a deg, the cube's 55 - 2a): rippled water came out about twice too bright, with the horizon's
// sky in it. Here every pixel knows the eye and its ripple:
//   NORMAL    the game's own (RGGameWaterNormal: its ripples carried along the flow, + the surface's slope).
//   HOW MUCH  Schlick's Fresnel for water on that facet; where the ripple texture's mips have averaged
//             ripples away (a share u of their slopes, from the mip the GPU reads), the rough sea's
//             reflectance (Cox & Munk's slopes: _RGSeaTable) on the filtered normal instead -- what
//             filtering takes from the normals is roughness, not slope (Bruneton, Neyret & Holzschuch
//             2010, "Real-time Realistic Ocean Lighting using Seamless Transitions from Geometry to BRDF").
//   OF WHAT   the sky the player sees: a live cube of the game's skybox, Volumetric Weather's clouds in
//             it (the glass's GlassSky; _RGWaterSkyCube), read along the facet's mirror ray (a ray turned
//             below the level meets the next wave: mirrored back up), as blurred as the averaged-away
//             slopes spread it; towards a published real sky by its weight (RGSkyLight).
//   ONTO      Blend One OneMinusSrcAlpha, premultiplied: the water as the game drew it x (1 - F) + the
//             sky x F -- light that is reflected is not also let through. Queue Geometry+2, before the
//             game's fog: far water hazes as the game's own water does.
//   THE SUN'S GLITTER (2026-10-17, with the FFT waves: WaterSpectrum.cs): the sun's reflection off a
//             sea of facets whose slopes are Gaussian about the ripple texture's own (LEAN mapping,
//             Olano & Baker 2010: the mean from the filtered normal, the covariance from what the
//             mips averaged away -- WaterSpectrum.Table, by the mip the GPU reads each of the game's
//             two reads at -- plus the waves too short for the texture). The microfacet sum for a
//             distant light, E F p(slope of h) / (4 cos v cos^4 h), Smith's masking for Gaussian
//             slopes (Bruneton, Neyret & Holzschuch 2010, after Walter et al. 2007): far away the
//             averaged-away slopes widen it into the glitter path toward a low sun; close up the
//             texture's own facets catch it. Added over the water; the game's own highlight (exponent
//             512 on its normal, x the material's _SpecColor) is turned off meanwhile. SIZE (2026-10-21,
//             after the release: "the glitter area is way too big"): the slopes the glitter sees x a
//             size, its peak kept -- the sun's and the moon's each their own strength and size.
//   REALISTIC DEPTH (1.1.9, the Realistic depth row: the RG_WATER_DEPTH variant; without it the pixel as
//             it always was). The game's water lets its bed through by a hyperbola of the water along the
//             view, c / (3 t + c): a quarter of it still shows through 10 m, 6% through 50 m -- a sand's
//             edge far out on the seabed stays a hard line. While this draws, the game's own bed is taken
//             away by its material (_WaterColorUnder 0: Water/WaterSurface.cs), which leaves it the bed
//             x (1 - s)(1 - O)(1 - A) of its first metre or so (WaterGame.cginc's RGGameBedKept), and this
//             layer adds the bed as real water lets it through, x the rest of that, 1 - (1 - s)(1 - O),
//             and x (1 - A) under the game's own foam: the bed's light falls exponentially with the water
//             it crosses, each colour at its own rate (Beer-Lambert, as Unity's HDRP water, Unreal's Single
//             Layer Water and Crest render it; W. Toman 2009), along Lee, Carder, Mobley, Steward & Patch's
//             (1999) path for a seabed seen from above, H (1 / cos sun' + D_u / cos view'), both angles
//             under the water, D_u 1.05. The rates from two rows (WaterOptics.DepthSigma): Clarity, the
//             depth at which 5% of the bed is left straight down under a high sun, and the underwater
//             colour, whose tint the bed takes at one Clarity; x (1 + the game's dirt). The bed is our own
//             copy of the frame before the water (WaterSurfaceMask, with the scene's depth then), read where
//             the ripple over it bends the eye's ray (a facet of slope s shifts what lies H under it by
//             s H (1 - 1/n), H capped; Gonzalez-Ochoa & Holder 2012 scale the bend by depth) and kept only
//             where what lies there is behind the water and on the screen (Sousa 2005's mask). The sun on
//             the bed: the area a sunbeam through the ripples lands on (Watt 1990's beam tracing, as
//             RealGlass/WaterUnder's), from the ripple texture FILTERED to the pixel -- read at the mip of
//             the pixel's footprint, the difference step no finer than that mip's texel -- so a city camera
//             sees the longer waves' pattern steadily; x the sun's share of the light down there.
Shader "RealGlass/WaterSky"
{
    // _MainTex is the game's (the block sets it per patch); declared so that its _MainTex_ST is the
    // identity the game's own material has, not 0.
    Properties
    {
        _MainTex ("", 2D) = "black" {}
        _WaterNormal ("", 2D) = "bump" {}
        // Only that this shader has REALISTIC DEPTH: the mod looks for it before it takes the game's
        // seabed away (an older bundle on another platform cannot put it back).
        _RGWaterDepthReady ("", Float) = 1
    }

    SubShader
    {
        // Right after the game's water (Geometry+1), before the foam (Geometry+3), the glass and the fog.
        Tags { "Queue" = "Geometry+2" "IgnoreProjector" = "True" "RenderType" = "Transparent" }

        Pass
        {
            ZWrite Off
            // The game's own test (Custom/Water/Default: ZTest LEqual, Cull Back, Offset -3, 0, and it
            // writes depth): drawn on the nearest water only, as the game's water shows. Ours is drawn
            // 2 cm nearer along each eye ray (RGGameLayerClip) with one more unit of slope than the
            // game's: at -3, -2 alone the driver's rounding put ours behind in thin dark dashes, and on
            // far triangles seen edge-on a hair of rounding tilts the slope term itself; the 2 units
            // stay for OpenGL's fixed-point depth. (Until 2026-10-23: ZTest Always + a hand test
            // against the depth texture, which has no water -- every water surface along a ray got the
            // sky, so a tsunami's crest, back and the sea beyond showed through its face.)
            ZTest LEqual
            Offset -4, -2
            Cull Back
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            // REALISTIC DEPTH (the header): the mod's keyword on its material; without it none of it
            // is compiled in.
            #pragma multi_compile __ RG_WATER_DEPTH
            #include "UnityCG.cginc"
            #include "Finite.cginc"
            #include "WaterGame.cginc"
            #include "WaterCommon.cginc"

            // The sky as the player sees it: a live cube of the skybox (WaterSkyLayer, a global).
            samplerCUBE _RGWaterSkyCube;

            // x: the Sky slider (1 = real water); y: the cube's size (texels a face); z: its last mip.
            float4 _RGWaterSky;

            // The ripples' averaging (WaterOptics.SkyRippleMips). x: the mip of the ripple texture where
            // the shortest wave is averaged away, y: the mips from there to the longest, z: the ripple
            // texture's texels a side, w: the ripples' mean square slope at full detail (both directions).
            float4 _RGWaterSkyRipples;

            // The FFT waves' averaging (WaterSpectrum.Table at the ripples' strength, in the texture's
            // slope units): entry e = mip e - 1 of the ripple texture, the slopes averaged away (xx, zz,
            // xz) and their share of all the texture holds.
            float4 _RGWaterSkyWaves[12];

            // xyz: the slopes of the waves too short for the texture (xx, zz, xz, same units); w: 1 =
            // the table above is in use, 0 = the 32 waves' averaging (_RGWaterSkyRipples) as before.
            float4 _RGWaterSkyTail;

            // The glitter of the light the water is lit by (the sun, the moon at night). xyz: toward it
            // (unit); w: its strength (1 = real water, 0 = none).
            float4 _RGWaterSun;

            // rgb: its light, linear (the game's light: colour x intensity). w: the glitter's size, every
            // facet's slope x this (1 = the waves' own), its brightest kept (Water/WaterSkyLayer.cs).
            float4 _RGWaterSunColour;

#if defined(RG_WATER_DEPTH)
            // Realistic depth (the header's REALISTIC DEPTH). The frame as it stood before the water and
            // the scene's depth then, raw: copies taken at the final pass, before the forward water
            // (Water/WaterSurfaceMask.cs), on this material.
            sampler2D _RGWaterBedFrame;
            UNITY_DECLARE_DEPTH_TEXTURE(_RGWaterBedDepth);
            // x: the Distortion row (1 = real water's bend); y: the Sunlight on the seabed row (1 = real,
            // 0 none); w: the preview's mask check (the mod: 0).
            float4 _RGWaterDepth;
            // rgb: what the bed's light loses per metre of its path through the water (WaterOptics.DepthSigma).
            float4 _RGWaterDepthSigma;
            // xyz: toward the sun under the water (refracted; unit, up: y at least the critical angle's
            // 0.66). w: the sun's share of the light on the bed (0 at night).
            float4 _RGWaterDepthSun;

            // The ripples bend the bed no farther than as under this much water (m): deeper beds are dim,
            // and their shift would outrun the screen.
            static const float DepthBendDepth = 4.0;

            // The view's path under the water per metre of it below the eye's ray, x 1 / cos view' (Lee et
            // al. 1999's D_u for the bed's light; WaterOptics.DepthViewPath).
            static const float DepthViewPath = 1.05;

            // The water surface's slope (dh/dx, dh/dz) at xz from the ripple texture at a mip, as the game
            // shades still water (WaterGame.cginc's reads with no flow, x 0.25: the game's gain at rest).
            float2 DepthRippleSlope(float2 xz, float lod)
            {
                float phase = _SimulationTime.x * 0.4 + 0.2 * cos(0.06 * xz.x) * cos(0.06 * xz.y);
                float blend = 2.0 * abs(0.5 - frac(phase));
                float4 a = tex2Dlod(_WaterNormal, float4(xz * 0.0061, 0.0, lod));
                float4 b = tex2Dlod(_WaterNormal, float4(xz.yx * 0.0057, 0.0, lod));
                float2 n2 = (lerp(float2(a.w, a.y), float2(b.y, b.w), blend) * 2.0 - 1.0) * 0.25;
                // The game's normal is (n2.x, up, -n2.y).
                return float2(-n2.x, n2.y);
            }

            // Sunlight through the surface onto a point D metres below it, xz: the share it is focused to
            // (1 = even; Watt 1990 -- the area a beam through the surface lands on, 1 / det(I + D (1 - 1/n)
            // H), H the ripple surface's curvature), capped at 6. The surface filtered to the pixel: read at
            // the mip whose texel is the step h, h no finer than the pixel's footprint (m) or the sun's
            // disc (0.53 deg) at D, so a far pixel sees the longer waves' pattern, not their aliasing.
            float DepthCaustic(float2 xz, float D, float footprint)
            {
                float2 l = _RGWaterDepthSun.xz / max(_RGWaterDepthSun.y, 0.2);
                float2 s = xz + l * D;
                float texel = 1.0 / (0.0061 * max(_RGWaterSkyRipples.z, 1.0));
                float h = max(max(footprint, 0.0046 * D), max(texel, 0.2));
                float lod = log2(h / texel);
                float2 gx0 = DepthRippleSlope(s - float2(h, 0.0), lod), gx1 = DepthRippleSlope(s + float2(h, 0.0), lod);
                float2 gz0 = DepthRippleSlope(s - float2(0.0, h), lod), gz1 = DepthRippleSlope(s + float2(0.0, h), lod);
                float k = D * 0.25 / (2.0 * h);
                float jxx = 1.0 + k * (gx1.x - gx0.x), jzz = 1.0 + k * (gz1.y - gz0.y);
                float jxz = k * (gz1.x - gz0.x), jzx = k * (gx1.y - gx0.y);
                float det = abs(jxx * jzz - jxz * jzx);
                return min(1.0 / max(det, 0.05), 6.0);
            }
#endif

            // The mip (fractional, -1 = magnified) the GPU reads a ripple texture of _RGWaterSkyRipples.z
            // texels at: anisotropic filtering (4 x) takes the longer footprint over up to 4 taps, so the
            // mip is the shorter axis's, or a quarter of the longer; the box it averages over is the
            // footprint itself -- taken here as one square of the same area.
            float RippleMip(float2 uv)
            {
                float2 dx = ddx(uv) * _RGWaterSkyRipples.z, dy = ddy(uv) * _RGWaterSkyRipples.z;
                float major = max(length(dx), length(dy)), minor = min(length(dx), length(dy));
                return 0.5 * log2(max(major * max(major * 0.25, minor), 1e-12));
            }

            float4 WavesAt(float mip)
            {
                float e = clamp(mip + 1.0, 0.0, 11.0);
                float i = floor(e);
                float4 lo = _RGWaterSkyWaves[(int)i];
                float4 hi = _RGWaterSkyWaves[(int)min(i + 1.0, 11.0)];
                return lerp(lo, hi, e - i);
            }

            // Smith's Lambda for Gaussian slopes of covariance s (xx, zz, xz), seen from w (unit, up y):
            // (e^(-a^2) / (a sqrt(pi)) - erfc(a)) / 2, a = cos / (sqrt(2 s_w) sin), s_w the slopes'
            // variance along w's azimuth; erfc by Abramowitz & Stegun 7.1.26.
            float SmithLambda(float3 w, float3 s)
            {
                float2 az = w.xz;
                float across = max(dot(az, az), 1e-8);
                float sw = (az.x * az.x * s.x + 2.0 * az.x * az.y * s.z + az.y * az.y * s.y) / across;
                float a = max(w.y, 1e-4) / sqrt(2.0 * max(sw, 1e-8) * across);
                float t = 1.0 / (1.0 + 0.3275911 * a);
                float erfc = t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429)))) * exp(-a * a);
                return max(0.0, 0.5 * (exp(-a * a) / (a * 1.7724539) - erfc));
            }

            // The sun's light off the facets about the normal n (the texture's, filtered) whose slopes
            // spread by s, toward the eye v. The size k scales every slope -- n's and the spread's -- so
            // the pattern is k times as wide; the distribution's peak (1 / k^2 as high) is put back:
            // smaller, as bright.
            float3 SunGlitter(float3 v, float3 n, float3 s)
            {
                float3 l = _RGWaterSun.xyz;
                float k = clamp(_RGWaterSunColour.w, 0.05, 2.0);
                float k2 = k * k;
                float3 h = SafeNormalize(v + l, float3(0.0, 1.0, 0.0));
                float zv = max(v.y, 1e-3), zh = max(h.y, 1e-3), zn = max(n.y, 1e-3);
                float2 d = float2(-h.x, -h.z) / zh - float2(-n.x, -n.z) * (k / zn);
                float sxx = s.x * k2 + 1e-5, szz = s.y * k2 + 1e-5, sxz = s.z * k2;
                float det = max(sxx * szz - sxz * sxz, 1e-12);
                float q = (szz * d.x * d.x - 2.0 * sxz * d.x * d.y + sxx * d.y * d.y) / det;
                float p = exp(-0.5 * q) / (6.2831853 * sqrt(det));
                float c = 1.0 - saturate(dot(v, h));
                float c2 = c * c;
                float fresnel = 0.0204 + 0.9796 * c2 * c2 * c;
                float3 sd = float3(sxx, szz, sxz);
                float masking = 1.0 + SmithLambda(v, sd) + SmithLambda(l, sd);
                float lit = l.y > 0.0 ? 1.0 : 0.0;
                // x pi: the game's light is Unity's (a white Lambert surface shows it at its colour).
                return _RGWaterSunColour.rgb * (lit * 3.14159265 * fresnel * p * k2 / (4.0 * zv * zh * zh * zh * zh * masking));
            }

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                // The water's height (m): the game's pixel program compares the terrain with it.
                float water : TEXCOORD1;
#if defined(RG_WATER_DEPTH)
                // The game's water's own ComputeScreenPos (not the layer's 2 cm nearer): its place on the
                // screen xy / w, w its eye depth (m).
                float4 screen : TEXCOORD2;
#endif
            };

            v2f vert(appdata_base v)
            {
                RGGameWaterVertex g = RGGameWaterAt(v.vertex);
                v2f o;
                o.world = g.world;
                o.pos = RGGameLayerClip(g.world);
                o.water = g.waterHeight * _MainTex_ST.x + _MainTex_ST.z;
#if defined(RG_WATER_DEPTH)
                o.screen = ComputeScreenPos(mul(UNITY_MATRIX_VP, float4(g.world, 1.0)));
#endif
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 world = i.world;
                float2 uv = RGGameSurfaceUV(world);
                float4 sa, sb;
                RGGameSurface(uv, sa, sb);

                // The game's own test for water here; dropped at the very end, after every read that
                // needs its neighbours.
                float depth = i.water - RGGameTerrainPixel(uv);
                float wet = min(depth, sb.y - 0.001);

                RGGameRippleReads reads = RGGameRipplePlaces(world.xz, sb);
                float3 n = RGGameWaterNormalAt(reads, sa);
                // The slopes this pixel averages away (shaded: x (speed + 0.5) as the game shades the
                // texture's), and their share of all the ripples' slope.
                float u;
                float3 averaged;
                float gain = reads.speed + 0.5;
                if (_RGWaterSkyTail.w > 0.5)
                {
                    // The FFT waves: each of the game's two reads at its own mip, B transposed (its slopes
                    // are the texture's swapped and negated), weighted as the game crossfades them.
                    float4 wa = WavesAt(RippleMip(reads.uvA));
                    float4 wb = WavesAt(RippleMip(reads.uvB));
                    float ka = 1.0 - reads.blend, kb = reads.blend;
                    averaged = gain * gain * (ka * ka * wa.xyz + kb * kb * wb.yxz + _RGWaterSkyTail.xyz);
                    u = lerp(wa.w, wb.w, reads.blend);
                }
                else
                {
                    // The 32 waves: how much of their slope the mip the GPU reads the first copy at has
                    // averaged away, by the pixel's longer footprint (anisotropic filtering keeps the other).
                    float texels = max(length(ddx(reads.uvA)), length(ddy(reads.uvA))) * _RGWaterSkyRipples.z;
                    u = saturate((log2(max(texels, 1e-6)) - _RGWaterSkyRipples.x) / max(_RGWaterSkyRipples.y, 0.5));
                    float each = 0.5 * u * max(_RGWaterSkyRipples.w, 0.0);
                    averaged = float3(each, each, 0.0);
                }

                float3 v = SafeNormalize(_WorldSpaceCameraPos - world, float3(0.0, 1.0, 0.0));
                float cosI = saturate(dot(v, n));
                float c = 1.0 - cosI;
                float c2 = c * c;
                float schlick = 0.0204 + 0.9796 * c2 * c2 * c;
                float reflectance = lerp(schlick, RGSeaRho(cosI), u);

                float3 r = reflect(-v, n);
                r.y = abs(r.y);
                // The averaged-away slopes spread the reflected rays by 2 sigma in each plane: as wide in
                // the cube's texels (a face spans pi / 2).
                float spread = 2.0 * sqrt(0.5 * max(averaged.x + averaged.y, 0.0));
                float lod = clamp(log2(max(1.0, spread * _RGWaterSky.y * 0.6366198)), 0.0, max(_RGWaterSky.z, 0.0));
                float3 sky = Finite(RGSkyLight(texCUBElod(_RGWaterSkyCube, float4(r, lod)).rgb, r));

                float a = FiniteAlpha(reflectance * max(_RGWaterSky.x, 0.0));
                float3 glitter = _RGWaterSun.w > 0.0 ? Finite(SunGlitter(v, n, averaged) * _RGWaterSun.w) : float3(0.0, 0.0, 0.0);

#if defined(RG_WATER_DEPTH)
                // REALISTIC DEPTH (the header): the bed as real water lets it through, where the game's
                // own (taken away by its material) has left off.
                // A pixel's size on the water (m) and the game's foam texture here, outside any branch
                // (derivatives).
                float footprint = max(length(ddx(world)), length(ddy(world)));
                float2 foamTex = RGGameFoamTexture(reads);
                float dirt = RGGameDirt(sa, foamTex.y);

                float2 uv0 = i.screen.xy / i.screen.w;
                float eye = max(i.screen.w, 1e-3);
                float scene0 = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_RGWaterBedDepth, float4(uv0, 0.0, 0.0)));
                // The bed under this pixel: on the eye's ray, where the frame holds it.
                float3 ray = world - _WorldSpaceCameraPos;
                float3 p0 = _WorldSpaceCameraPos + ray * (scene0 / eye);
                float h0 = max(world.y - p0.y, 0.0);

                // Where this ripple bends the eye's ray to: the bed's point shifted by the facet's slope,
                // on the screen. Kept only when what lies there is behind the water and on the screen
                // (as numbers: HLSLcc's "not" of a combined test is never false).
                float2 shift = -n.xz / max(n.y, 0.2) * (0.25 * min(h0, DepthBendDepth) * max(_RGWaterDepth.x, 0.0));
                float4 s1 = ComputeScreenPos(mul(UNITY_MATRIX_VP, float4(p0 + float3(shift.x, 0.0, shift.y), 1.0)));
                float2 uv1 = s1.xy / max(s1.w, 1e-3);
                float scene1 = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_RGWaterBedDepth, float4(uv1, 0.0, 0.0)));
                float ok = step(eye, scene1) * step(0.0, min(uv1.x, uv1.y)) * step(max(uv1.x, uv1.y), 1.0);
                float3 bed = Finite(tex2Dlod(_RGWaterBedFrame, float4(lerp(uv0, uv1, ok), 0.0, 0.0)).rgb);
                float3 p = lerp(p0, _WorldSpaceCameraPos + ray * (scene1 / eye) + float3(shift.x, 0.0, shift.y), ok);
                float h = max(world.y - p.y, 0.0);

                // The light's path through the water: down from the sun, up along the view's ray
                // refracted into the water (cos view' from Snell, n = 1.333).
                float cosView = sqrt(max(1.0 - (1.0 - v.y * v.y) * 0.5628, 1e-4));
                float path = h * (1.0 / max(_RGWaterDepthSun.y, 0.66) + DepthViewPath / cosView);
                float3 through = exp(-max(_RGWaterDepthSigma.rgb, 0.0) * ((1.0 + dirt) * path));

                // The sun's share of the bed's light focused or spread by the ripples; above 100% the
                // pattern's contrast raised (c to that power), never below none of the sun's.
                float lit = 1.0;
                if (_RGWaterDepth.y > 0.0 && _RGWaterDepthSun.w > 0.0)
                {
                    float focus = DepthCaustic(p.xz, h, footprint);
                    lit = 1.0 + _RGWaterDepthSun.w * (min(exp2(_RGWaterDepth.y * log2(max(focus, 1e-3))), 8.0) - 1.0);
                }

                // Where the game's own bed left off, and not under the game's own foam.
                float opacity = RGGameOpacity(i.screen, scene0, dirt);
                float foam = RGGameFoamCover(foamTex.x, reads.speed, RGGameRain(reads), opacity);
                float3 seen = Finite(bed * through * (lit * (1.0 - RGGameBedKept(opacity)) * (1.0 - foam)));
#endif

                clip(wet);
#if defined(RG_WATER_DEPTH)
                // The preview's check of the two masks: where this layer draws (the mod never sets it).
                if (_RGWaterDepth.w > 0.5)
                    return float4(0.0, 1000.0, 0.0, 0.0);
#endif
                float4 colour = float4(sky * a + glitter, a);
#if defined(RG_WATER_DEPTH)
                // Under the reflection: light that is reflected is not also let through.
                colour.rgb += seen * (1.0 - a);
#endif
                return colour;
            }
            ENDCG
        }
    }

    Fallback Off
}
