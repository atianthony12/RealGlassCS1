// The mirror measurement's probe (FoamPreview "mirrorprobe", 2026-11-21; preview only, the mod never
// draws it): the terms RealGlass/CityReflection's composite works with, written out per pixel instead of
// composited, read with the same reads the composite makes (the flat-mirror path, _RGWaterWave.w = 0, as
// the mod runs it). Drawn round the camera after the reflection's buffer, into float targets.
//   0: x rho_flat = RGSeaRho(-d.y); y lum(SkyEstimate(flat mirror ray)); z Schlick for flat water at the
//      view's angle (0.0204 + 0.9796 (1 - mu)^5, the sky layer's); w the share covered (k / c.a).
//      Off the water: x = -1.
//   1: rgb the light the composite adds (c.rgb / c.a), w lum(base) (its half-size frame copy at the pixel).
//      Where the trace has no water (c.a <= 1e-3): w = -1.
//   2: a full-screen pass into an 8 x 1 target: the reflection's sky cube (_RGWaterTheme) along
//      _ProbeDirs[i] at mip _ProbeDirs[i].w, i the texel.
Shader "Hidden/RealGlassPreviewMirrorProbe"
{
    Properties
    {
        _MainTex ("", 2D) = "black" {}
    }

    CGINCLUDE
    #include "UnityCG.cginc"
    #include "../RealGlass/Finite.cginc"
    #include "../RealGlass/WaterCommon.cginc"

    sampler2D _RGWaterSurfaceDepth;
    sampler2D _RGWaterFrame;
    sampler2D _RGWaterStreaked;
    sampler2D _RGWaterStreaked2;
    samplerCUBE _RGWaterTheme;
    float4 _RGWaterGeom;
    float4 _RGWaterLight;
    float4 _ProbeDirs[8];

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

    // 1 on visible water, else 0 (a number, not a bool: HLSLcc's "not" of a combined test is never false).
    float OnWater(float3 d, float2 uv)
    {
        float eye = tex2Dlod(_RGWaterSurfaceDepth, float4(uv, 0.0, 0.0)).r;
        return step(1e-9, eye) * step(d.y, -1e-4);
    }

    float4 fragTerms(v2f i) : SV_Target
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;
        if (OnWater(d, uv) < 0.5)
            return float4(-1.0, 0.0, 0.0, 0.0);
        const float3 lum = float3(0.2126, 0.7152, 0.0722);
        float mu = saturate(-d.y);
        float rho = RGSeaRho(mu);
        float3 mirror = float3(d.x, -d.y, d.z);
        float3 sky = Finite(RGSkyLight(texCUBElod(_RGWaterTheme, float4(mirror, _RGWaterGeom.w)).rgb * _RGWaterLight.rgb, mirror));
        float c = 1.0 - mu;
        float c2 = c * c;
        float schlick = 0.0204 + 0.9796 * c2 * c2 * c;
        float4 t = tex2Dlod(_RGWaterStreaked, float4(uv, 0.0, 0.0));
        float k = tex2Dlod(_RGWaterStreaked2, float4(uv, 0.0, 0.0)).r;
        float covered = t.a > 1e-3 ? saturate(k / t.a) : 0.0;
        return float4(rho, dot(sky, lum), schlick, covered);
    }

    float4 fragAdded(v2f i) : SV_Target
    {
        float3 d = normalize(i.dir);
        float2 uv = i.screen.xy / i.screen.w;
        const float3 lum = float3(0.2126, 0.7152, 0.0722);
        float4 t = tex2Dlod(_RGWaterStreaked, float4(uv, 0.0, 0.0));
        if (OnWater(d, uv) * step(1e-3, t.a) < 0.5)
            return float4(0.0, 0.0, 0.0, -1.0);
        float3 added = Finite(t.rgb / t.a);
        float3 base = Finite(tex2Dlod(_RGWaterFrame, float4(uv, 0.0, 0.0)).rgb);
        return float4(added, dot(base, lum));
    }

    float4 fragCube(v2f_img i) : SV_Target
    {
        int k = (int)min(floor(i.uv.x * 8.0), 7.0);
        float4 dir = _ProbeDirs[k];
        return float4(texCUBElod(_RGWaterTheme, float4(dir.xyz, dir.w)).rgb, 1.0);
    }
    ENDCG

    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragTerms
            #pragma target 3.5
            ENDCG
        }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment fragAdded
            #pragma target 3.5
            ENDCG
        }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend Off
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment fragCube
            #pragma target 3.5
            ENDCG
        }
    }
    Fallback Off
}
