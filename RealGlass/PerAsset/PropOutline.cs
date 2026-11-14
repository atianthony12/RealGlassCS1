using System;
using System.Collections.Generic;
using ColossalFramework;
using HarmonyLib;
using UnityEngine;

namespace RealGlass.PerAsset
{
    /// <summary>
    /// The prop chosen in a building's props list, outlined where it stands on that building (2.0.0, the
    /// author's call, 2026-11-14: "like how BOB does it"): the game's own prop outline
    /// (PropTool.RenderOverlay: a circle the prop's size, reaching 100 m up and down, so it marks the
    /// walls it stands against too) round every copy of it the game drew last frame on the picked
    /// building and its sub-buildings.
    /// </summary>
    /// <remarks>
    /// Where the copies are is taken from the game's own drawing, not worked out again: the props' draw
    /// hooks (Props.PropRenderRotatedPatch / PropRenderMatrixPatch) hand over each draw of the watched
    /// asset (<see cref="Watched"/>, null while nothing is outlined: one reference test a prop), with
    /// the building in its InstanceID (BuildingAI.GetPropRenderID). So a variation, a prop the game
    /// leaves out by its probability or another mod's replacement come out as drawn. Props bent over
    /// the ground or the water (other overloads) and props too far away to be drawn get no outline.
    /// Main thread. Any error switches it off for the session, with one line in the log.
    /// </remarks>
    public static class PropOutline
    {
        /// <summary>BOB's colour for a chosen prop: the outline stands out against any building.</summary>
        private static readonly Color Colour = new Color(1f, 0f, 1f, 0.8f);

        private const int MostCopies = 256;
        private const int MostBuildings = 16;

        private struct Copy
        {
            public Vector3 Position;
            public float Scale;
            public float Angle;
        }

        /// <summary>The asset whose draws are kept; null: none (the draw hooks test only this).</summary>
        public static PropInfo Watched;

        private static readonly ushort[] Buildings = new ushort[MostBuildings];
        private static int _buildingCount;
        private static List<Copy> _current = new List<Copy>();
        private static List<Copy> _last = new List<Copy>();
        private static int _frame = -1;
        private static bool _failed;

        /// <summary>A draw of the watched asset (the props' draw hooks, while <see cref="Watched"/> is it).</summary>
        public static void Note(InstanceID id, Vector3 position, float scale, float angle)
        {
            if (_failed)
                return;
            try
            {
                ushort building = 0;
                if (id.Type == InstanceType.Building)
                {
                    building = id.Building;
                }
                else if (id.Type == InstanceType.BuildingProp)
                {
                    int index;
                    id.GetBuildingProp(out building, out index);
                }
                if (building == 0 || !Watching(building))
                    return;

                int frame = Time.frameCount;
                if (frame != _frame)
                {
                    List<Copy> swap = _last;
                    _last = _current;
                    _current = swap;
                    _current.Clear();
                    if (_frame != frame - 1)
                        _last.Clear();
                    _frame = frame;
                }
                if (_current.Count < MostCopies)
                    _current.Add(new Copy { Position = position, Scale = scale, Angle = angle });
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        private static bool Watching(ushort building)
        {
            for (int i = 0; i < _buildingCount; i++)
            {
                if (Buildings[i] == building)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Once a frame in the game's overlay pass (after the props were drawn): the outlines, then what
        /// to watch next frame.
        /// </summary>
        public static void RenderOverlay(RenderManager.CameraInfo cameraInfo)
        {
            if (_failed)
                return;
            try
            {
                PropInfo shown = Watched;
                if (shown != null)
                {
                    // This frame's draws (the props are drawn before the overlay pass), or the last
                    // frame's when none came this one.
                    int frame = Time.frameCount;
                    List<Copy> copies = _frame == frame || _frame == frame - 1 ? _current : null;
                    if (copies != null)
                    {
                        for (int i = 0; i < copies.Count; i++)
                            PropTool.RenderOverlay(cameraInfo, shown, copies[i].Position, copies[i].Scale, copies[i].Angle, Colour);
                    }
                }

                Choose();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        /// <summary>
        /// What to watch: the prop chosen in the open props list of the picked building (its asset the
        /// list's), on that building and its sub-buildings; else nothing.
        /// </summary>
        private static void Choose()
        {
            PropInfo info = PropSelection.Selected;
            ushort main = AssetSelection.Building;
            if (info == null || main == 0 || !ModController.PropWindowListing || PropSelection.Building != AssetSelection.Info ||
                !Singleton<BuildingManager>.exists)
            {
                Stop();
                return;
            }

            if (info != Watched || _buildingCount == 0 || Buildings[0] != main)
            {
                Building[] buffer = Singleton<BuildingManager>.instance.m_buildings.m_buffer;
                _buildingCount = 0;
                ushort b = main;
                while (b != 0 && _buildingCount < MostBuildings)
                {
                    Buildings[_buildingCount++] = b;
                    b = buffer[b].m_subBuilding;
                }
                _current.Clear();
                _last.Clear();
                _frame = -1;
                Watched = info;
            }
        }

        public static void Stop()
        {
            Watched = null;
            _buildingCount = 0;
            _current.Clear();
            _last.Clear();
            _frame = -1;
        }

        private static void Fail(Exception e)
        {
            _failed = true;
            Watched = null;
            Log.Error("props: outlining the chosen prop failed; no outline from now on (the rest is unaffected)", e);
        }
    }

    /// <summary>
    /// The outline's place in the game's overlay pass: after the current tool's (ToolManager
    /// .EndOverlayImpl draws it), whatever tool is current, applied at startup with the rest. Not in
    /// RealGlass.Props: a hook that cannot be made takes the outline with it, not the props' glass.
    /// </summary>
    [HarmonyPatch(typeof(ToolManager), "EndOverlayImpl", new[] { typeof(RenderManager.CameraInfo) })]
    public static class PropOutlineOverlayPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(RenderManager.CameraInfo cameraInfo)
        {
            PropOutline.RenderOverlay(cameraInfo);
        }
    }
}
