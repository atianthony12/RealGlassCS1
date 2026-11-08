// Real Glass: the glass's PRE-PASS -- this frame's glass as a thin G-buffer, for the reflection pass
// (GlassReflection.shader; GlassPrePass.cs records the draws). The very draws of Glass.shader (the
// same meshes, matrices and blocks, recorded beside them), drawn at the end of the deferred frame
// against the camera's depth (ZTest LEqual, Offset 0 -1 as the glass), running the same first half
// of the fragment (GlassSurface.cginc): where the forward glass will be, so is this, with the same
// pane normal and roughness.
//   SV_Target0 (ARGBHalf): the pane's normal np, octahedral (xy); the roughness (z); the coverage
//                          (w: mask x strength, never 0 where glass is -- 0 is "no glass here").
//   SV_Target1 (ARGBHalf): the building's id as two exact halves (x x 256 + y, IdOf: the city walk
//                          does not meet its own building); the glass's height over its ground
//                          (z: P.y - baseY); the pixel's footprint in metres (w).
Shader "RealGlass/GlassPrePass"
{
    Properties
    {
        _MainTex ("Diffuse", 2D) = "white" {}
        _XYSMap ("Normal XY, specular", 2D) = "gray" {}
        _ACIMap ("Alpha, colour, illumination", 2D) = "black" {}
        _RGOffsetFactor ("Depth offset factor", Float) = 0
        _RGOffsetUnits ("Depth offset units", Float) = -1
    }

    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset [_RGOffsetFactor], [_RGOffsetUnits]
            Blend Off

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment fragPre
            #pragma multi_compile RG_NEAR RG_LOD RG_GROUP
            #include "GlassSurface.cginc"

            void fragPre(v2f i, out float4 g0 : SV_Target0, out float4 g1 : SV_Target1)
            {
                GlassSurf s = (GlassSurf)0;
                SurfaceMask(i, s);
                SurfacePane(i, s);
                float id = IdOf(i.building);
                float hi = floor(id / 256.0);
                g0 = float4(OctEncode(s.np), s.rough, max(s.mask * s.strength, 0.001));
                g1 = float4(hi, id - hi * 256.0, s.P.y - s.baseY, s.footprint);
            }
            ENDCG
        }
    }
}
