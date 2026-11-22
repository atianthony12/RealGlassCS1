// Stand-ins for the passes after the water that read the depth texture or reshape the picture, for
// tools/issue-probe (IssueProbe.cs). Not the real effects: their shape only.
//   0 THE GAME'S FOG (as Editor/PreviewFogSky's pass 0, from the game's disassembly): a sky pixel below
//     the horizon (the void past the map's edge) is filled with the fog's colour; elsewhere a distance fog.
//   1 VOLUMETRIC WEATHER'S CLOUDS: sky pixels above the horizon covered at 80%.
//   2 AMBIENT OCCLUSION (Render It's / Lumina's): darkens a pixel by how many depth neighbours stand nearer.
//   3 DEPTH OF FIELD (Render It's, the game's tilt-shift): a gather whose radius follows each pixel's depth.
//   4 TONEMAP + SHARPEN (Render It's colour grading and sharpening, Lumina's tonemapping).
//   5 BLOOM, bright part (half size); 6 its blur (pass 4 adds it back).
Shader "Hidden/IssueProbeEffects"
{
    Properties { _MainTex ("", 2D) = "black" {} }

    CGINCLUDE
    #include "UnityCG.cginc"
    UNITY_DECLARE_DEPTH_TEXTURE(_CameraDepthTexture);
    sampler2D _MainTex;
    float4 _MainTex_TexelSize;
    sampler2D _BloomTex;
    float4 _ProbeFogColour;
    float4 _ProbeCloudColour;
    float4 _ProbeDof;        // x focus distance (m), y range (m), z largest radius (px)
    float4 _ProbeAo;         // x radius (px), y strength
    float4 _ProbePost;       // x exposure, y sharpen, z bloom threshold, w bloom amount

    struct v2fCube
    {
        float4 pos : SV_POSITION;
        float3 dir : TEXCOORD0;
        float4 screen : TEXCOORD1;
    };

    v2fCube vertCube(float4 vertex : POSITION)
    {
        v2fCube o;
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

    float EyeAt(float2 uv)
    {
        return LinearEyeDepth(SAMPLE_DEPTH_TEXTURE_LOD(_CameraDepthTexture, float4(uv, 0.0, 0.0)));
    }

    float4 fragFog(v2fCube i) : SV_Target
    {
        float eye = EyeAt(i.screen.xy / i.screen.w);
        bool sky = eye >= 0.98 * _ProjectionParams.z;
        float amount = sky ? (normalize(i.dir).y < 0.0 ? 1.0 : 0.0) : 1.0 - exp(-eye / 1500.0);
        return float4(_ProbeFogColour.rgb, amount);
    }

    float4 fragClouds(v2fCube i) : SV_Target
    {
        bool sky = EyeAt(i.screen.xy / i.screen.w) >= 0.98 * _ProjectionParams.z;
        return float4(_ProbeCloudColour.rgb, sky && normalize(i.dir).y >= 0.0 ? 0.8 : 0.0);
    }

    struct v2fQuad
    {
        float4 pos : SV_POSITION;
        float2 uv : TEXCOORD0;
    };

    v2fQuad vertQuad(appdata_img v)
    {
        v2fQuad o;
        o.pos = UnityObjectToClipPos(v.vertex);
        o.uv = v.texcoord;
        return o;
    }

    float4 fragAo(v2fQuad i) : SV_Target
    {
        float4 c = tex2D(_MainTex, i.uv);
        float eye = EyeAt(i.uv);
        float occluded = 0.0;
        for (int k = 0; k < 8; k++)
        {
            float a = 6.2831853 * k / 8.0;
            for (int r = 1; r <= 2; r++)
            {
                float2 o = float2(cos(a), sin(a)) * _ProbeAo.x * r * 0.5 * _MainTex_TexelSize.xy;
                float d = EyeAt(i.uv + o);
                float nearer = eye - d;
                occluded += (nearer > 0.02 * eye && nearer < 0.5 * eye) ? 1.0 : 0.0;
            }
        }
        return float4(c.rgb * (1.0 - _ProbeAo.y * occluded / 16.0), c.a);
    }

    float Coc(float eye)
    {
        return saturate(abs(eye - _ProbeDof.x) / _ProbeDof.y) * _ProbeDof.z;
    }

    float4 fragDof(v2fQuad i) : SV_Target
    {
        float centre = Coc(EyeAt(i.uv));
        float3 sum = tex2D(_MainTex, i.uv).rgb;
        float weight = 1.0;
        for (int k = 0; k < 16; k++)
        {
            float a = 6.2831853 * (k + 0.5) / 16.0;
            float rr = (k % 2 == 0 ? 0.5 : 1.0) * _ProbeDof.z;
            float2 o = float2(cos(a), sin(a)) * rr * _MainTex_TexelSize.xy;
            float theirs = Coc(EyeAt(i.uv + o));
            // A neighbour counts when its own circle (or this pixel's) reaches this far.
            float w = saturate(max(centre, theirs) - rr + 1.0);
            sum += tex2D(_MainTex, i.uv + o).rgb * w;
            weight += w;
        }
        return float4(sum / weight, 1.0);
    }

    float3 Tonemap(float3 c)
    {
        c *= _ProbePost.x;
        return c / (1.0 + c);
    }

    float4 fragPost(v2fQuad i) : SV_Target
    {
        float2 t = _MainTex_TexelSize.xy;
        float3 c = tex2D(_MainTex, i.uv).rgb;
        float3 n = tex2D(_MainTex, i.uv + float2(t.x, 0)).rgb + tex2D(_MainTex, i.uv - float2(t.x, 0)).rgb
                 + tex2D(_MainTex, i.uv + float2(0, t.y)).rgb + tex2D(_MainTex, i.uv - float2(0, t.y)).rgb;
        float3 sharp = max(c + _ProbePost.y * (c - 0.25 * n), 0.0);
        float3 bloom = tex2D(_BloomTex, i.uv).rgb;
        float3 mapped = Tonemap(sharp + _ProbePost.w * bloom);
        return float4(pow(saturate(mapped), 1.0 / 2.2), 1.0);
    }

    float4 fragBright(v2fQuad i) : SV_Target
    {
        float2 t = _MainTex_TexelSize.xy;
        float3 c = 0.25 * (tex2D(_MainTex, i.uv + t * float2(-0.5, -0.5)).rgb + tex2D(_MainTex, i.uv + t * float2(0.5, -0.5)).rgb
                         + tex2D(_MainTex, i.uv + t * float2(-0.5, 0.5)).rgb + tex2D(_MainTex, i.uv + t * float2(0.5, 0.5)).rgb);
        return float4(max(c - _ProbePost.z, 0.0), 1.0);
    }

    float4 fragBlur(v2fQuad i) : SV_Target
    {
        float2 t = _MainTex_TexelSize.xy;
        float3 s = 0.0;
        for (int y = -2; y <= 2; y++)
            for (int x = -2; x <= 2; x++)
                s += tex2D(_MainTex, i.uv + t * float2(x, y)).rgb;
        return float4(s / 25.0, 1.0);
    }
    ENDCG

    SubShader
    {
        Pass { ZWrite Off ZTest Always Cull Off Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vertCube
            #pragma fragment fragFog
            #pragma target 3.5
            ENDCG }
        Pass { ZWrite Off ZTest Always Cull Off Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vertCube
            #pragma fragment fragClouds
            #pragma target 3.5
            ENDCG }
        Pass { ZWrite Off ZTest Always Cull Off
            CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragAo
            #pragma target 3.5
            ENDCG }
        Pass { ZWrite Off ZTest Always Cull Off
            CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragDof
            #pragma target 3.5
            ENDCG }
        Pass { ZWrite Off ZTest Always Cull Off
            CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragPost
            #pragma target 3.5
            ENDCG }
        Pass { ZWrite Off ZTest Always Cull Off
            CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragBright
            #pragma target 3.5
            ENDCG }
        Pass { ZWrite Off ZTest Always Cull Off
            CGPROGRAM
            #pragma vertex vertQuad
            #pragma fragment fragBlur
            #pragma target 3.5
            ENDCG }
    }
}
