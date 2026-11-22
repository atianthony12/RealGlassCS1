// The view from under the water (RealGlass/Water/WaterUnder.cs draws it, Water/WaterUnderOptics.cs has
// the numbers and their sources; 2026-10-24, its look redone 2026-10-28 after a player's picture): one
// full-screen pass while the camera is at or below the water's level, on a copy of the frame before the
// image effects. Per pixel, the ray through the water to what it ends on -- from the eye when the eye is
// under the level (the game's near plane, a metre or more out, may cross the surface: those rays still
// meet it from below); from the near plane when only that dips in, the waterline across the frame:
//   THE MURK  the image formation of Jaffe 1990 / McGlamery 1975 as revised by Akkaynak & Treibitz
//             (2018, "A Revised Underwater Image Formation Model"): what the ray ends on x e^(-c d) +
//             the veiling light. The water's beam attenuation c per channel (WaterUnderOptics: pure
//             water, dissolved matter, particles), so what lies under the water keeps its shape and
//             takes the water's teal as it recedes. The veiling light is in-scattered daylight, which
//             fades with depth as e^(-K_d z) (K_d after Kirk 1984): integrated along the ray as its
//             depth changes, B0 e^(-K z0) c (1 - e^(-(c - K dy) d)) / (c - K dy) -- the water darker
//             looking down, lighter looking up; brighter toward the sun, as water's scattering is peaked
//             forward (Henyey & Greenstein 1941). B0: the water's own reflectance from those numbers x
//             the daylight x the n-squared law (WaterUnderOptics.Vectors).
//   THE SHAFTS  the sun's share of that veiling light comes only from water the sunbeams reach: the
//             ray sampled in steps (each start jittered per pixel, Jimenez 2014's interleaved gradient
//             noise), each sample's sunbeam traced back up, refracted, to where it entered the surface,
//             and that point tested against the game's own shadow map of the sun (grabbed at the
//             light's AfterShadowMap with its cascades: WaterUnder.cs) -- the light between the shadows
//             of bridges and buildings hangs in the water as shafts (single scattering with volumetric
//             shadows, Toth & Umenhoffer 2009). The share kept is the samples' lit share, weighted as
//             each adds to the veil.
//   THE BED   the game lights the seabed as dry land: x e^(-K_d depth), the daylight that reaches it;
//             the sun's share of it focused by the ripples into caustics -- the area a beam of sunlight
//             through the surface lands on (Watt 1990's beam tracing): 1 / det(I + D (1 - 1/n) H), H the
//             ripple surface's curvature, D the depth (Hessian by central differences of the ripple
//             texture the water uses).
//   THE SURFACE FROM BELOW (redone 2026-10-30 on the author's call: the view looking up fell short) a
//             level plane at the water's height, each ripple a facet: the ripple texture filtered over
//             the pixel's footprint, the slopes that filtering averages away kept as roughness, as the
//             sky layer reckons them from above (LEAN mapping, Olano & Baker 2010) -- the waves show near
//             and smooth out far off. Facets met at less than the critical angle (Snell's window, 48.6 deg
//             of straight up) let the world above through along the refracted ray (the whole sky squeezed
//             into the window, the horizon at its rim; the frame where that ray lands on the screen -- the
//             game culls the water's underside -- the sky on the water's live cube where it does not), x
//             the exact Fresnel transmission x n^2 (the radiance's gain into the denser water: Mobley
//             1994's n-squared law). The averaged-away slopes make the window's rim a band, not a line:
//             the share of facets inside it is Gaussian across it, and beyond it the facets tilted toward
//             the eye still let the sky through -- what makes a choppy surface's underside read as waves.
//             The rest is total internal reflection: the water below along the mirror ray, darker the
//             steeper down (VeilBelow). The sun through the waves: Walter, Marschner, Li & Torrance 2007's
//             microfacet transmission on the same slopes, where the shadow map lets its light reach the
//             surface.
Shader "RealGlass/WaterUnder"
{
    // Gray: no slope, until the mod binds the ripple texture the water is shaded with.
    Properties
    {
        _RGRipples ("", 2D) = "gray" {}
    }

    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "Finite.cginc"

            // The scene before the water: this pass's own copy of the camera's depth texture, taken at
            // AfterFinalPass (pass 2; WaterUnder.cs). The global _CameraDepthTexture this late in the frame
            // is not to be trusted: in the author's game (2026-10-30) it read far too near, and every pixel
            // took the frame as drawn, the city above sharp and lit through the surface.
            UNITY_DECLARE_DEPTH_TEXTURE(_RGUnderDepth);
            // The frame as drawn (a copy).
            sampler2D _RGUnderFrame;
            // The ripple texture the water is shaded with (the game's layout: x in .w, y in .y).
            sampler2D _RGRipples;
            float4 _SimulationTime;
            // The sun's shadow map as the game rendered it this frame, and its cascades' constants
            // (24 texels: unity_WorldToShadow[4] by rows, unity_ShadowSplitSpheres[4],
            // unity_ShadowSplitSqRadii, _LightShadowData, unity_ShadowFadeCenterAndType, _LightSplitsNear).
            UNITY_DECLARE_SHADOWMAP(_RGSunShadow);
            sampler2D _RGSunShadowData;

            // x: the water's level (m), y: the caustics' strength (0 none, 1 real), z: the ripples' slope
            // per decoded unit (the game's shading at rest: 0.25; 0 no ripples), w: unused.
            float4 _RGUnder;
            // rgb: the beam attenuation c (1/m).
            float4 _RGUnderC;
            // rgb: the daylight's diffuse attenuation K_d (1/m).
            float4 _RGUnderK;
            // rgb: the veiling light at the surface, looking level (linear).
            float4 _RGUnderVeil;
            // xyz: toward the sun under the water (refracted; unit, up). w: the sun's share of the
            // daylight just below the surface (0..1; 0 at night or under cloud).
            float4 _RGUnderSun;
            // x: 1 = the sun's shadow map is there this frame (0: no shafts, the sun's light everywhere);
            // y: the steps along a ray; z: how far they reach (m); w: how high over the surface the
            // light's entry is tested (m: the water itself never shades it).
            float4 _RGUnderShafts;
            // The preview's only (the mod never sets it): 1 = the shafts' lit share as grey.
            float _RGUnderDebug;
            // The live sky the water's sky layer reads (WaterSkyLayer: the game's skybox, Volumetric
            // Weather's clouds in it, the game's fog over it).
            samplerCUBE _RGWaterSkyCube;
            // x: 1 = that cube is there (0: the frame alone, held at the screen's edge); y: its size
            // (texels a face); z: its last mip.
            float4 _RGUnderSky;
            // rgb: the sun's light (linear: the game's light, colour x intensity).
            float4 _RGUnderSunLight;
            // The ripples' averaging, as the sky layer has it (WaterSky.shader): x the mip where the
            // shortest wave is averaged away, y the mips from there to the longest, z the ripple texture's
            // texels a side, w their mean square slope at full detail; the FFT waves' table (entry e = mip
            // e - 1: the slopes averaged away, xx zz xz, and their share) and the waves too short for the
            // texture (xyz; w: 1 = the table is in use).
            float4 _RGWaterSkyRipples;
            float4 _RGWaterSkyWaves[12];
            float4 _RGWaterSkyTail;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
                float4 screen : TEXCOORD1;
            };

            // The forward peak of water's scattering of the sun's light (Henyey & Greenstein 1941, g 0.8),
            // averaging 1 over all directions.
            float SunPhase(float3 dir)
            {
                const float g = 0.8;
                float q = 1.0 + g * g - 2.0 * g * dot(_RGUnderSun.xyz, dir);
                return (1.0 - g * g) / (q * sqrt(q));
            }

            // The veiling light's brightness along dir (averaging 1 over all directions): the sun's share
            // scattered forward (lit: the share of it the shafts let through), the rest (the sky's light)
            // evenly -- the water glows toward the sun, darker away from it.
            float VeilToward(float3 dir, float lit)
            {
                float s = _RGUnderSun.w;
                return (1.0 - s) + s * lit * SunPhase(dir);
            }

            // The water's light along dir as a ray reflected down off the surface from below meets it,
            // relative to the veil's level: the sun's share as VeilToward has it; the sky's share, which
            // VeilToward spreads evenly, peaked about straight down as it comes in -- through Snell's window,
            // spread over its cone (the mean cosine 0.84 of an even sky refracted in: Mobley 1994, "Light and
            // Water"), so its lobe is the water's (g 0.8) widened by that spread (g x 0.84: the first
            // moments of two lobes multiply). Looking level it is VeilToward's: the far underside meets the
            // haze seamlessly; steeper down, the deep is darker, which is what lets the waves show there.
            float VeilBelow(float3 dir, float lit)
            {
                const float g = 0.8 * 0.84;
                float q = 1.0 + g * g - 2.0 * g * dir.y;
                float level = (1.0 + g * g) * sqrt(1.0 + g * g);
                float s = _RGUnderSun.w;
                return (1.0 - s) * level / (q * sqrt(q)) + s * lit * SunPhase(dir);
            }

            // A cube round the camera, every face at the far plane (as CityReflection's).
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

            // How the game's two reads of the ripple texture are crossfaded at xz (still water: a 2.5 s
            // cycle, WaterGame.cginc's): 0 all A, 1 all B.
            float RippleBlend(float2 xz)
            {
                float phase = _SimulationTime.x * 0.4 + 0.2 * sin(0.06 * xz.x) * sin(0.06 * xz.y);
                return 2.0 * abs(0.5 - frac(phase));
            }

            // The water surface's slope (dh/dx, dh/dz) at xz from the ripple texture, as the game shades
            // still water (WaterGame.cginc's reads with no flow: A at xz x 0.0061, B at (z, x) x 0.0057
            // with its channels swapped, crossfaded), filtered over the pixel's footprint on the surface
            // (dxz, dyz: m a pixel across and down; 0 = the finest mip) as the GPU filters the game's own.
            float2 RippleSlope(float2 xz, float2 dxz, float2 dyz)
            {
                float blend = RippleBlend(xz);
                float4 a = tex2Dgrad(_RGRipples, xz * 0.0061, dxz * 0.0061, dyz * 0.0061);
                float4 b = tex2Dgrad(_RGRipples, xz.yx * 0.0057, dxz.yx * 0.0057, dyz.yx * 0.0057);
                float2 n2 = (lerp(float2(a.w, a.y), float2(b.y, b.w), blend) * 2.0 - 1.0) * _RGUnder.z;
                // The game's normal is (n2.x, up, -n2.y).
                return float2(-n2.x, n2.y);
            }

            float2 RippleSlope(float2 xz)
            {
                return RippleSlope(xz, float2(0.0, 0.0), float2(0.0, 0.0));
            }

            // The mip (fractional, -1 = magnified) the GPU reads the ripple texture at for a footprint (uv a
            // pixel across and down), as WaterSky.shader's.
            float RippleMip(float2 dx, float2 dy)
            {
                dx *= _RGWaterSkyRipples.z;
                dy *= _RGWaterSkyRipples.z;
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

            // The slopes a pixel's footprint on the surface averages away (xx, zz, xz: the covariance, in
            // RippleSlope's units), as the sky layer reckons them from above (LEAN mapping, Olano & Baker
            // 2010): what the mips took from the two reads, plus the waves too short for the texture.
            float3 AveragedSlopes(float2 xz, float2 dxz, float2 dyz)
            {
                // The game's gain on the texture's slopes (still water: its flow's speed + 0.5), 0 when
                // there are no ripples.
                float gain = 2.0 * _RGUnder.z;
                float3 s;
                if (_RGWaterSkyTail.w > 0.5)
                {
                    float blend = RippleBlend(xz);
                    float4 wa = WavesAt(RippleMip(dxz * 0.0061, dyz * 0.0061));
                    float4 wb = WavesAt(RippleMip(dxz.yx * 0.0057, dyz.yx * 0.0057));
                    float ka = 1.0 - blend, kb = blend;
                    s = gain * gain * (ka * ka * wa.xyz + kb * kb * wb.yxz + _RGWaterSkyTail.xyz);
                }
                else
                {
                    float texels = max(length(dxz), length(dyz)) * 0.0061 * _RGWaterSkyRipples.z;
                    float u = saturate((log2(max(texels, 1e-6)) - _RGWaterSkyRipples.x) / max(_RGWaterSkyRipples.y, 0.5));
                    float each = 0.5 * u * max(_RGWaterSkyRipples.w, 0.0) * gain * 2.0;
                    s = float3(each, each, 0.0);
                }
                return s;
            }

            // Smith's Lambda for Gaussian slopes of covariance s (xx, zz, xz), seen from w (unit, up y), as
            // WaterSky.shader's: (e^(-a^2) / (a sqrt(pi)) - erfc(a)) / 2, a = cos / (sqrt(2 s_w) sin).
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

            // The sun's light let through the facets about the normal n (the texture's, filtered; up)
            // whose slopes spread by s, to the eye below: the microfacet transmission of Walter, Marschner,
            // Li & Torrance 2007 ("Microfacet Models for Refraction through Rough Surfaces", eq. 21) -- the
            // facets that bend the sun's light toward the eye (the half-vector of refraction), their share
            // by the sky layer's Gaussian slopes, Fresnel's transmitted part, n^2 the radiance's gain into
            // the denser water, Smith's masking. v: from the surface toward the eye (unit, down); l: toward
            // the sun in the air (unit, up). Close up the texture's own facets break the sun into a
            // flickering patch; farther, the averaged-away slopes widen it into a glow.
            float3 SunThrough(float3 v, float3 l, float3 n, float3 s)
            {
                const float eta = 1.333;
                float3 h = SafeNormalize(-(l + eta * v), float3(0.0, 1.0, 0.0));
                float zh = max(h.y, 1e-3), zn = max(n.y, 1e-3);
                float2 dd = float2(-h.x, -h.z) / zh - float2(-n.x, -n.z) / zn;
                float sxx = s.x + 1e-5, szz = s.y + 1e-5, sxz = s.z;
                float det = max(sxx * szz - sxz * sxz, 1e-12);
                float q = (szz * dd.x * dd.x - 2.0 * sxz * dd.x * dd.y + sxx * dd.y * dd.y) / det;
                float D = exp(-0.5 * q) / (6.2831853 * sqrt(det) * zh * zh * zh * zh);
                float lh = dot(l, h), vh = dot(v, h);
                float c = 1.0 - saturate(lh);
                float c2 = c * c;
                float fresnel = 0.0204 + 0.9796 * c2 * c2 * c;
                float3 sd = float3(sxx, szz, sxz);
                float masking = 1.0 + SmithLambda(float3(v.x, -v.y, v.z), sd) + SmithLambda(l, sd);
                float jacobian = abs(lh * vh) / max((lh + eta * vh) * (lh + eta * vh), 1e-4);
                float ok = h.y > 0.0 && l.y > 0.0 && lh > 0.0 && vh < 0.0 ? 1.0 : 0.0;
                // x pi: the game's light is Unity's (a white Lambert surface shows it at its colour).
                return _RGUnderSunLight.rgb * (ok * 3.14159265 * jacobian * eta * eta * (1.0 - fresnel) * D / (masking * max(-v.y, 1e-3)));
            }

            // The share of a Gaussian below z (Abramowitz & Stegun 7.1.26's erfc, mirrored for z > 0).
            float Phi(float z)
            {
                float x = abs(z) * 0.70710678;
                float t = 1.0 / (1.0 + 0.3275911 * x);
                float beyond = 0.5 * t * (0.254829592 + t * (-0.284496736 + t * (1.421413741 + t * (-1.453152027 + t * 1.061405429)))) * exp(-x * x);
                return z > 0.0 ? 1.0 - beyond : beyond;
            }

            // Water to air on a facet at incidence cosI: Fresnel's reflected share (unpolarised), and the
            // refracted ray's cosine (0 past the critical angle: all reflected).
            float FresnelUp(float cosI, out float cosT)
            {
                float sinT2 = 1.7769 * (1.0 - cosI * cosI);
                cosT = sqrt(saturate(1.0 - sinT2));
                float rs = (1.333 * cosI - cosT) / max(1.333 * cosI + cosT, 1e-6);
                float rp = (cosI - 1.333 * cosT) / max(cosI + 1.333 * cosT, 1e-6);
                float F = 0.5 * (rs * rs + rp * rp);
                return sinT2 >= 1.0 ? 1.0 : F;
            }

            // The world above along t (unit, up, in the air) as the camera would see it: the frame where t
            // falls on the screen, the live sky cube where it does not (faded over the screen's outer 8%),
            // the cube blurred to its mip lod.
            float3 Above(float3 t, float lod)
            {
                float3 sky = _RGUnderSky.x > 0.5 ? texCUBElod(_RGWaterSkyCube, float4(t, lod)).rgb : float3(0.0, 0.0, 0.0);
                float4 clip = mul(UNITY_MATRIX_VP, float4(_WorldSpaceCameraPos + t * 1000.0, 1.0));
                float4 sp = ComputeScreenPos(clip);
                float2 uv = sp.xy / max(sp.w, 1e-6);
                float2 edge = min(uv, 1.0 - uv);
                float onScreen = clip.w > 0.0 ? saturate(min(edge.x, edge.y) / 0.08) : 0.0;
                float w = _RGUnderSky.x > 0.5 ? onScreen : 1.0;
                float3 frame = tex2Dlod(_RGUnderFrame, float4(saturate(uv), 0.0, 0.0)).rgb;
                return Finite(lerp(sky, frame, w));
            }

            // Sunlight through the surface onto a point D metres below it, xz: the share it is focused
            // to (1 = even; Watt 1990). Blurred by the sun's disc (0.53 deg) and faded to 1 where the
            // pixel spans the pattern (footprint, m).
            float Caustic(float2 xz, float D, float footprint)
            {
                float2 l = _RGUnderSun.xz / max(_RGUnderSun.y, 0.2);
                float2 s = xz + l * D;
                float h = max(0.2, 0.0046 * D);
                float2 gx0 = RippleSlope(s - float2(h, 0.0)), gx1 = RippleSlope(s + float2(h, 0.0));
                float2 gz0 = RippleSlope(s - float2(0.0, h)), gz1 = RippleSlope(s + float2(0.0, h));
                float k = D * 0.25 / (2.0 * h);
                float jxx = 1.0 + k * (gx1.x - gx0.x), jzz = 1.0 + k * (gz1.y - gz0.y);
                float jxz = k * (gz1.x - gz0.x), jzx = k * (gx1.y - gx0.y);
                float det = abs(jxx * jzz - jxz * jzx);
                float c = min(1.0 / max(det, 0.05), 6.0);
                return lerp(c, 1.0, smoothstep(0.1, 0.4, footprint / h));
            }

            float4 ShadowData(int k)
            {
                return tex2Dlod(_RGSunShadowData, float4((k + 0.5) / 24.0, 0.5, 0.0, 0.0));
            }

            // The sun's cascades, read once a pixel: unity_WorldToShadow[4] by rows, the split spheres
            // and their squared radii.
            struct SunCascades
            {
                float4 rows[16];
                float4 spheres[4];
                float4 sq;
            };

            SunCascades ReadCascades()
            {
                SunCascades s;
                [unroll]
                for (int k = 0; k < 16; k++)
                    s.rows[k] = ShadowData(k);
                [unroll]
                for (int j = 0; j < 4; j++)
                    s.spheres[j] = ShadowData(16 + j);
                s.sq = ShadowData(20);
                return s;
            }

            // Whether the sun reaches the world point p (1 lit, 0 shaded): the game's cascades as its
            // own lighting picks them (the split spheres), lit past the last one.
            float SunLit(float3 p, SunCascades s)
            {
                float4 w;
                float3 f0 = p - s.spheres[0].xyz, f1 = p - s.spheres[1].xyz;
                float3 f2 = p - s.spheres[2].xyz, f3 = p - s.spheres[3].xyz;
                w.x = dot(f0, f0) < s.sq.x ? 1.0 : 0.0;
                w.y = dot(f1, f1) < s.sq.y ? 1.0 : 0.0;
                w.z = dot(f2, f2) < s.sq.z ? 1.0 : 0.0;
                w.w = dot(f3, f3) < s.sq.w ? 1.0 : 0.0;
                w.yzw = saturate(w.yzw - w.xyz);
                float inside = dot(w, float4(1.0, 1.0, 1.0, 1.0));
                float4 q = float4(p, 1.0);
                float4 coord = float4(0.0, 0.0, 0.0, 0.0);
                [unroll]
                for (int c = 0; c < 4; c++)
                    coord += w[c] * float4(dot(s.rows[c * 4], q), dot(s.rows[c * 4 + 1], q), dot(s.rows[c * 4 + 2], q), dot(s.rows[c * 4 + 3], q));
                float lit = UNITY_SAMPLE_SHADOW(_RGSunShadow, coord.xyz);
                return lerp(1.0, saturate(lit), inside);
            }

            // The share of the sun's in-scattered light along the ray from p0 (path m, in the water)
            // that the sunbeams reach: each sample's beam traced up to the surface, tested there. The
            // steps closest together near the eye, where the veil is mostly made (t = reach u^2).
            float ShaftShare(float3 p0, float3 d, float path, float level, float2 pixel, SunCascades cascades)
            {
                // Interleaved gradient noise (Jimenez 2014): each pixel's steps start apart.
                float jitter = frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
                float reach = min(path, _RGUnderShafts.z);
                int steps = (int)_RGUnderShafts.y;
                float n = max(_RGUnderShafts.y, 1.0);
                float3 up = _RGUnderSun.xyz / max(_RGUnderSun.y, 0.05);
                float lit = 0.0, all = 0.0;
                [loop]
                for (int i = 0; i < 64; i++)
                {
                    if (i >= steps)
                        break;
                    float u = (i + jitter) / n;
                    float t = reach * u * u;
                    float3 p = p0 + d * t;
                    float z = max(level - p.y, 0.0);
                    // As this sample adds to the veil: through the water back to the eye, by the light
                    // that reaches its depth (green: the eye's), over its share of the way (dt / du).
                    float w = exp(-_RGUnderC.g * t - _RGUnderK.g * z) * u;
                    float3 entry = p + up * z;
                    entry.y = level + _RGUnderShafts.w;
                    lit += w * SunLit(entry, cascades);
                    all += w;
                }
                return all > 0.0 ? lit / all : 1.0;
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 uv = i.screen.xy / i.screen.w;
                float3 frame = Finite(tex2D(_RGUnderFrame, uv).rgb);
                float3 d = SafeNormalize(i.dir, float3(0.0, 0.0, 1.0));
                float3 forward = -UNITY_MATRIX_V[2].xyz;
                float along = max(dot(d, forward), 1e-4);
                float level = _RGUnder.x;
                float3 cam = _WorldSpaceCameraPos;

                // Where the ray would meet the level, and the pixel's footprint there (m a pixel across and
                // down): taken before any pixel leaves, so that every pixel's neighbours are there for it.
                float tLevel = d.y > 1e-5 ? min((level - cam.y) / d.y, 1.0e5) : 1.0e5;
                float2 sxz = cam.xz + d.xz * tLevel;
                float2 dxz = ddx(sxz), dyz = ddy(sxz);

                // Where the ray starts in the water. The eye under the level: at the eye, every ray in the
                // water from there (the game's near plane is a metre or more out: a ray may cross it above
                // the surface, yet it left the eye in the water and meets the surface from below). The eye
                // over it, its near plane dipping in: at the near plane, the rays crossing it above the
                // level the air's, as drawn.
                float start = cam.y < level ? 0.0 : _ProjectionParams.y / along;
                float3 p0 = cam + d * start;
                if (p0.y >= level)
                    return float4(frame, 1.0);

                float raw = SAMPLE_DEPTH_TEXTURE(_RGUnderDepth, uv);
                float eye = LinearEyeDepth(raw);
                bool sky = eye >= 0.999 * _ProjectionParams.z;
                float tScene = sky ? 1.0e6 : eye / along;
                float tSurface = d.y > 1e-5 ? (level - cam.y) / d.y : 1.0e6;

                float3 c = max(_RGUnderC.rgb, 1e-4);
                float3 K = max(_RGUnderK.rgb, 0.0);
                float t = min(min(tSurface, tScene), 1.0e6);
                float path = max(t - start, 0.0);

                // The sun's cascades (zeros until its first capture: all sunlit); its shadow map is read
                // only where it is there this frame.
                SunCascades cascades = ReadCascades();
                bool shadows = _RGUnderShafts.x > 0.5;

                // The shafts: the sun's share of the veil the sunbeams reach along this ray.
                float lit = 1.0;
                if (shadows && _RGUnderSun.w > 0.0)
                    lit = ShaftShare(p0, d, path, level, i.pos.xy, cascades);
                if (_RGUnderDebug > 0.5)
                    return float4(lit, lit, lit, 1.0);

                float3 end;
                if (tSurface < tScene)
                {
                    // THE SURFACE FROM BELOW, each ripple a facet: its normal filtered over the pixel's
                    // footprint, the slopes that filtering averages away kept as roughness (as the sky
                    // layer's from above), so the surface's waves show near and smooth out far off.
                    float3 s = cam + d * tSurface;
                    float2 g = RippleSlope(s.xz, dxz, dyz);
                    float3 n = normalize(float3(-g.x, 1.0, -g.y));
                    float3 rough = AveragedSlopes(s.xz, dxz, dyz);
                    float cosI = saturate(dot(d, n));
                    float3 r = reflect(d, -n);
                    // The water below along the mirror ray (looking down from the level): all of it outside
                    // Snell's window (total internal reflection), Fresnel's share inside.
                    float3 below = _RGUnderVeil.rgb * VeilBelow(r, lit) * c / (c + K * saturate(-r.y));

                    // SNELL'S WINDOW: the facets the ray meets at less than the critical angle (48.6 deg)
                    // let the world above through. The averaged-away slopes tilt the facets about n: along
                    // the ray's way by sigma (rad), so the window's rim is not a line but a band -- the share
                    // of facets inside it (Gaussian) falls from 1 to 0 across it, and those beyond the rim
                    // that still let light through are the ones tilted toward the eye, the rim's own incidence
                    // minus a sigma or so (sigma 0: the filtered facet itself, a sharp rim).
                    float2 way = d.xz / max(length(d.xz), 1e-4);
                    float sigma = sqrt(max(way.x * way.x * rough.x + 2.0 * way.x * way.y * rough.z + way.y * way.y * rough.y, 0.0));
                    float incidence = acos(cosI);
                    const float critical = 0.848062;
                    float inside = Phi((critical - incidence) / max(sigma, 1e-3));
                    float facet = max(min(incidence, critical - sigma), 0.0);
                    // That facet: n turned toward the eye by the difference, in the ray's plane of incidence.
                    float3 e = SafeNormalize(d - cosI * n, float3(way.x, 0.0, way.y));
                    float turn = incidence - facet;
                    float3 m = cos(turn) * n + sin(turn) * e;
                    float3 em = cos(turn) * e - sin(turn) * n;
                    float cosT;
                    float F = FresnelUp(cos(facet), cosT);
                    // Through it: the world above along the refracted ray -- the whole sky squeezed into 48.6
                    // deg of straight up, the horizon at the window's rim -- x the transmitted share x n^2
                    // (the radiance's gain into the denser water: Mobley 1994's n-squared law).
                    float3 through = cosT * m + 1.333 * sin(facet) * em;
                    // The averaged-away slopes spread the refracted rays too (as wide as reflected ones, in
                    // the cube's texels: a face spans pi / 2).
                    float spread = 2.0 * sqrt(0.5 * max(rough.x + rough.y, 0.0));
                    float lod = clamp(log2(max(1.0, spread * _RGUnderSky.y * 0.6366198)), 0.0, max(_RGUnderSky.z, 0.0));
                    float let = inside * (1.0 - F);
                    end = Above(SafeNormalize(through, float3(0.0, 1.0, 0.0)), lod) * (1.7769 * let) + below * (1.0 - let);

                    // The sun through the waves, where its light reaches the surface (the shadow map, a
                    // metre over it, as the shafts test it).
                    float3 toSun = float3(1.333 * _RGUnderSun.x, 0.0, 1.333 * _RGUnderSun.z);
                    toSun.y = sqrt(saturate(1.0 - dot(toSun.xz, toSun.xz)));
                    float sunLit = 1.0;
                    if (shadows)
                        sunLit = SunLit(float3(s.x, level + _RGUnderShafts.w, s.z), cascades);
                    if (_RGUnderSun.w > 0.0)
                        end += Finite(SunThrough(-d, toSun, n, rough)) * sunLit;
                }
                else
                {
                    // Something under the water: lit by the daylight that reaches its depth.
                    float3 p = cam + d * t;
                    float D = max(level - p.y, 0.0);
                    float light = 1.0;
                    // (No "!sky": Unity 5.6's HLSLcc turns a not of a combined bool into never-false.)
                    if (_RGUnder.y > 0.0 && _RGUnderSun.w > 0.0 && tScene < 1.0e5)
                    {
                        // A pixel's width at this distance (m): the frustum's height over the screen's.
                        float footprint = t * 2.0 / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y);
                        float focus = Caustic(p.xz, D, footprint);
                        light = 1.0 + _RGUnderSun.w * _RGUnder.y * (focus - 1.0);
                    }
                    end = sky ? float3(0.0, 0.0, 0.0) : frame * exp(-K * D) * light;
                }

                // Along the way: the veiling light, the daylight fading with depth as the ray climbs or
                // sinks (the start's depth z0, the ray's rise dy).
                float z0 = max(level - p0.y, 0.0);
                float3 q = max(c - K * d.y, 1e-4);
                float3 veil = _RGUnderVeil.rgb * VeilToward(d, lit) * exp(-K * z0) * c * (1.0 - exp(-q * path)) / q;
                float3 colour = end * exp(-c * path) + veil;
                return float4(Finite(colour), 1.0);
            }
            ENDCG
        }

        // 1 SHADOW DATA (a Blit in the sun's AfterShadowMap: WaterUnder.cs): the light's cascade constants
        // as they stand right after its shadow map, one texel each, 24 wide -- unity_WorldToShadow[4] by
        // rows, unity_ShadowSplitSpheres[4], unity_ShadowSplitSqRadii, _LightShadowData,
        // unity_ShadowFadeCenterAndType, _LightSplitsNear (probed in the 5.6 editor: valid there, the
        // frame unchanged).
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 frag(v2f_img i) : SV_Target
            {
                // Unsigned: fxc warns of signed integer divides (a warning fails the bundle build).
                uint k = (uint)max(floor(i.uv.x * 24.0), 0.0);
                float4 v = float4(0.0, 0.0, 0.0, 0.0);
                if (k < 16u)
                    v = unity_WorldToShadow[k / 4u][k % 4u];
                else if (k < 20u)
                    v = unity_ShadowSplitSpheres[k - 16u];
                else if (k == 20u)
                    v = unity_ShadowSplitSqRadii;
                else if (k == 21u)
                    v = _LightShadowData;
                else if (k == 22u)
                    v = unity_ShadowFadeCenterAndType;
                else
                    v = _LightSplitsNear;
                return v;
            }
            ENDCG
        }

        // 2 SCENE DEPTH (a Blit at AfterFinalPass, before the forward water and every image effect:
        // WaterUnder.cs): the camera's depth texture copied raw into _RGUnderDepth, as WaterSurfaceMask's
        // pass 1 copies it for the city reflection.
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_full
            #pragma fragment frag
            #include "UnityCG.cginc"
            #include "FullScreen.cginc"
            UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);

            float4 frag(v2f_full i) : SV_Target
            {
                float raw = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(i.uv, 0.0, 0.0));
                return float4(raw, 0.0, 0.0, 1.0);
            }
            ENDCG
        }
    }

    Fallback Off
}
