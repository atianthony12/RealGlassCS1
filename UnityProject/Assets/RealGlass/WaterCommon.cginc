// The Water tab's shared maths (RealGlass/Water/WaterOptics.cs has the model and its sources).
#ifndef RG_WATER_COMMON
#define RG_WATER_COMMON

#include "Finite.cginc"   // what the water adds to the frame is a number, on every API

// The rough sea's reflectance of the sky (Cox & Munk slopes, Schlick's Fresnel for water): entry i
// at x = i / 32, mu = x^2, mu the sine of how far below the level one looks. x: rho.
float4 _RGSeaTable[33];

float RGSeaRho(float mu)
{
    float x = sqrt(saturate(mu)) * 32.0;
    int i0 = min(31, (int)floor(x));
    return lerp(_RGSeaTable[i0].x, _RGSeaTable[i0 + 1].x, x - i0);
}

// A real sky's LIGHT, when another mod publishes one (the author's own RealLight, internal: its sky
// under the cloud, linear, in the game's light units, 64 px; no Fresnel, no sun disc), and how much
// of it to use (0..1: its day weight; an unset global reads 0, so without it the theme's sky stays).
samplerCUBE _RLSkyLightCube;
float _RLSkyLightCubeWeight;

// The sky's light along d as the water should reflect it: the theme's cube lit by the water's light
// (themeLit), towards the published real sky by its weight.
float3 RGSkyLight(float3 themeLit, float3 d)
{
    float w = saturate(_RLSkyLightCubeWeight);
    if (w <= 0.0)
        return themeLit;
    return lerp(themeLit, texCUBElod(_RLSkyLightCube, float4(d, 0.0)).rgb, w);
}

#endif
