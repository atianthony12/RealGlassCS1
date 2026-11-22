// The preview's stand-in for the game's distance fog over the opaque scene (FoamPreview "screenfog 1",
// 2026-11-21, the one mirror round's fog gate): Hidden/DayNight/Fog's shape as GlassUtil's SkyFog ports it
// for a sky pixel -- the distance term 1 - exp(-saturate(d x _FogParams0.z - 1) x _FogParams0.w x
// _FogParams0.y), x the 3D noise's 30 steps of _FogNoiseGainConstrastStepScale.z metres toward the eye (the
// preview binds a flat 0.5 noise, so its sum is closed-form), x the height fade 1 - saturate(y x
// _FogParams1.x) -- on every pixel that is not sky, by the depth texture the passes after the water read
// (the water's surface merged in). Its colour: the live sky's horizon in the pixel's direction (the game's is
// the sky's own scattering over the fog's path). Blended as the game's lerp. Preview only.
Shader "Hidden/RealGlassPreviewScreenFog"
{
    CGINCLUDE
    #include "UnityCG.cginc"
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    float4 _FogParams0, _FogParams1, _FogPollutionParams, _FogNoiseGainConstrastStepScale;
    samplerCUBE _PreviewFogSky;

    struct v2f
    {
        float4 pos : SV_POSITION;
        float3 dir : TEXCOORD0;
        float4 screen : TEXCOORD1;
    };

    v2f vert(float4 vertex : POSITION)
    {
        v2f o;
        o.pos = mul(UNITY_MATRIX_VP, float4(_WorldSpaceCameraPos + vertex.xyz * 500.0, 1.0));
        #if defined(UNITY_REVERSED_Z)
        o.pos.z = 1.0e-6 * o.pos.w;
        #else
        o.pos.z = 0.999999 * o.pos.w;
        #endif
        o.dir = vertex.xyz;
        o.screen = ComputeScreenPos(o.pos);
        return o;
    }

    float4 frag(v2f i) : SV_Target
    {
        float3 d = normalize(i.dir);
        float eye = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(i.screen.xy / i.screen.w, 0.0, 0.0)));
        float sky = step(0.98 * _ProjectionParams.z, eye);
        float3 forward = -normalize(UNITY_MATRIX_V[2].xyz);
        float dist = eye / max(dot(d, forward), 1e-3);
        float3 p = _WorldSpaceCameraPos + d * dist;
        float4 f0 = _FogParams0.y > 0.0 ? _FogParams0 : float4(0.0, 0.00223, 1.0 / 596.0, 10136.0);
        float hx = _FogParams1.x > 0.0 ? _FogParams1.x : 1.0 / 5000.0;
        float stepM = _FogNoiseGainConstrastStepScale.z > 0.0 ? _FogNoiseGainConstrastStepScale.z : 200.0;
        float distant = 1.0 - exp(-saturate(dist * f0.z - 1.0) * f0.w * f0.y);
        float noise = 0.0;
        [unroll]
        for (int k = 0; k < 30; k++)
            noise += 0.5 * saturate((dist - k * stepM) / stepM);
        noise = saturate(1.0 - exp2(noise * -0.0480898358) + _FogPollutionParams.w);
        float amount = noise * distant * (1.0 - saturate(p.y * hx)) * (1.0 - sky);
        float3 colour = texCUBElod(_PreviewFogSky, float4(normalize(float3(d.x, 0.02, d.z)), 3.0)).rgb;
        return float4(colour, saturate(amount));
    }
    ENDCG

    SubShader
    {
        Pass
        {
            ZWrite Off ZTest Always Cull Off Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            ENDCG
        }
    }
    Fallback Off
}
