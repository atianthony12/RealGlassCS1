// Real Glass: the facade cards' LEVEL taken from the frame (FacadeCards.cs, FacadeCards.Gain; a preview
// round, 2026-11-13). The frame a reader reads already has the fog, the ambient occlusion and other
// mods' light on it; a card lit by a formula does not. So, every frame, where a building that has a card
// is on the screen, the frame's colour there is set against the card's own colour AT THAT SAME POINT
// (the card looked up by the world point and the face its normal names, lit as CardRadiance lights it,
// seen as the camera sees it), summed in four classes -- walls and glass (the card's glass share), each
// on faces the main light reaches and faces it does not (N.L) -- and the readers multiply a card's light
// by frame / card of its classes (FacadeCards.cginc's CardGain). Luminance only.
//   0 SAMPLE: drawn on the cube round the camera (as GlassReflection's passes) into a small target,
//     one screen point a texel in each third of it: the walls' share of the sums (frame x lit, card x
//     lit, frame x shaded, card x shaded), the glass's, and the four weights (walls lit, walls shaded,
//     glass lit, glass shaded). _RGGainInfo.w = 1 or 2 (the preview's dumps, a target the screen's
//     size): 1 (frame, card, lit weight, glass share), 2 (the building's id, the face, 1 where it
//     counted, N.L).
//   1 REDUCE: the sample target summed into 3 x 1 (one texel a third), blended with last frame's IN THE
//     SHADER (GlassUtil's _RGCity is the model): a blend unit would keep a NaN.
//   2 GRADE (the preview's test only): a made-up grade -- x a gain and a share toward a haze colour --
//     put on a picture (the finished picture, before the readers read it).
// Not in the bundles (a preview round): BundleBuilder and ShaderCheck list it when the mod uses it.
Shader "RealGlass/FacadeCardGain"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "GlassCommon.cginc"
    #include "FacadeCards.cginc"

    sampler2D _MainTex;
    sampler2D _RGGainFrame;      // the frame the reader reads, on this frame's camera (_RGGainInfo.y = 0)
    sampler2D _RGGainPrev;       // REDUCE: last frame's sums (2 x 1)
    float4 _RGGainInfo;          // x -, y 1: the frame is last frame's finished picture (reprojected), 0: _RGGainFrame;
                                 // z -; w 0 the sums, 1 / 2 the preview's dumps
    float4 _RGGainInfo2;         // xy one screen pixel (uv), z the share of last frame's sums kept (0: this frame alone),
                                 // w how far from the box's face a point may lie and count (m)
    float4 _RGGainSize;          // REDUCE: x, y the sample target's texels a side (whole target), z, w -
    float4 _RGGainGrade;         // GRADE: x the gain, y the share toward the haze; _RGGainHaze: the haze's colour (linear)
    float4 _RGGainHaze;

    struct v2f
    {
        float4 pos : SV_POSITION;
        float3 dir : TEXCOORD0;
        float4 screen : TEXCOORD1;
    };

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

    // NaN and infinities to 0, by the bits (Metal compiles with fast math); no clamp: the sums run to thousands.
    float4 Sane(float4 c)
    {
        uint4 mag = asuint(c) & 0x7FFFFFFFu;
        return mag >= 0x7F800000u ? float4(0.0, 0.0, 0.0, 0.0) : c;
    }

    // The depth: the camera's own, read at AfterFinalPass -- the moment the mod's copies of it are taken
    // (GlassScreen's Hi-Z, WaterSurfaceMask.SceneDepth), before the forward water is drawn and before
    // WaterSurfaceMask merges the water into the global at BeforeImageEffectsOpaque. Not the half-float
    // Hi-Z: a normal from one pixel's depth step needs the full precision. The pass must run at that event.
    float EyeAt(float2 uv)
    {
        return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0.0, 0.0)));
    }

    float4x4 _RGGainInvVP;       // this frame's camera, inverted (OpenGL-convention projection x view: the uv the copies are read with)

    // The world point at screen uv and eye depth eye.
    float3 PointAt(float2 uv, float eye, float3 forward)
    {
        float4 w = mul(_RGGainInvVP, float4(uv * 2.0 - 1.0, 0.0, 1.0));
        float3 d = SafeNormalize(w.xyz / w.w - _WorldSpaceCameraPos, forward);
        return _WorldSpaceCameraPos + d * (eye / max(dot(d, forward), 1e-4));
    }

    float4 fragSample(v2f i) : SV_Target
    {
        float2 suv = i.screen.xy / i.screen.w;
        bool dump = _RGGainInfo.w > 0.5;
        // Three thirds, the same screen point in each: the walls' sums, the glass's sums, the weights.
        float section = 0.0;
        if (!dump)
        {
            section = floor(suv.x * 3.0);
            suv.x = frac(suv.x * 3.0);
        }
        float4 none = float4(0.0, 0.0, 0.0, 0.0);
        float raw = SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(suv, 0.0, 0.0));
        float3 forward = -normalize(UNITY_MATRIX_V[2].xyz);
        float2 px = _RGGainInfo2.xy;
        float4 result = none;
        // No finished picture yet (the glass's first frame): no data.
        bool haveFrame = _RGGainInfo.y < 0.5 || _RGFinished.x > 0.5;
        if (haveFrame && Linear01Depth(raw) < 0.9999)
        {
            float eye = LinearEyeDepth(raw);
            float3 P = PointAt(suv, eye, forward);
            // The normal from the depth at the neighbours, each axis the side whose depth is nearer
            // this point's (an edge's other side is another surface).
            float eR = EyeAt(suv + float2(px.x, 0.0)), eL = EyeAt(suv - float2(px.x, 0.0));
            float eU = EyeAt(suv + float2(0.0, px.y)), eD = EyeAt(suv - float2(0.0, px.y));
            float sx = abs(eR - eye) <= abs(eL - eye) ? 1.0 : -1.0;
            float sy = abs(eU - eye) <= abs(eD - eye) ? 1.0 : -1.0;
            float3 Px = PointAt(suv + float2(px.x * sx, 0.0), sx > 0.0 ? eR : eL, forward);
            float3 Py = PointAt(suv + float2(0.0, px.y * sy), sy > 0.0 ? eU : eD, forward);
            float3 n = SafeNormalize(cross(Px - P, Py - P), float3(0.0, 1.0, 0.0));
            n = dot(n, _WorldSpaceCameraPos - P) < 0.0 ? -n : n;

            // Whose: the city map's cell two metres inside the surface.
            float id = 0.0;
            if (_RGCityMapST.w > 0.5)
            {
                float2 c = floor((P.xz - n.xz * 2.0 - _RGCityMapST.xy) / _RGCityMapST.z);
                if (all(c >= 0.0) && all(c < _RGCityMapInfo.x))
                    id = DecodeCity(tex2Dlod(_RGCityMap, float4((c + 0.5) / _RGCityMapInfo.x, 0.0, 0.0))).y;
            }
            float dist = length(P - _WorldSpaceCameraPos);
            float pixelM = dist * 2.0 * px.y / max(abs(UNITY_MATRIX_P[1][1]), 1e-4);
            CardSample card;
            float face, texelM;
            CardAtPoint(id, P, n, pixelM, _RGGainInfo2.w, card, face, texelM);
            if (card.found > 0.5)
            {
                // The frame there, read as wide as the card's texel; the card lit as the reader lights it.
                float lod = clamp(log2(max(texelM / max(pixelM, 1e-4), 1.0)), 0.0, 8.0);
                float2 uv = suv;
                float3 frame;
                if (_RGGainInfo.y > 0.5)
                {
                    float2 last;
                    bool onLast = ScreenUV(P, true, last);
                    uv = onLast ? last : suv;
                    frame = Finite(tex2Dlod(_RGSceneFinished, float4(uv, 0.0, lod)).rgb);
                }
                else
                {
                    frame = Finite(tex2Dlod(_RGGainFrame, float4(uv, 0.0, lod)).rgb);
                }
                float3 V = SafeNormalize(P - _WorldSpaceCameraPos, forward);
                float3 mirror = reflect(V, card.n);
                // Below the horizon the card's glass mirrors what the reader passes: the glass's ground
                // fill, the water's frame average.
                float3 below = float3(0.0, 0.0, 0.0);
                if (mirror.y < 0.0)
                    below = _RGGainInfo.y > 0.5 ? GroundFill(P, mirror, _RGRefl.y, _RGFill.y) : Finite(tex2Dlod(_RGGainFrame, float4(0.5, 0.5, 0.0, 12.0)).rgb);
                float3 model = CardRadiance(card, V, id, _RGRefl.y, below);
                float nl = saturate(dot(card.n, _RGCardSunDir.xyz));
                float wl = smoothstep(0.0, 0.3, nl);
                // Four classes: walls and glass (the card's glass share), each lit / not by N.L.
                float g = card.glass;
                float F = Luma(frame), M = Luma(model);
                float4 sums = float4(F * wl, M * wl, F * (1.0 - wl), M * (1.0 - wl));
                if (_RGGainInfo.w > 1.5)
                    result = float4(id, face, 1.0, nl);
                else if (dump)
                    result = float4(F, M, wl, g);
                else
                    result = section < 0.5 ? sums * (1.0 - g)
                           : (section < 1.5 ? sums * g : float4(wl * (1.0 - g), (1.0 - wl) * (1.0 - g), wl * g, (1.0 - wl) * g));
            }
        }
        return Sane(result);
    }

    struct v2f_q
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f_q vertQuad(float4 vertex : POSITION, float2 uv : TEXCOORD0)
    {
        v2f_q o;
        o.pos = UnityObjectToClipPos(vertex);
        o.uv = uv;
        return o;
    }

    float4 fragReduce(v2f_q i) : SV_Target
    {
        // Texel k (0 the walls' sums, 1 the glass's, 2 the weights): the sample target's k-th third, and
        // this frame's weights (the third third) for how much of last frame's each class keeps.
        float w = _RGGainSize.x / 3.0, h = _RGGainSize.y;
        float section = floor(i.uv.x * 3.0);
        float start = section * w;
        float4 sum = float4(0.0, 0.0, 0.0, 0.0);
        float4 weights = float4(0.0, 0.0, 0.0, 0.0);
        [loop]
        for (float y = 0.0; y < h; y += 1.0)
        {
            [loop]
            for (float x = 0.0; x < w; x += 1.0)
            {
                float v = (y + 0.5) / h;
                sum += Sane(tex2Dlod(_MainTex, float4((start + x + 0.5) / _RGGainSize.x, v, 0.0, 0.0)));
                weights += Sane(tex2Dlod(_MainTex, float4((2.0 * w + x + 0.5) / _RGGainSize.x, v, 0.0, 0.0)));
            }
        }
        float4 prev = Sane(tex2Dlod(_RGGainPrev, float4(i.uv, 0.0, 0.0)));
        // Each component's class: (frame, card) pairs of lit and shaded, walls then glass; the weights
        // texel one class each.
        float4 n = section < 0.5 ? weights.xxyy : (section < 1.5 ? weights.zzww : weights);
        // How much of last frame's each class keeps: the base share (_RGGainInfo2.z; 0 = this frame alone)
        // with plenty of points, slower with few (0.985 near none), and ALL of it with none -- a class
        // out of sight holds its last good value instead of jumping.
        float base = saturate(_RGGainInfo2.z);
        float4 slow = lerp(float4(0.985, 0.985, 0.985, 0.985), base.xxxx, saturate(n / 100.0));
        float4 keep = base > 0.0 ? (n > 0.5 ? slow : float4(1.0, 1.0, 1.0, 1.0)) : float4(0.0, 0.0, 0.0, 0.0);
        return lerp(sum, prev, keep);
    }

    float4 fragGrade(v2f_q i) : SV_Target
    {
        float4 c = tex2Dlod(_MainTex, float4(i.uv, 0.0, 0.0));
        return float4(lerp(c.rgb * _RGGainGrade.x, _RGGainHaze.rgb, _RGGainGrade.y), c.a);
    }
    ENDCG

    SubShader
    {
        ZWrite Off
        ZTest Always
        Cull Off

        Pass
        {
            Name "Sample"
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment fragSample
            ENDCG
        }

        Pass
        {
            Name "Reduce"
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertQuad
            #pragma fragment fragReduce
            ENDCG
        }

        Pass
        {
            Name "Grade"
            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertQuad
            #pragma fragment fragGrade
            ENDCG
        }
    }
}
