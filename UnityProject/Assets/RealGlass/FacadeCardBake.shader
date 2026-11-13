// Real Glass: the facade cards' baker (FacadeCards.cs). Each side of a building asset's bounding box,
// and its roof, is a tile of two atlases; the asset's own mesh is drawn into each tile, orthographic
// along that side's normal, with a depth test (the nearest surface is what a card holds).
//   0 CLEAR: the tile emptied -- colour 0, depth at the far end -- before its side is drawn (a tile
//     is cleared alone: the rest of the atlas keeps the other assets).
//   1 BAKE: the mesh into the tile (FacadeCards.Frame maps a mesh point to the tile's u, v and the
//     depth from the side), the three atlases at once: rgb the albedo and a the glass share; the
//     windows that glow, the share of the windows the game lights at night by their value in four
//     bins, the share drawn; the specular (the game's own reflection on every surface scales with it). The asset's maps
//     are read 4 x 4 times over each texel at their full size: the glass threshold and the game's
//     rule for lit windows are steps, which no filtered or mip-mapped read keeps (a window at 0.9
//     beside a wall at 0 averages to a value the game lights by another rule).
//   2 SHOW (the preview's pictures): an atlas at a level, through Blit.
// Not in the bundles yet (a preview round, 2026-11-12): BundleBuilder and ShaderCheck list it when
// the mod uses it.
Shader "RealGlass/FacadeCardBake"
{
    Properties
    {
        _MainTex ("", 2D) = "white" {}
        _XYSMap ("", 2D) = "gray" {}
        _ACIMap ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    #include "FullScreen.cginc"

    sampler2D _MainTex;
    float4 _MainTex_ST;
    sampler2D _XYSMap;
    sampler2D _ACIMap;
    float4 _XYSMap_TexelSize;    // (1 / w, 1 / h, w, h), set by FacadeCards.Bake
    float4 _ACIMap_TexelSize;
    float4x4 _RGCardFrame;       // mesh space -> (u, v, depth from the side) in 0..1
    float4 _RGCardTile;          // the tile in the atlas: xy its corner, zw its size (0..1)
    float4 _RGCardBakeInfo;      // x the glass threshold (spec at or over it is glass), y the level SHOW reads,
                                 // z 1: the illumination map read bilinearly (0: at its texels' centres)

    // A point of the tile at depth d (0 at the side, 1 at the far one), in clip space as this API
    // takes it (written here, not through Unity's matrices: the same tile on every API).
    float4 TileClip(float2 q, float d)
    {
        float2 xy = (_RGCardTile.xy + q * _RGCardTile.zw) * 2.0 - 1.0;
        #if UNITY_UV_STARTS_AT_TOP
        xy.y = -xy.y;   // a read at v = 0 reads the first row written (Direct3D's, Metal's)
        #endif
        #if defined(UNITY_REVERSED_Z)
        float farZ = 0.0;
        #else
        float farZ = 1.0;
        #endif
        return float4(xy, lerp(UNITY_NEAR_CLIP_VALUE, farZ, clamp(d, 0.001, 0.999)), 1.0);
    }

    struct v2f
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2f vertClear(float4 vertex : POSITION)
    {
        v2f o;
        // The quad (GlassScreen.ClipQuad: -1..1) over the whole tile, at the far end.
        o.pos = TileClip(vertex.xy * 0.5 + 0.5, 1.0);
        o.uv = float2(0.0, 0.0);
        return o;
    }

    struct Out2
    {
        float4 atlas : SV_Target0;
        float4 light : SV_Target1;
        float4 spec : SV_Target2;
    };

    Out2 fragClear(v2f i)
    {
        Out2 o;
        o.atlas = float4(0.0, 0.0, 0.0, 0.0);
        o.light = float4(0.0, 0.0, 0.0, 0.0);
        o.spec = float4(0.0, 0.0, 0.0, 0.0);
        return o;
    }

    v2f vertBake(float4 vertex : POSITION, float2 uv : TEXCOORD0)
    {
        v2f o;
        float3 q = mul(_RGCardFrame, float4(vertex.xyz, 1.0)).xyz;
        o.pos = TileClip(q.xy, q.z);
        o.uv = TRANSFORM_TEX(uv, _MainTex);
        return o;
    }

    Out2 fragBake(v2f i)
    {
        // The texel's footprint on the asset's maps, read 4 x 4 at their full size.
        float2 dx = ddx(i.uv), dy = ddy(i.uv);
        float3 albedo = float3(0.0, 0.0, 0.0);
        float kept = 0.0, glass = 0.0, glow = 0.0, specSum = 0.0, bSum = 0.0;
        float4 bins = float4(0.0, 0.0, 0.0, 0.0);
        [unroll]
        for (int y = 0; y < 4; y++)
        {
            [unroll]
            for (int x = 0; x < 4; x++)
            {
                float2 uv = i.uv + dx * ((x + 0.5) / 4.0 - 0.5) + dy * ((y + 0.5) / 4.0 - 0.5);
                // The steps (lit-window bands, the glass threshold, the cut-out) on the maps' own texels,
                // read at their centres: a bilinear read between a window at 0.9 and a wall at 0 is a
                // value in between, which the game's rule lights as a glow (0..0.48).
                // (_RGCardBakeInfo.z = 1, the preview's choice: read bilinearly instead, as the game's
                // own pixel reads its map up close -- the edge values kept, and lit by the same rule.)
                float2 centre = (floor(uv * _ACIMap_TexelSize.zw) + 0.5) * _ACIMap_TexelSize.xy;
                float4 aci = tex2Dlod(_ACIMap, float4(_RGCardBakeInfo.z > 0.5 ? uv : centre, 0.0, 0.0));
                // The asset's cut-out, as the game tests it: _ACIMap.r over a half is not drawn.
                float here = aci.r > 0.5 ? 0.0 : 1.0;
                float spec = 1.0 - tex2Dlod(_XYSMap, float4((floor(uv * _XYSMap_TexelSize.zw) + 0.5) * _XYSMap_TexelSize.xy, 0.0, 0.0)).z;
                float b = aci.b;
                albedo += tex2Dlod(_MainTex, float4(uv, 0.0, 0.0)).rgb * here;
                glass += (spec >= _RGCardBakeInfo.x ? 1.0 : 0.0) * here;
                specSum += spec * here;
                glow += (b > 0.48 ? 0.0 : b / 0.48) * here;
                // The windows the game lights at night, in two bins -- band 1's values (0.48..0.75) and
                // band 2's (over 0.75) -- each its share and the sum of its values: the rule is run on
                // each bin's MEAN at the lookup (exact where an asset paints its windows with one value
                // a band, as the real assets read in round 2 do).
                float in1 = b > 0.48 && b <= 0.75 ? 1.0 : 0.0, in2 = b > 0.75 ? 1.0 : 0.0;
                bins += float4(in1, in1 * b, in2, in2 * b) * here;
                bSum += b * here;
                kept += here;
            }
        }
        if (kept < 0.5)
            discard;
        // Premultiplied by the share drawn (/ 16): the mips then average what is there with what is not.
        Out2 o;
        o.atlas = float4(albedo, glass) / 16.0;
        o.light = float4(glow, bins.x, bins.y, kept) / 16.0;
        o.spec = float4(specSum, bins.z, bins.w, bSum) / 16.0;
        return o;
    }

    float4 fragShow(v2f_full i) : SV_Target
    {
        float4 c = tex2Dlod(_MainTex, float4(i.uv, 0.0, _RGCardBakeInfo.y));
        return c;
    }
    ENDCG

    SubShader
    {
        Pass
        {
            Name "Clear"
            Cull Off
            ZWrite On
            ZTest Always

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertClear
            #pragma fragment fragClear
            ENDCG
        }

        Pass
        {
            Name "Bake"
            Cull Off
            ZWrite On
            ZTest LEqual

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vertBake
            #pragma fragment fragBake
            ENDCG
        }

        Pass
        {
            Name "Show"
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert_full
            #pragma fragment fragShow
            ENDCG
        }
    }
}
