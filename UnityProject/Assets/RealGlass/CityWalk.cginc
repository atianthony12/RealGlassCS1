// Real Glass -- the city seen from above (CityMap.cs) and the walk across it: what the glass's
// reflection (GlassCommon.cginc) and, with the facade cards, the water's (CityReflection.shader) meet
// where the screen has nothing for a reflected ray. Moved here unchanged from GlassCommon.cginc
// (2026-11-12) so that a shader without the glass's other uniforms can take it.
//
// Sources (credited in README.md and THIRD-PARTY-NOTICES.md): Amanatides & Woo, "A Fast Voxel
// Traversal Algorithm for Ray Tracing", Eurographics 1987; the kind of top-down map CRYENGINE's
// height-map ambient occlusion and Unreal's heightfield sky occlusion of reflections read.

#ifndef REALGLASS_CITYWALK_INCLUDED
#define REALGLASS_CITYWALK_INCLUDED

// The city from above (CityMap.cs): per cell the highest roof over it and whose it is.
sampler2D _RGCityMap;         // rg the roof's world height in 1/32 m, ba the building's id (0 none): 4 m cells
sampler2D _RGCityCoarse;      // the same for blocks of 8 x 8 cells: each block's highest roof
float4 _RGCityMapST;          // xy the map's corner (world x, z), z metres a cell, w 1 while a map is bound
float4 _RGCityMapInfo;        // x cells a side, y cells a block, z the highest roof in the map (world y)

float4 _RGBudget;     // x city walk cells, y its blocks, z -, w steps each way up and down to a painted pane's edges

// ---------------------------------------------------------------------------------------------
// The buildings round a facade, where the screen cannot show them: the reflected ray walked across
// the city seen from above (CityMap.cs), cell by cell -- Amanatides & Woo's grid traversal (1987):
// from one cell to the next is two compares and an add. The first cell of another building whose
// roof the ray passes under is what the glass mirrors. 4 m cells for the first ~250 m, then 32 m
// blocks, each its highest roof, out to ~1 km. The same kind of top-down map as CRYENGINE's
// height-map ambient occlusion and Unreal's heightfield sky occlusion of reflections.
// ---------------------------------------------------------------------------------------------

float2 DecodeCity(float4 cell)
{
    float4 b = floor(cell * 255.0 + 0.5);
    return float2((b.r * 256.0 + b.g) * (1.0 / 32.0), b.b + b.a * 256.0);
}

// True when the ray from origin along R runs into a building other than ownId before it leaves the
// map, climbs over every roof or reaches groundY. tHit: metres along R; hitN: the side it met (the
// roof's +y when it came down onto it).
bool CityHit(float3 origin, float3 R, float ownId, float groundY, out float tHit, out float hitId, out float3 hitN)
{
    tHit = 0.0;
    hitId = 0.0;
    hitN = float3(0.0, 1.0, 0.0);
    bool hit = false;
    float top = _RGCityMapInfo.z;
    bool done = R.y >= 0.0 && origin.y >= top;
    float t = 0.0;

    [unroll]
    for (int phase = 0; phase < 2; phase++)
    {
        float block = phase == 0 ? 1.0 : _RGCityMapInfo.y;
        float cellSize = _RGCityMapST.z * block;
        float count = _RGCityMapInfo.x / block;
        float2 p = (origin.xz + R.xz * t - _RGCityMapST.xy) / cellSize;
        float2 d = R.xz / cellSize;
        d = float2(abs(d.x) > 1e-7 ? d.x : 1e-7, abs(d.y) > 1e-7 ? d.y : 1e-7);
        float2 stepDir = float2(d.x > 0.0 ? 1.0 : -1.0, d.y > 0.0 ? 1.0 : -1.0);
        float2 c = floor(p);
        float2 tDelta = abs(1.0 / d);
        float2 tMax = t + (c + max(stepDir, 0.0) - p) / d;
        float side = -1.0;
        float steps = phase == 0 ? _RGBudget.x : _RGBudget.y;

        [loop]
        for (int i = 0; i < 128; i++)
        {
            if (i >= steps)
                break;   // this phase's budget spent: on to the coarse blocks
            if (done || any(c < 0.0) || any(c >= count))
            {
                done = true;
                break;
            }

            float tExit = min(tMax.x, tMax.y);
            float4 texel = phase == 0 ? tex2Dlod(_RGCityMap, float4((c + 0.5) / count, 0.0, 0.0))
                                      : tex2Dlod(_RGCityCoarse, float4((c + 0.5) / count, 0.0, 0.0));
            float2 roof = DecodeCity(texel);
            float yIn = origin.y + R.y * t;
            float yOut = origin.y + R.y * tExit;
            if (roof.y > 0.5 && abs(roof.y - ownId) > 0.5 && min(yIn, yOut) < roof.x)
            {
                hit = true;
                done = true;
                hitId = roof.y;
                if (yIn < roof.x)
                {
                    tHit = t;
                    hitN = side > 0.5 ? float3(0.0, 0.0, -stepDir.y)
                         : (side > -0.5 ? float3(-stepDir.x, 0.0, 0.0) : normalize(float3(-R.x, 0.0, -R.z) + float3(1e-4, 0.0, 0.0)));
                }
                else
                {
                    tHit = (roof.x - origin.y) / min(R.y, -1e-4);
                }
                break;
            }

            // Over every roof and climbing, or down at the ground (the ground fill's): nothing more.
            if ((R.y >= 0.0 && yOut > top) || yOut < groundY)
            {
                done = true;
                break;
            }

            if (tMax.x < tMax.y)
            {
                c.x += stepDir.x;
                t = tMax.x;
                tMax.x += tDelta.x;
                side = 0.0;
            }
            else
            {
                c.y += stepDir.y;
                t = tMax.y;
                tMax.y += tDelta.y;
                side = 1.0;
            }
        }
    }
    return hit;
}

#endif
