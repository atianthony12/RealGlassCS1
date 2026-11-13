// Real Glass: the glass of a building, drawn a second time over the game's own draw of it.
//
// The mod draws every building again, right after the game does, with the same mesh, matrix and
// data (Harmony postfixes on the game's three building paths): RG_NEAR = BuildingAI.RenderMesh
// (one building), RG_LOD = Building.RenderLod (up to 8 instances of one asset's LOD mesh, placed by
// _BuildingLocations[int(color.a * 8)]), RG_GROUP = RenderGroup/MegaRenderGroup.RenderMesh (a
// 384 m block of the city baked into one mesh, world-space normals, atlas uvs). The vertex rules
// (GlassSurface.cginc) are the game's own (read from the disassembly of
// Custom/Buildings/Building/Default), so this draw lands on the game's depth exactly; Offset 0, -1
// settles the last bit. No slope factor: it pulls the glass forward by a pixel's worth of the
// facade's depth slope (decimetres from afar), and a window frame modelled that close in front of
// its glass would be drawn over.
//
// Which building a pixel belongs to comes from the same place on every tier: its colour index, the
// building's texel in the game's object colour map (near: _ObjectIndex, middle: _ObjectIndices[],
// far: the baked second uv) -- what the city walk knows its own building by.
//
// Queue 2499, the last of the opaque range: Unity draws it forward after the deferred final pass
// and BEFORE the game's fog ([ImageEffectOpaque]), so the glass is fogged like the rest of the
// city. What is not glass is discarded; glass is REPLACED (alpha = mask x strength). Behind it,
// by day, a dark inside; by night the game's own picture of the window, untouched -- which
// windows are lit, how brightly and in what colour stays each asset's and the game's.
//
// WHAT IT MIRRORS comes from its own pass (since 1.1.9): GlassPrePass.shader drew this same glass
// at the end of the deferred frame into a thin G-buffer, and GlassReflection.shader traced every
// glass pixel's reflection over the whole screen and blended it with last frame's, reprojected along
// the reflection's own virtual point (GlassPrePass.cs) -- the history a forward pass drawn building
// by building can never keep, and what calms the reflections under TAA. Here it is read back, one
// texel: _RGReflection.
Shader "RealGlass/Glass"
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
        Tags { "Queue" = "Geometry+499" "IgnoreProjector" = "True" "RenderType" = "Transparent" }

        Pass
        {
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset [_RGOffsetFactor], [_RGOffsetUnits]
            Blend SrcAlpha OneMinusSrcAlpha

            CGPROGRAM
            #pragma target 3.5
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile RG_NEAR RG_LOD RG_GROUP
            #include "GlassSurface.cginc"
            #include "Dirt.cginc"

            // The reflection pass's result (GlassPrePass.cs): rgb what this pixel's glass mirrors, a
            // the reflection's virtual distance (0: the pre-pass found no glass here).
            sampler2D _RGReflection;

            float4 frag(v2f i) : SV_Target
            {
                GlassSurf s = (GlassSurf)0;
                SurfaceMask(i, s);
                if (_RGMask.z > 0.5)
                    return float4(1.0, 0.0, 1.0, max(s.mask, 0.35));
                SurfacePane(i, s);

                float3 P = s.P;
                float3 n = s.n;
                float3 V = s.V;
                float3 t = s.t;
                float baseY = s.baseY;
                float footprint = s.footprint;
                float4 painted = s.painted;
                float3 albedo = s.albedo;
                float wall = s.wall;
                float3 np = s.np;
                float rough = s.rough;
                float mask = s.mask;
                float strength = s.strength;

                // The dirt's metre coordinate's gradients, here, outside every branch (Dirt.cginc).
                float2 dirtM = float2(dot(P, t), P.y);
                float4 dirtGrad = float4(ddx(dirtM), ddy(dirtM));

                // The glass's colour: the chosen glass, and as much of the building's own painted
                // glass colour as the "own colour" setting keeps. The data sheets' numbers are
                // LUMINOUS (visible light transmittance / reflectance, eye-weighted), so both
                // filters are scaled to a luminance of 1: "clarity 24%" lets 24% of the light
                // through, whatever the colour; "reflectivity 20%" reflects 20%.
                float3 own = NormaliseTint(albedo);
                float3 tint = NormaliseTint(lerp(s.tintOwn.rgb, own, s.tintOwn.a));
                if (_RGDebug.x > 9.5 && _RGDebug.x < 10.5)
                    return float4(lerp(float3(0.05, 0.15, 0.6), float3(1.0, 0.85, 0.2), s.lines), 1.0);   // 10: the painted lines (yellow) on the glass (blue)
                if (_RGDebug.x > 11.5 && _RGDebug.x < 12.5)
                    return float4(SunThrough().xxx, 1.0);   // 12: the sun's disc through the clouds (white all of it, black none)
                if (_RGDebug.x > 12.5 && _RGDebug.x < 13.5)
                    return float4(1.0 - s.roomShape, s.roomShape, 0.05, 1.0);   // 13: where a room can stand (green), not (red)
                if (_RGDebug.x > 13.5 && _RGDebug.x < 14.5)
                {
                    // 14: the rooms' frame (RoomFrameAt), rooms or none: a checker of its rooms -- bright where
                    // the storey is the asset's own (fitted), dim on the storey grid; red where its glass holds none.
                    RoomFrame rf = RoomFrameAt(P, n, t, i.building, baseY, 6.0);
                    float2 rc = RoomCellOf(rf.q, rf);
                    float3 rcol = lerp(float3(0.15, 0.3, 0.9), float3(0.95, 0.8, 0.2), frac((rc.x + rc.y) * 0.5) * 2.0);
                    rcol *= abs(rf.storey - max(_RGPane2.z, 2.0)) > 1e-3 ? 1.0 : 0.35;
                    return float4(rf.allowed < 0.5 ? float3(1.0, 0.05, 0.05) : rcol, 1.0);
                }
                // Through the glass the light takes its colour once. The reflection, twice: a
                // reflective coating sits behind the tinted lite (Solarcool on the second surface,
                // every tinted preset), so what it mirrors crosses the tint going in and coming
                // out; only the outer surface's own 4% comes back untinted (EN 410 / ISO 9050's
                // reflectance of a coated lite). With the colour at 60% and once, a blue sky
                // outweighed it: bronze glass mirrored a blue-grey sky (2026-10-04).
                float F0 = s.F0;
                float3 filter = tint / max(Luma(tint), 0.05);
                float3 twice = tint * tint;
                twice /= max(Luma(twice), 0.05);
                float outer = saturate(0.04 / max(F0, 1e-4));
                // coat = the head-on reflectance's colour (F0 x coat), no channel above 1.
                float3 coat = min(outer + (1.0 - outer) * twice, 1.0 / max(F0, 1e-4));

                float cosT = saturate(dot(np, -V));
                float Fs;
                float3 F = GlassFresnel(cosT, F0, coat, Fs);
                float3 R = reflect(V, np);

                float2 suv = i.screenPos.xy / i.screenPos.w;
                float3 gamePixel = Finite(tex2Dlod(_RGScene, float4(suv, 0.0, 0.0)).rgb);

                // What the glass reflects: the reflection pass's answer for this pixel. Where it found
                // no glass (it should not happen: the same meshes, the same depth test), the sky.
                float4 h = tex2Dlod(_RGReflection, float4(suv, 0.0, 0.0));
                float found = FiniteIn(h.a, 0.0, 1e6) > 0.0 ? 1.0 : 0.0;
                float3 refl = found > 0.5 ? Finite(h.rgb) : SkyRadiance(R, rough);

                // The reflection pass's own check views (1 the trace, 2 the depth, 5 the finished
                // picture reprojected, 6 the city walk, 9 the hierarchical depth) come through it;
                // 15: where the pre-pass left this glass pixel a reflection (green) or none (red).
                if ((_RGDebug.x > 0.5 && _RGDebug.x < 2.5) || (_RGDebug.x > 4.5 && _RGDebug.x < 6.5) || (_RGDebug.x > 8.5 && _RGDebug.x < 9.5))
                    return float4(found > 0.5 ? Finite(h.rgb) : float3(1.0, 0.0, 1.0), 1.0);
                if (_RGDebug.x > 14.5 && _RGDebug.x < 15.5)
                    return float4(found > 0.5 ? float3(0.1, 0.9, 0.2) : float3(1.0, 0.05, 0.05), 1.0);
                if (_RGDebug.x > 15.5 && _RGDebug.x < 16.5)
                    return float4(refl, 1.0);   // 16: what the glass mirrors, alone (the flicker harness's)
                if (_RGDebug.x > 2.5 && _RGDebug.x < 4.5)
                    return float4(gamePixel, 1.0);   // 3 (and 4, which was the plain-glass towers): the copy

                if (_RGDebug.x > 7.5 && _RGDebug.x < 8.5)
                {
                    // 8: the painted pane found round this pixel (green, its border yellow), or not
                    // (red: the panes' own grid).
                    float along = dot(P, t);
                    float pfound = painted.y > painted.x ? 1.0 : 0.0;
                    float border = pfound * (min(min(along - painted.x, painted.y - along), min(P.y - painted.z, painted.w - P.y)) < 0.1 ? 1.0 : 0.0);
                    return float4(lerp(lerp(float3(0.8, 0.1, 0.1), float3(0.1, 0.8, 0.2), pfound), float3(1.0, 0.9, 0.1), border), 1.0);
                }
                if (_RGDebug.x > 16.5 && _RGDebug.x < 17.5)
                {
                    // 17: each pane one flat colour from its identity (its cell, facade and building),
                    // bright where it is the painted window's, dim on the panes' own grid.
                    float pfid = FacadeId(n);
                    float pid = IdOf(i.building);
                    float3 pc = float3(Hash22(s.paneCell + float2(pfid * 17.0, pfid * 3.0) + pid * 0.37),
                                       Hash12(s.paneCell.yx + float2(pfid, pid * 0.11)));
                    pc = 0.2 + 0.8 * pc;
                    return float4(pc * (painted.y > painted.x ? 1.0 : 0.4), 1.0);
                }

                // Through the glass. By day the inside of a building, dark (a share of the sky's
                // light, GlassUniforms.InsideByDay), seen through the glass's clarity and colour. By
                // night the game's own picture of this window, untouched: which windows are lit, how
                // brightly and in what colour is each asset's own night texture and the game's
                // timing -- unless the rooms are on for this building (below). The reflections go on
                // top either way.
                float daylight = _RGLight.x;
                float3 through = min(s.clarity * filter, 1.0) * (1.0 - Fs);
                float3 inside = _RGAmbSky.rgb * _RGLight.y * through;

                // Dirt on the pane (Dirt.cginc), where the slider is above 0: what is behind seen
                // through less of it (OURS only: the inside by day, our rooms -- never the game's own
                // lit window), the reflection dulled, a veil of grime lit by the sky and a little sun
                // (a little: this pass knows no cast shadows).
                float dirt = 0.0;
                [branch]   // at 0 (every player's default) none of it runs: no reads, no hashes
                if (s.dirt > 0.0)
                {
                    DirtIn di;
                    di.P = P;
                    di.n = n;
                    di.t = t;
                    di.footprint = footprint;
                    di.building = IdOf(i.building);
                    di.facade = FacadeId(n);
                    di.paneCell = s.paneCell;
                    di.paneLocal = s.paneLocal;
                    di.painted = painted.y > painted.x ? 1.0 : 0.0;
                    di.grad = dirtGrad;
                    di.flat = s.roomShape;
                    di.own = s.dirtOwn;
                    di.builtUp = _RGDirt.z;
                    dirt = DirtCover(di, s.dirt);
                }
                if (_RGDebug.x > 17.5 && _RGDebug.x < 18.5)
                    return float4(dirt.xxx, 1.0);   // 18: the dirt's cover (white all grime)

                // The asset's painted lines (PaintedWall): the glass steps aside there and the
                // building shows as its author painted it. (_RGGlass2.z, the preview's other way:
                // the reflection kept over them, the painting under it instead of the dark inside.)
                float coverage = mask * strength;
                float aside = _RGGlass2.z > 0.5 ? 0.0 : wall;   // where the glass steps aside for them
                if (_RGGlass2.z > 0.5)
                    inside = lerp(inside, gamePixel * (1.0 - Fs), wall);
                coverage *= 1.0 - aside;

                // The rooms behind the glass (Interiors.cginc), where they are on for this building:
                // OURS at night, in place of the game's own window, and by day too when asked; else
                // today's dark inside by day. At night the room's light is not dimmed by the clarity
                // (its brightness is the slider's), only coloured a little by the glass (round 5's).
                float3 nightFilter = lerp(float3(1.0, 1.0, 1.0), filter, 0.3);
                nightFilter /= max(Luma(nightFilter), 0.05);
                float3 roomNight, roomDay;
                float rooms = RoomsBehind(P, n, t, V, baseY, footprint, painted, IdOf(i.building), i.building, s.asset.a, s.illumination,
                                          inside, through, nightFilter * (1.0 - Fs), roomNight, roomDay);
                if (dirt > 0.0)
                {
                    // After the rooms: their day picture starts from inside (dimmed once, not twice).
                    inside *= 1.0 - 0.6 * dirt;
                    roomNight *= 1.0 - 0.6 * dirt;
                    roomDay *= 1.0 - 0.6 * dirt;
                }
                float3 behind = lerp(gamePixel, inside, daylight);
                float roomCover = 0.0, roomCoverNight = 0.0;
                if (rooms > 0.0)
                {
                    rooms *= s.roomShape;   // none on sloped, curved or smooth-shaded glass: the game's window there
                    float byDay = _RGRooms.y;
                    // At night the room fills the whole window the game lights, painted lines and
                    // all: the lines are drawn here as painted, by the sky's light alone -- a frame
                    // does not glow when the building's lights come on.
                    roomNight = lerp(roomNight, albedo * _RGAmbSky.rgb, aside);
                    behind = lerp(lerp(gamePixel, roomNight, rooms), lerp(inside, roomDay, rooms * byDay), daylight);
                    roomCover = rooms * byDay * daylight;
                    roomCoverNight = rooms * (1.0 - daylight);
                }

                float3 front = F * refl * _RGRefl.x + SunGlint(np, V, Fs, rough) * (F / max(Fs, 1e-4));
                if (dirt > 0.0)
                {
                    // A dark, thin veil (the author's call after a first look in the game, 2026-11-13):
                    // a third of dust's albedo, the sun a tenth (no cast shadows here).
                    float3 grimeLight = 0.5 * (_RGAmbSky.rgb + _RGAmbGround.rgb) +
                                        0.1 * _RGSunColor.rgb * saturate(dot(n, _RGSunDir.xyz)) * _RGSunDir.w * SunThrough();
                    front = front * (1.0 - 0.5 * dirt) + dirt * float3(0.12, 0.11, 0.10) * grimeLight;
                }

                // What is behind the glass covers the whole window where it is a room (no share of
                // the game's own lit window under an unlit room), the reflections only at the glass's
                // strength: out = coverage x front + cover x behind, written for SrcAlpha blending as
                // that / cover. By night the room's window (RoomWindow, the lines in it), by day the
                // glass. With no rooms cover = coverage: front + behind, as always.
                float full = coverage / max(strength, 1e-4);
                float cover = coverage + (full - coverage) * roomCover + (s.roomWindow - coverage) * roomCoverNight;
                float3 L = (coverage * front + cover * behind) / max(cover, 1e-4);

                // The last guard, on every API (Finite.cginc): whatever slipped through, the pixel
                // is light that is a number, and a coverage that is not one is none -- the game's
                // own glass shows there instead of a NaN reaching the frame (and, through the
                // finished picture, next frame's reflections).
                return float4(Finite(L), FiniteAlpha(cover));
            }
            ENDCG
        }
    }
}
