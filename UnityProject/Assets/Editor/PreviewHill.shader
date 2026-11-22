// The preview's hill for the mirror views (FoamPreview, 2026-11-21, a measurement of the water's
// reflection under clouds): plain Lambert ground, its albedo the mesh's vertex colour (patches of pale
// sand and dark vegetation), drawn in the deferred path as the preview's ground is. Preview only.
Shader "RealGlass/Preview/Hill"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        CGPROGRAM
        #pragma surface surf Lambert
        #pragma target 3.5

        struct Input
        {
            float4 color : COLOR;
        };

        void surf(Input i, inout SurfaceOutput o)
        {
            o.Albedo = i.color.rgb;
        }
        ENDCG
    }
    Fallback Off
}
