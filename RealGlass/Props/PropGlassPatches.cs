using HarmonyLib;
using UnityEngine;

namespace RealGlass.Props
{
    // The props' glass hooks (1.1.9; IL 2026-11-08). Every near prop -- one standing on its own, a
    // building's, a road's -- is drawn by a static PropInstance.RenderInstance overload. Two of them
    // draw the props our glass can go over: the one with a rotation (its matrix TRS(position, rotation,
    // scale)) and the one with a matrix; the other two draw props bent over the ground's or the water's
    // map, which ours leaves alone (PropSelection.CanDraw). Each draws the prop's mesh near when it is
    // initialised, has a renderer, its layer is in the camera's mask and CheckRenderDistance(position,
    // m_lodRenderDistance) passes; else its LOD. Neither writes those arguments before (no starg).
    //
    // POSTFIXES, not a hook on the draw call: another mod may take the game's DrawMesh calls in these
    // methods over (a batcher, for one, rewrites them into its own), and a hook on the call then finds
    // nothing (2026-11-08, the first in-game session). A postfix runs whatever another mod did to the
    // body; it repeats the game's near test (PropGlassDraw.NoteNear) and draws over the prop at the
    // same matrix. The game's code is never changed. In their own namespace: a hook that cannot be made
    // takes the props' glass with it and nothing else.

    /// <summary>RenderInstance(cameraInfo, info, id, position, rotation, scale, angle, color, objectIndex, active, billboardRect).</summary>
    [HarmonyPatch(typeof(PropInstance), nameof(PropInstance.RenderInstance), new[]
    {
        typeof(RenderManager.CameraInfo), typeof(PropInfo), typeof(InstanceID), typeof(Vector3), typeof(Quaternion), typeof(float),
        typeof(float), typeof(Color), typeof(Vector4), typeof(bool), typeof(Vector4),
    })]
    public static class PropRenderRotatedPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(RenderManager.CameraInfo cameraInfo, PropInfo info, Vector3 position, Quaternion rotation, float scale)
        {
            if (PropGlassDraw.Active)
                PropGlassDraw.NoteNear(cameraInfo, info, position, Matrix4x4.TRS(position, rotation, new Vector3(scale, scale, scale)));
        }
    }

    /// <summary>RenderInstance(cameraInfo, info, id, matrix, position, scale, angle, color, objectIndex, active).</summary>
    [HarmonyPatch(typeof(PropInstance), nameof(PropInstance.RenderInstance), new[]
    {
        typeof(RenderManager.CameraInfo), typeof(PropInfo), typeof(InstanceID), typeof(Matrix4x4), typeof(Vector3), typeof(float),
        typeof(float), typeof(Color), typeof(Vector4), typeof(bool),
    })]
    public static class PropRenderMatrixPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix(RenderManager.CameraInfo cameraInfo, PropInfo info, Matrix4x4 matrix, Vector3 position)
        {
            if (PropGlassDraw.Active)
                PropGlassDraw.NoteNear(cameraInfo, info, position, matrix);
        }
    }

    // The props gathered so far drawn after each manager that draws props: its own (PropManager), the
    // buildings' (BuildingManager.EndRenderingImpl -> BuildingAI.RenderProps), the roads' (NetManager).
    // Whatever order the game calls them in, every prop of the frame is flushed before the camera
    // renders. Last, so a prop another mod's postfix draws there is in the same frame.

    [HarmonyPatch(typeof(PropManager), "EndRenderingImpl", new[] { typeof(RenderManager.CameraInfo) })]
    public static class PropManagerEndRenderingPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            PropGlassDraw.Flush();
        }
    }

    [HarmonyPatch(typeof(BuildingManager), "EndRenderingImpl", new[] { typeof(RenderManager.CameraInfo) })]
    public static class PropBuildingEndRenderingPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            PropGlassDraw.Flush();
        }
    }

    [HarmonyPatch(typeof(NetManager), "EndRenderingImpl", new[] { typeof(RenderManager.CameraInfo) })]
    public static class PropNetEndRenderingPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            PropGlassDraw.Flush();
        }
    }
}
