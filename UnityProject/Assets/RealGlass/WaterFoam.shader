// Foam on the map's water (RealGlass/Water/WaterFoam.cs has the model and its sources). Drawn on the
// game's own water meshes, right after each of the game's water draws, with the game's property
// block (WaterGame.cginc): the vertices land where the game's water is, and every input is the
// game's simulation as the game's own shader reads it. The game's water is drawn untouched first;
// this only adds foam, nothing anywhere else.
//
// IN LAYERS (2026-10-24, the author: "In real life, the foam is often layered"), as foam ages
// (Monahan & Lu 1990's stages of a whitecap; FoamOptics' numbers):
//   FRESH    stage A, made by a crest breaking now: dense, the brightest (Koepke 1984), patch-heavy
//            sheets with small burst-bubble holes, lit as if it had volume (a normal from its
//            thickness: Crest's "foam 3D lighting"); gone in about a second.
//   RESIDUAL stage B, the sheet left behind: lace-heavy (its holes grow), thinner (the water shows
//            through), dimmer, its lace torn into streaks (domain warping); tens of seconds (Potter
//            et al. 2015), so the foam of the last few waves lies one over the other.
//   BUBBLES  under both, the bubbles the break pushed down: the water's own colour made paler (the
//            body reflectance f b_b / (a + b_b) with the bubbles' backscatter added: Gordon et al.
//            1975, Morel & Prieur 1977), seen a little under the surface (a parallax offset along the
//            refracted eye ray: Crest's "foam bubbles") through the water above them (pure water's
//            absorption, Pope & Fry 1997), under the sky's reflection (x 1 - Fresnel).
// THE AMOUNT (the Amount row) is the share of the model's foam, as it always was; each layer keeps
// its own life. A breaking crest and the swash stay solid white from a third of it up.
//
// HOW MUCH FOAM, and HOW OLD, from three sources, combined as independent chances:
//   SHORE    the surf, a band of the sea along the waterline measured in DISTANCE from it (War
//            Thunder phases its shore waves by the distance to the shore): the distance is the depth
//            over the seabed's slope round the point (the game's coasts drop steeply -- in the
//            author's city under 6% of the shore's water is shallower than 2 m -- so a band of depth
//            alone was a single thin line, 2026-10-24) -- kept within a corridor round the real distance
//            from the waterline (ShoreDistance's map), as on a bay's flat floor the slope nears 0 and that
//            measure ran away, closing the band into rings (2026-10-25). Broken waves (bores) every few seconds, the
//            bigger breaking farther out (McCowan 1894: as deep as their height allows), running in at
//            the shallow-water speed sqrt(g d) of a gently shelving beach -- slowing and bunching
//            toward the waterline -- in sets of strong and weak ones (wave groups). A point's age is
//            the time since the last bore passed it: a bright crest line, the residual sheet and the
//            bubbles fading behind it, the last bores' foam under the next. Farther out the same
//            crests are the swell coming in, breaking here and there before they reach the surf
//            (spilling): short streaks of white, fewer the farther out (the author's picture of such
//            streaks, 2026-10-24). At the sea's level only.
//            ITS EDGES (2026-11-11, the author's report: pointy in places): the depth and the slope are
//            a SMOOTH seabed's (a cubic B-spline through the terrain's heights: the game's terrain is
//            creased along its grid, and every line of the surf had a corner at every crease); the
//            swash has an edge of its own, at the smooth waterline as a bore arrives and draining back
//            in lobes after, white along its front (a white band used to lie on the game's waterline,
//            which is a polygon); a streak's strength comes up over a fixed distance inside its end
//            (it used to run out in a spear's tip).
//   FAST     where the flow is fast (rapids, weirs, dam outflows), more where the surface is steep;
//            the core fresh, the fringe old.
//   FRONTS   where the water is rising fast on a steep face: the front of a tsunami, a flood or a dam
//            break, fresh (Chentanez & Mueller 2010's breaking test: fast and steep -- the game's calm
//            sea rises and falls up to a metre a water frame everywhere, but gently); and on its back,
//            where the water falls again behind the crest, the residual sheet and the bubbles.
// WHERE it is white: the foam pattern (FoamPattern.cs), thresholded at the level that leaves that
// share above it; carried along the flow in two phases half a cycle apart and crossfaded (Vlachos
// 2010, the game's own way with its ripples).
// FILTERED so it does not sparkle: where a pixel spans more than the pattern's features, the
// threshold turns into a ramp as wide as the variation averaged away, and far away the pixel shows
// exactly the share it should (the amount itself).
Shader "RealGlass/WaterFoam"
{
    // _MainTex is the game's (the block sets it per patch); declared so that its _MainTex_ST is the
    // identity the game's own material has, not 0.
    Properties
    {
        _MainTex ("", 2D) = "black" {}
        _RGFoamPattern ("", 2D) = "gray" {}
        _RGShoreDistance ("", 2D) = "gray" {}
        _RGShorePaint ("", 2D) = "gray" {}
    }

    SubShader
    {
        // Right after the game's water (Geometry+1) and the sky on it (RealGlass/WaterSky, Geometry+2):
        // foam is on top of the water, reflecting nothing; before the glass and the game's fog.
        Tags { "Queue" = "Geometry+3" "IgnoreProjector" = "True" "RenderType" = "Transparent" }

        Pass
        {
            Tags { "LightMode" = "ForwardBase" }
            ZWrite Off
            // The game's own test (Custom/Water/Default: ZTest LEqual, Cull Back, Offset -3, 0, and it
            // writes depth): foam on the nearest water only, drawn as the sky layer is -- 2 cm nearer
            // along each eye ray (RGGameLayerClip) and one more unit of slope than the game's, against
            // the driver's rounding (WaterSky.shader has the why); the 2 units stay for OpenGL's
            // fixed-point depth. (The first in-game run, at plain LEqual, showed
            // only the strip the game's water leaves out: its Offset -3 puts it in front. Until
            // 2026-10-23: ZTest Always + a hand test against the depth texture, which has no water -- a
            // tsunami's far side showed through.)
            ZTest LEqual
            Offset -4, -2
            Cull Back
            // Premultiplied: the bubbles and the white composited in the shader.
            Blend One OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase nolightmap nodirlightmap nodynlightmap novertexlight
            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"
            #include "Finite.cginc"
            #include "WaterGame.cginc"

            sampler2D _RGFoamPattern;

            // x: the lace's repeats a metre, y: the patches' repeats a metre, z: the pattern's texels a
            // side, w: how far inside its end a streak beyond the surf is at its full strength (m)
            float4 _RGFoamScale;
            // x: the lace's features and y: the patches' (texels; log2), z: the width of the swash's own
            // edge, from no foam to all of it (m), w: how long a bore's run up the beach takes to drain
            // back (s)
            float4 _RGFoamFeature;
            // Shore foam. x: its amount at the waterline (0 = none), y: the depth it reaches (m),
            // z: the sea's level (m), w: how far from it the water still counts as the sea (m)
            float4 _RGFoamShore;
            // Fast water. x: its amount, y: the flow speed it starts at, z: where it is full (the game's
            // units), w: how much a steep surface adds (x its slope)
            float4 _RGFoamFast;
            // Fronts. x: their amount, y: the rise a water frame it starts at, z: where it is full (m);
            // w: the depth of water the surf's foam thins away in at the water's edge (m)
            float4 _RGFoamFront;
            // x: the foam's reflectance, y: the flow's speed on the water at 1 (m/s), z: the advection's
            // cycle (s), w: the strength of it all (0..)
            float4 _RGFoamLook;
            // A diagnostic (the hidden WaterFoamView row; 0 normally): x = 1 shows each source's amount
            // as a colour (red shore, green fast water, blue fronts) on every wet pixel;
            // 2 magenta on every pixel drawn (past the depth test), before the game's no-water test; 3
            // the same after it; 4 the layers (red fresh, green residual, blue the bubbles); 5 the surf's
            // distance from the waterline as lines.
            // y = 1 while the shore paint tool is out: the paint view (PaintView, below) over the foam.
            float4 _RGFoamDebug;

            // The layers (FoamOptics.LayerVectors).
            // x, y, z: how long fresh foam, the residual sheet and the bubbles last (s); w: a fresh
            // sheet's reflectance
            float4 _RGFoamLayers;
            // x: an old sheet's reflectance, y: how much of the water it hides, z, w: the share of a
            // fresh and of an old sheet in burst-bubble holes
            float4 _RGFoamSheet;
            // x: Morel & Prieur's f, y: the bubbles' backscattering over the water's own at their
            // densest, z: how deep they are seen (m), w: how much of the water they hide at most
            float4 _RGFoamBubbles;
            // The surf. x: a bore every this many s, y: the slope of the beach the bores run in at the
            // pace of, z: bores in a set, w: the swash's width (m from the waterline)
            float4 _RGFoamSurf;
            // x: the bubbles' repeats a metre, y: the bubble clouds', z, w: their features (texels; log2)
            float4 _RGFoamScale2;
            // x: a fresh sheet's thickness for its lighting (m), y: the bubbles' spread over the white,
            // z: the bubble clouds' edge, w: the surf's width (m from the waterline)
            float4 _RGFoamExtra;
            // x: how long a bore's crest stays nearly solid (s), y: half the distance between the points
            // the smooth seabed runs through (m), z: the deepest water the surf reaches (m), w: the least slope
            float4 _RGFoamCrest;
            // rgb: the water's colour (linear, as the game's water is drawn with it); w: -
            float4 _RGFoamWater;
            // rgb: pure water's absorption in red, green and blue (per m); w: -
            float4 _RGFoamAbsorb;
            // Outside the surf. x: how far the swell's crests break (x the surf's width), y: in water no
            // deeper than (m), z: how long a streak's foam lasts (s), w: the noise's blotches (m)
            float4 _RGFoamOuter;
            // Fronts: the surface's slope their foam starts at (x) and is full at (y); the surf's corridor
            // round the real distance from the waterline: z its factor, w its slack (m)
            float4 _RGFoamSteep;
            // The real distance from the sea's waterline (ShoreDistance; RealGlass/Water/ShoreMap.cs): a
            // byte a sample, 128 at the waterline, the sea above, inland below
            sampler2D _RGShoreDistance;
            // x, y: the map's uv = world xz x + y; z: m a step of its byte; w: 1 once the map is up (0: the
            // depth over the slope alone)
            float4 _RGShoreMapping;
            // Where the player painted the surf in or out (RealGlass/Water/ShorePaint*.cs): a byte a sample
            // on the shore map's grid, 128 + 127 x the paint (-1 no surf .. +1 surf; 128 automatic)
            sampler2D _RGShorePaint;
            // x, y: the map's uv = world xz x + y; w: 1 once anything is painted (0: no paint read)
            float4 _RGShorePaintMapping;

            // The paint at a point, -1 .. +1 (0 automatic).
            float ShorePaintAt(float2 xz)
            {
                if (_RGShorePaintMapping.w < 0.5)
                    return 0.0;
                float2 uv = xz * _RGShorePaintMapping.x + _RGShorePaintMapping.y;
                return clamp((tex2Dlod(_RGShorePaint, float4(uv, 0.0, 0.0)).a * 255.0 - 128.0) / 127.0, -1.0, 1.0);
            }

            // The real distance from the sea's waterline at a point (m; negative inland), 0 until the map is up.
            float ShoreMetres(float2 xz)
            {
                float2 uv = xz * _RGShoreMapping.x + _RGShoreMapping.y;
                return (tex2Dlod(_RGShoreDistance, float4(uv, 0.0, 0.0)).a * 255.0 - 128.0) * _RGShoreMapping.z;
            }

            // The paint view, while the shore paint tool is out: the water's layers as colours over the
            // foam -- the paint (green: surf painted in, red: painted out), where the surf can reach (a
            // cyan wash) and its band's edge (a cyan line), the sea's waterline (a white line), and each
            // source's foam (cyan white the surf, orange fast water, violet fronts). Premultiplied.
            float4 PaintView(float4 foam, float paint, float reach, float x, float surf, float2 xz,
                             float shoreW, float fastW, float frontW)
            {
                float3 c = foam.rgb;
                float a = foam.a;
                // The layers, each laid over the one before.
                float4 layers[6];
                layers[0] = float4(0.10, 0.65, 0.75, 0.22 * reach);
                float edge = reach * (1.0 - smoothstep(0.0, 3.0, abs(x - surf)));
                layers[1] = float4(0.30, 0.95, 1.00, 0.75 * edge);
                float d = _RGShoreMapping.w > 0.5 ? ShoreMetres(xz) : 1000.0;
                layers[2] = float4(1.00, 1.00, 1.00, 0.85 * (1.0 - smoothstep(2.0, 5.0, abs(d))));
                layers[3] = float4(0.20, 0.95, 0.35, 0.50 * saturate(paint));
                layers[4] = float4(1.00, 0.25, 0.20, 0.50 * saturate(-paint));
                float fastOnly = saturate(fastW - shoreW);
                layers[5] = float4(1.00, 0.55, 0.10, 0.80 * saturate(max(fastOnly, frontW)));
                if (frontW > fastOnly)
                    layers[5].rgb = float3(0.70, 0.35, 1.00);
                for (int k = 0; k < 6; k++)
                {
                    float ak = saturate(layers[k].a);
                    c = c * (1.0 - ak) + layers[k].rgb * ak;
                    a = a * (1.0 - ak) + ak;
                }
                return float4(c, a);
            }

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 world : TEXCOORD0;
                // x: the water's height (m; the game's pixel program compares the terrain with it),
                // y: its rise from the older frame to the newer (m)
                float2 water : TEXCOORD1;
                SHADOW_COORDS(2)
            };

            v2f vert(appdata_base v)
            {
                RGGameWaterVertex g = RGGameWaterAt(v.vertex);
                v2f o;
                o.world = g.world;
                o.pos = RGGameLayerClip(g.world);
                o.water = float2(g.waterHeight * _MainTex_ST.x + _MainTex_ST.z, g.heights.y - g.heights.x);
                TRANSFER_SHADOW(o);
                return o;
            }

            // The threshold that leaves a share a of w G + (1 - w) R above it (G, R uniform on 0..1):
            // the inverse of the distribution of the sum, a trapezoid.
            float FoamThreshold(float a, float w)
            {
                float hi = max(w, 1.0 - w), lo = min(w, 1.0 - w);
                float p = 1.0 - saturate(a);
                float edge = lo / (2.0 * hi);
                if (p < edge)
                    return sqrt(2.0 * hi * lo * p);
                if (p > 1.0 - edge)
                    return 1.0 - sqrt(2.0 * hi * lo * (1.0 - p));
                return hi * p + 0.5 * lo;
            }

            // How far a channel is averaged away at this pixel: 0 while a texel is no bigger than the
            // pixel, 1 once the pixel spans a whole feature (footprint and feature in texels, log2).
            float Averaged(float2 uv, float feature)
            {
                float2 dx = ddx(uv) * _RGFoamScale.z, dy = ddy(uv) * _RGFoamScale.z;
                float texels = max(sqrt(max(dot(dx, dx), dot(dy, dy))), 1.0);
                return saturate(log2(texels) / max(feature, 1.0));
            }

            // The white share at one phase: patches and lace (patch weight w) against the threshold t
            // for amount a, less the burst-bubble holes (share h). thick: how far inside the sheet
            // (0 at its edge, 1 well in; 0 where the lace is averaged away).
            float Coverage(float2 xz, float a, float t, float w, float h, out float thick)
            {
                // The lace's cells torn about: its place warped by two noises a few metres across
                // (domain warping), so the bubble walls run in streaks and swirls, not a net of cells.
                float2 warp = tex2D(_RGFoamPattern, xz * (1.0 / 9.0)).ga - 0.5;
                float2 uvLace = (xz + warp * 2.2) * _RGFoamScale.x;
                float2 uvPatch = xz * _RGFoamScale.y;
                float2 uvHole = xz * _RGFoamScale2.x;
                float lace = tex2D(_RGFoamPattern, uvLace).r;
                float patch = tex2D(_RGFoamPattern, uvPatch).g;
                float hole = tex2D(_RGFoamPattern, uvHole).b;
                float rLace = Averaged(uvLace, _RGFoamFeature.x);
                float rPatch = Averaged(uvPatch, _RGFoamFeature.y);
                float rHole = Averaged(uvHole, _RGFoamScale2.z);

                float field = w * patch + (1.0 - w) * lace;
                // Where the lace is averaged away its share of the field is spread evenly round the
                // patches' value: the share above t is then a ramp half that wide. Plus the pixel's own
                // change across it (the glass's anti-aliased threshold, 0.75 fwidth) -- of the field
                // AND of the threshold, which moves with the amount (a patch of foam's edge). Where the
                // patches too are averaged away, the amount itself.
                float e = 0.5 * (1.0 - w) * rLace + 0.75 * fwidth(field - t) + 1e-3;
                float c = saturate((field - t + e) / (2.0 * e));
                thick = saturate((field - t) / 0.12) * (1.0 - rLace);
                // The holes: the share h of the bubble channel at its top, its own ramp; averaged
                // away, exactly h of the sheet.
                float eh = 0.75 * fwidth(hole) + 0.02;
                float holes = lerp(saturate((hole - (1.0 - h) + eh) / (2.0 * eh)), h, rHole);
                return lerp(c, a, rPatch) * (1.0 - holes);
            }

            // The bubble clouds' share at one phase, read deeper along the eye ray (under).
            float Clouds(float2 xz, float b)
            {
                float2 uv = xz * _RGFoamScale2.y;
                float v = tex2D(_RGFoamPattern, uv).a;
                float r = Averaged(uv, _RGFoamScale2.w);
                float e = _RGFoamExtra.z + 0.75 * fwidth(v);
                float c = saturate((v - (1.0 - b) + e) / (2.0 * e));
                return lerp(c, b, r);
            }

            // The world gradient (per m) of a value from its screen derivatives and the point's: the
            // 2 x 2 system solved, 0 where it is degenerate.
            float2 WorldGradient(float f, float2 xz)
            {
                float2 dx = ddx(xz), dy = ddy(xz);
                float fx = ddx(f), fy = ddy(f);
                float det = dx.x * dy.y - dx.y * dy.x;
                float inv = det / (det * det + 1e-12);
                return float2(dy.y * fx - dx.y * fy, dx.x * fy - dy.x * fx) * inv;
            }

            // The seabed for the surf's lines, SMOOTH: a cubic B-spline through the terrain's heights at
            // points 2 x span apart (the game's own 16 m points; every fourth texel where a patch holds
            // detail heights) -- its height (x, m) and its gradient (yz, per m) from the same sixteen
            // reads (Sigg & Hadwiger 2005). The game's terrain runs straight from point to point, with
            // creases along its grid: the depth and the slope read off it (the terrain span m either
            // side, until 2026-11-11) handed those creases to every line of the surf as corners, and the
            // division made the most of them where the seabed is nearly flat. A point's height sits at
            // uv = its texel's index x the texel, read at the texel's centre, as the game's programs
            // take it; both height sets, the lower.
            float3 SeabedSmooth(float2 uv, float span)
            {
                float2 t = _TerrainMapping.xz;
                float metres = t.x / max(_SurfaceMapping.z, 1e-9);
                float every = max(floor(2.0 * span / max(metres, 1e-3) + 0.5), 1.0);
                float2 knot = t * every;
                float2 p = uv / knot;
                float2 j = floor(p);
                float2 f = p - j;
                float2 f2 = f * f, f3 = f2 * f;
                float2 w[4], d[4];
                w[0] = (1.0 - 3.0 * f + 3.0 * f2 - f3) / 6.0;
                w[1] = (4.0 - 6.0 * f2 + 3.0 * f3) / 6.0;
                w[2] = (1.0 + 3.0 * f + 3.0 * f2 - 3.0 * f3) / 6.0;
                w[3] = f3 / 6.0;
                d[0] = (-1.0 + 2.0 * f - f2) * 0.5;
                d[1] = (-4.0 * f + 3.0 * f2) * 0.5;
                d[2] = (1.0 + 2.0 * f - 3.0 * f2) * 0.5;
                d[3] = f2 * 0.5;
                float3 sum = 0.0;
                for (int b = 0; b < 4; b++)
                {
                    for (int a = 0; a < 4; a++)
                    {
                        float2 at = clamp((j + float2(a - 1.0, b - 1.0)) * knot + 0.5 * t, 0.5 * t, 1.0 - 0.5 * t);
                        float4 s = tex2Dlod(_TerrainHeight, float4(at, 0.0, 0.0));
                        float h = min(RGGameDecode(s.xy), RGGameDecode(s.zw));
                        sum += h * float3(w[a].x * w[b].y, d[a].x * w[b].y, w[a].x * d[b].y);
                    }
                }
                float perMetre = 1.0 / (every * max(metres, 1e-3));
                return sum * _TerrainMapping.y * float3(1.0, perMetre, perMetre);
            }

            // The greater and the lesser of two, the corner between them rounded over k (Quilez's
            // polynomial smooth minimum): a hard limit creases every line of the surf that crosses
            // where it sets in.
            float SoftMax(float a, float b, float k)
            {
                float h = max(k - abs(a - b), 0.0) / k;
                return max(a, b) + 0.25 * h * h * k;
            }

            float SoftMin(float a, float b, float k)
            {
                float h = max(k - abs(a - b), 0.0) / k;
                return min(a, b) - 0.25 * h * h * k;
            }

            // The distance from the waterline x (the depth over the seabed's slope) kept within a corridor
            // round the real one d (the map; negative inland): at sea no nearer than d / k and no farther
            // than d k, give or take the slack s; on flooded land only no nearer than |d| / k - s (the
            // surf does not follow a flood inland), the far bound opening fast so nothing steps at the
            // waterline. Soft bounds (over half the slack; the near one less where it is itself near 0,
            // so the waterline's own 0 stays 0).
            float ShoreCorridor(float x, float2 xz)
            {
                float2 uv = xz * _RGShoreMapping.x + _RGShoreMapping.y;
                float d = (tex2Dlod(_RGShoreDistance, float4(uv, 0.0, 0.0)).a * 255.0 - 128.0) * _RGShoreMapping.z;
                float k = max(_RGFoamSteep.z, 1.0), s = _RGFoamSteep.w;
                float lo = abs(d) / k - s;
                float hi = s + k * max(d, 0.0) + 40.0 * max(-d, 0.0);
                float soft = max(0.5 * s, 1e-3);
                return SoftMin(SoftMax(x, lo, clamp(lo, 1e-3, soft)), hi, soft);
            }

            float Hash(float n)
            {
                return frac(sin(n * 12.9898 + 4.1414) * 43758.5453);
            }

            // Independent chances: the share of a point covered by either.
            float Either(float a, float b)
            {
                return 1.0 - (1.0 - saturate(a)) * (1.0 - saturate(b));
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 world = i.world;
                float2 uv = RGGameSurfaceUV(world);
                float4 sa, sb;
                RGGameSurface(uv, sa, sb);

                // The game's own test for water here (the terrain under the surface, some depth); the
                // pixel is dropped at the very end, after every read that needs its neighbours.
                float depth = i.water.x - RGGameTerrainPixel(uv);
                float wet = min(depth, sb.y - 0.001);

                float2 flow = sb.xz * 2.0 - 1.0;
                float speed = length(flow);
                float2 tilt = sa.xz * 2.0 - 1.0;
                float steep = length(tilt);
                float clock = _SimulationTime.x;
                // The amount: the share of the model's foam, as ever (each layer keeps its own life); a
                // breaking crest and the swash stay solid white from a third up (a crest breaks white
                // however much foam the sea makes), and fade out below it.
                float amount = max(_RGFoamLook.w, 0.0);
                float solid = saturate(3.0 * amount);
                float lifeA = _RGFoamLayers.x, lifeB = _RGFoamLayers.y, lifeC = _RGFoamLayers.z;

                // SHORE: the surf, at the sea's level, in a band measured from the waterline: the
                // distance is the depth over the seabed's slope here (measured only where the surf can
                // reach: its terrain reads are the costly part).
                float seaAuto = 1.0 - smoothstep(_RGFoamShore.w, 2.0 * _RGFoamShore.w, abs(i.water.x - _RGFoamShore.z));
                // The player's paint over the rule: + brings the surf to water off the sea's level (a
                // lake), - takes it away (a harbour); 0 leaves the rule's.
                float paint = ShorePaintAt(world.xz);
                float sea = saturate(seaAuto + paint);
                float deepest = max(_RGFoamCrest.z, 0.1);
                float outerDeepest = max(_RGFoamOuter.y, deepest);
                float x = max(depth, 0.0);
                // The smooth seabed's slope (0 where the surf does not reach).
                float beach = 0.0;
                [branch]
                if (sea > 0.001 && depth < outerDeepest)
                {
                    // The SMOOTH seabed's depth over its slope (no less than the least: a soft floor) -- a
                    // wave's front has no corners. Its waterline is not quite the game's (a cove a few
                    // metres across, a rock, a quay's step are smoothed away): the foam's own edge, below,
                    // is what meets the water's.
                    // At sea the surface is no higher than the sea's level: the game's water lies on up
                    // the beach as a thin film (clear in the game's picture, cut off along a polygon),
                    // which is no surf.
                    float3 bed = SeabedSmooth(uv, _RGFoamCrest.y);
                    float least = max(_RGFoamCrest.w, 1e-4);
                    float level = lerp(i.water.x, min(i.water.x, _RGFoamShore.z), seaAuto);
                    x = max(level - bed.x, 0.0) / sqrt(dot(bed.yz, bed.yz) + least * least);
                    beach = length(bed.yz);
                    // The map measures from the SEA's waterline: off the sea's level it would hold the surf off.
                    if (_RGShoreMapping.w > 0.5 && seaAuto > 0.5)
                        x = ShoreCorridor(x, world.xz);
                }
                float surf = max(_RGFoamExtra.w, 1.0);
                float period = max(_RGFoamSurf.x, 0.5);
                float sets = max(_RGFoamSurf.z, 1.0);
                // A bore broken at the band's edge reaches x after 2 (sqrt(surf) - sqrt(x)) / sqrt(g s):
                // at sqrt(g d) with d = s x, s the gentle beach the bores keep the pace of (the same on
                // every coast: the lines stay apart on a steep one); its phase drifts slowly along the shore.
                // Past the band's edge the same crests are the swell coming in, before they break (the
                // time to the edge, negative).
                float travel = 2.0 * (sqrt(surf) - sqrt(x)) / sqrt(9.80665 * max(_RGFoamSurf.y, 1e-3));
                float along = (0.031 * world.x + 0.023 * world.z) / 6.2831853;
                // A broken wave's front is ragged: its phase jittered by the patches' noise, a few metres
                // across (two scales of it).
                float ragged = 0.12 * (tex2D(_RGFoamPattern, world.xz * (1.0 / 60.0)).g - 0.5) + 0.05 * (tex2D(_RGFoamPattern, world.xz * (1.0 / 17.0)).a - 0.5);
                float phase = clock / period - travel / period + along + ragged;
                float bore = floor(phase);
                float age = frac(phase) * period;
                // The last three bores' foam, each its own generation (the older ones' residual still
                // under the newer: the layers of the surf).
                float shoreW = 0.0, shoreF = 0.0, shoreB = 0.0, crest = 0.0;
                for (int k = 0; k < 3; k++)
                {
                    float id = bore - k;
                    float inSet = id - sets * floor(id / sets);
                    // A set: the big ones together, then a lull.
                    float strength = saturate(0.55 + 0.4 * cos(6.2831853 * inSet / sets) + 0.2 * (Hash(inSet) - 0.5));
                    // The bigger it is the farther out it broke (as deep as its height allows); none
                    // past the deepest water the surf reaches.
                    float breaks = surf * (0.45 + 0.55 * strength);
                    float broken = strength * (1.0 - smoothstep(0.8 * breaks, breaks, x)) * (1.0 - smoothstep(0.8 * deepest, deepest, depth));
                    float t = age + k * period;
                    shoreW = Either(shoreW, broken * exp(-t / lifeB));
                    shoreF = Either(shoreF, broken * exp(-t / lifeA));
                    shoreB = Either(shoreB, broken * exp(-t / lifeC));
                    // The crest itself, the bore's churning front: nearly solid white for its first
                    // moments (its width the bore's run over them).
                    if (k == 0)
                        crest = saturate(1.3 * broken) * exp(-t / max(_RGFoamCrest.x, 0.05));
                }
                // THE SWASH. Each bore runs up the beach as a sheet and drains back before the next: the
                // foam's own edge, right at the waterline as the bore arrives and falling back from it
                // after, in lobes (each wave's its own: a draw of the patches' noise) -- white along its
                // front. Until 2026-11-11 a white band lay ON the waterline, and the game's waterline is
                // a polygon (its terrain runs straight between its points; its water is cut along its
                // 32 m depth map's cells): thin, clear water there in the game's own picture, a white
                // outline with corners in ours. Now the foam ends in water, thinning to nothing through
                // the lace, and also toward the game's two cuts wherever they come first.
                float swash = max(_RGFoamSurf.w, 0.01);
                float least0 = max(_RGFoamCrest.w, 1e-4);
                float run = exp(-age / max(_RGFoamFeature.w, 0.1));
                float2 lobeAt = world.xz * (1.0 / 90.0);
                float lobes = tex2Dgrad(_RGFoamPattern, lobeAt + float2(Hash(bore + 0.9) * 3.7, Hash(bore + 0.6) * 4.1), ddx(lobeAt), ddy(lobeAt)).g;
                float back = swash * lerp(0.08, 1.0, 1.0 - run) * (0.35 + 1.3 * lobes);
                float front = max(_RGFoamFeature.z, 0.05);
                float edge = smoothstep(back, back + front, x) *
                             smoothstep(0.0, max(_RGFoamFront.w, 1e-3), depth) * saturate((sb.y - 0.001) / 0.006);
                // (No white front on a flat: where the seabed barely slopes, the few metres "from the
                // waterline" are a wide sheet of shallows, all of it reached in the same instant.)
                float sloping = smoothstep(least0, 3.0 * least0, beach);
                crest = Either(crest, 0.8 * (0.5 + 0.5 * run) * sloping * (1.0 - smoothstep(back + front, back + front + swash * (0.5 + lobes), x)));

                // OUTSIDE THE SURF: the swell's crests coming in break here and there before they reach
                // it (spilling breakers) -- short streaks of white along each crest with a little trail,
                // fewer the farther out; each wave breaks in other places (its own draw of the noise,
                // blotches a few tens of metres across, cut by the thin crest into streaks of every
                // length), each streak its own strength.
                float outerReach = surf * max(_RGFoamOuter.x, 1.0);
                float outerBand = smoothstep(0.85 * surf, 1.05 * surf, x) * (1.0 - smoothstep(0.7 * outerReach, outerReach, x)) *
                                  (1.0 - smoothstep(0.8 * outerDeepest, outerDeepest, depth));
                float farOut = saturate((x - surf) / max(outerReach - surf, 1.0));
                float chance = lerp(0.42, 0.85, farOut);
                float2 q = world.xz / max(_RGFoamOuter.w, 1.0);
                float2 qWave = q + float2(Hash(bore + 0.71) * 7.1, Hash(bore + 0.37) * 5.3);
                // (the gradients of the place, not of the wave's jump: no seam of a coarse mip along the
                // crest; and the noise's finest octave averaged away, level 2 at the least: the blotches'
                // shapes, not their grain, say where a crest breaks.)
                float2 qx = ddx(q) * _RGFoamScale.z, qy = ddy(q) * _RGFoamScale.z;
                float blotLevel = max(0.5 * log2(max(max(dot(qx, qx), dot(qy, qy)), 1e-8)), 2.0);
                float blot = tex2Dlod(_RGFoamPattern, float4(qWave, 0.0, blotLevel)).g;
                // A streak's END: its strength comes up over a fixed distance inside the blotch's edge --
                // the noise over its own slope, from two more reads a step along each axis (the distance
                // to a level set: Taubin 1994). Until 2026-11-11 the strength was the noise itself over a
                // fixed step of it, and the white's width is the log of the strength: wherever the noise
                // rose slowly along a crest the streak ran out in a spear's tip tens of metres long, and
                // one that only just cleared its level was a hairline.
                // (The band's own fade raises the level the noise must clear -- fewer, shorter streaks
                // toward its limits -- where it used to scale the strength too.)
                float over = blot - lerp(1.0, chance, outerBand);
                float step2 = 1.5 / max(_RGFoamOuter.w, 1.0);
                float2 rise = float2(tex2Dlod(_RGFoamPattern, float4(qWave + float2(step2, 0.0), 0.0, blotLevel)).g,
                                     tex2Dlod(_RGFoamPattern, float4(qWave + float2(0.0, step2), 0.0, blotLevel)).g) - blot;
                float inside = over / max(length(rise) / 1.5, 0.002);
                float streak = smoothstep(0.0, max(_RGFoamScale.w, 0.1), inside) * (0.45 + 0.55 * Hash(bore + 0.13));
                float outerLife = max(_RGFoamOuter.z, 0.05);
                // Solid along the crest, with a faint trail of lace behind it.
                float spill = streak * saturate(1.3 * exp(-age / outerLife));
                shoreW = Either(shoreW, 0.3 * streak * exp(-age / (3.0 * outerLife)));
                crest = Either(crest, spill);

                float shoreAmount = _RGFoamShore.x * sea * edge;
                shoreW = Either(shoreW * amount, crest * solid) * shoreAmount;
                shoreF = Either(shoreF * amount, crest * solid) * shoreAmount;
                shoreB = Either(shoreB * amount, 0.5 * spill * solid) * shoreAmount;
                // Each bore's foam a pattern of its own (the set's place in the minute picks it).
                float set = bore - sets * floor(bore / sets);
                float2 boreShift = shoreW > 0.001 ? float2(Hash(set + 0.5) * 53.1, Hash(set + 0.25) * 71.3) : float2(0.0, 0.0);

                // FAST: the flow's speed, the steeper the surface the sooner; the core fresh.
                float fastSource = _RGFoamFast.x * smoothstep(_RGFoamFast.y, _RGFoamFast.z, speed * (1.0 + _RGFoamFast.w * steep));
                float fastW = saturate(fastSource * amount);
                float fastF = fastW * smoothstep(0.3, 1.0, fastSource);
                float fastB = saturate(fastSource * _RGFoamExtra.y * amount);

                // FRONTS: rising fast (fresh); falling again behind the crest (the residual, the bubbles)
                // -- and steep, as Chentanez & Mueller's breaking test asks: the game's calm sea rises and
                // falls up to a metre from one water frame to the next everywhere (the author's city), on
                // a surface no steeper than 6%; a wave's face is far steeper.
                float faces = smoothstep(_RGFoamSteep.x, _RGFoamSteep.y, steep);
                float up = _RGFoamFront.x * smoothstep(_RGFoamFront.y, _RGFoamFront.z, i.water.y) * faces;
                float down = _RGFoamFront.x * smoothstep(_RGFoamFront.y, _RGFoamFront.z, -i.water.y) * faces;
                float frontW = saturate(Either(up, 0.7 * down) * amount);
                float frontF = saturate(up * amount);
                float frontB = saturate(Either(up, down) * _RGFoamExtra.y * amount);

                float a = Either(Either(shoreW, fastW), frontW);
                float fresh = min(Either(Either(shoreF, fastF), frontF), a);
                float bubbles = max(Either(Either(shoreB, fastB), frontB), a);
                // How much of the white is fresh (stage A): its look, from residual (0) to fresh (1).
                float share = saturate(fresh / max(a, 1e-4));

                // Carried along the flow: two phases half a cycle apart, each reset when the other is
                // whole (Vlachos 2010), with a slow offset across the map so the resets do not pulse
                // in step (the game's own term).
                float cycle = max(_RGFoamLook.z, 0.5);
                float flowPhase = clock / cycle + 0.2 * cos(0.06 * world.x) * cos(0.06 * world.z);
                float f0 = frac(flowPhase), f1 = frac(flowPhase + 0.5);
                float2 reachFlow = flow * _RGFoamLook.y * cycle;
                float blend = 2.0 * abs(0.5 - f0);
                float2 p0 = world.xz - reachFlow * (f0 - 0.5) + boreShift;
                float2 p1 = world.xz - reachFlow * (f1 - 0.5) + boreShift + float2(17.3, 29.1);

                // The white: fresh sheets are patch-heavy and dense, old ones lace-heavy and holed.
                float w = lerp(0.5, 0.8, share);
                float h = lerp(_RGFoamSheet.w, _RGFoamSheet.z, share);
                float asked = min(a / max(1.0 - h, 0.05), 1.0);
                float t = FoamThreshold(asked, w);
                float thick0, thick1;
                float c0 = Coverage(p0, asked, t, w, h, thick0);
                float c1 = Coverage(p1, asked, t, w, h, thick1);
                float white = lerp(c0, c1, blend);
                float thick = lerp(thick0, thick1, blend);
                // Sparse foam as grey, not lone white specks (they would only sparkle): no pixel more
                // than four times the share asked for; and a trace of foam is none.
                white = min(white, 4.0 * a) * smoothstep(0.01, 0.05, a);

                // The bubbles, under: read where the refracted eye ray meets their depth.
                float3 toEye = normalize(_WorldSpaceCameraPos - world);
                float3 down3 = refract(-toEye, float3(0.0, 1.0, 0.0), 0.75);
                float2 under = down3.xz / max(-down3.y, 0.2) * _RGFoamBubbles.z;
                // Their clouds spread wider than the white; they are as thick as the bubbles are many (a
                // plume thins as it rises and spreads: soft edges, no discs).
                float spread = saturate(bubbles * _RGFoamExtra.y);
                float b0 = Clouds(p0 + under, spread);
                float b1 = Clouds(p1 + under, spread);
                float cloud = lerp(b0, b1, blend) * saturate(bubbles);

                // The light. The water's large-scale slope; the fresh sheet's own, from its thickness.
                float3 n = normalize(float3(tilt.x, sqrt(max(1.0 - dot(tilt, tilt), 0.04)), tilt.y));
                float2 grad = WorldGradient(thick, world.xz) * _RGFoamExtra.x * share;
                grad /= max(length(grad) / 1.5, 1.0);
                float3 nFoam = normalize(n + float3(-grad.x, 0.0, -grad.y));
                float shadow = SHADOW_ATTENUATION(i);
                float3 sun = _LightColor0.rgb * shadow;
                float3 l = _WorldSpaceLightPos0.xyz;
                float3 litFoam = sun * saturate(dot(nFoam, l)) + ShadeSH9(float4(nFoam, 1.0));
                float3 litWater = sun * saturate(dot(n, l)) + ShadeSH9(float4(n, 1.0));

                float3 white3 = lerp(_RGFoamSheet.x, _RGFoamLayers.w, share) * litFoam;
                // The bubbles: the water's own body reflectance (f b_b / (a + b_b)), its a / b_b from
                // the colour the water is drawn with, the bubbles' backscatter added.
                float3 r0 = max(_RGFoamWater.rgb, 1e-4);
                float f = _RGFoamBubbles.x;
                float3 ratio = max(f / r0 - 1.0, 0.0);
                float beta = _RGFoamBubbles.y * spread;
                float3 rb = f * (1.0 + beta) / (ratio + 1.0 + beta);
                // Seen through the water above them: the light's way down to them and back up to the
                // eye, absorbed as pure water absorbs (Pope & Fry 1997) -- red the most: turquoise.
                float path = _RGFoamBubbles.z * (1.0 + 1.0 / max(-down3.y, 0.2));
                rb = max(rb * exp(-_RGFoamAbsorb.rgb * path), r0);
                float3 bubble3 = rb * litWater;
                float fresnel = 0.0204 + 0.9796 * pow(1.0 - saturate(dot(n, toEye)), 5.0);

                float aw = white * lerp(_RGFoamSheet.y, 1.0, share);
                float ab = cloud * _RGFoamBubbles.w * (1.0 - fresnel);
                float3 colour = white3 * aw + bubble3 * ab * (1.0 - aw);
                float alpha = aw + ab * (1.0 - aw);

                if (_RGFoamDebug.x > 1.5 && _RGFoamDebug.x < 2.5)
                    return float4(0.85, 0.0, 0.85, 0.85);
                clip(wet);
                if (_RGFoamDebug.x > 3.5 && _RGFoamDebug.x < 4.5)
                    return float4(0.85 * saturate(float3(white * share, white * (1.0 - share), cloud)), 0.85);
                if (_RGFoamDebug.x > 2.5 && _RGFoamDebug.x < 4.5)
                    return float4(0.85, 0.0, 0.85, 0.85);
                if (_RGFoamDebug.x > 0.5 && _RGFoamDebug.x < 4.5)
                    return float4(0.85 * saturate(float3(shoreW, fastW, frontW)), 0.85);
                alpha = FiniteAlpha(alpha);
                float4 foam = float4(Finite(colour) * (alpha > 0.0 ? 1.0 : 0.0), alpha);
                // (5, the preview's: the surf's distance from the waterline as a line every 10 m over the
                // foam, the surf's own edge in yellow -- the shape every bore's line takes.)
                if (_RGFoamDebug.x > 4.5)
                {
                    float px = max(fwidth(x), 1e-4);
                    float every = abs(frac(x / 10.0 + 0.5) - 0.5) * 10.0;
                    float inReach = sea * step(depth, outerDeepest) * step(x, outerReach);
                    float tick = inReach * (1.0 - smoothstep(0.75, 1.5, every / px));
                    float band = inReach * (1.0 - smoothstep(1.0, 2.0, abs(x - surf) / px));
                    float3 ink = lerp(float3(1.0, 0.25, 0.9), float3(1.0, 0.9, 0.1), band);
                    float inked = max(tick, band);
                    foam = float4(lerp(foam.rgb, ink, inked), lerp(foam.a, 1.0, inked));
                }
                [branch]
                if (_RGFoamDebug.y > 0.5)
                {
                    float reach = sea * (1.0 - smoothstep(0.9 * outerReach, outerReach, x)) * (1.0 - smoothstep(0.9 * outerDeepest, outerDeepest, depth));
                    foam = PaintView(foam, paint, reach, x, surf, world.xz, shoreW, fastW, frontW);
                }
                return foam;
            }
            ENDCG
        }
    }

    Fallback Off
}
