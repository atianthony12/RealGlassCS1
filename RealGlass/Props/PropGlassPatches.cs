using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealGlass.Props
{
    // The props' glass hooks (2.0.0; IL 2026-11-08). Every near prop -- one standing on its own, a
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
        public static void Postfix(RenderManager.CameraInfo cameraInfo, PropInfo info, InstanceID id, Vector3 position, Quaternion rotation, float scale, float angle)
        {
            // The table look-up first: the matrix is made only for a prop that was chosen.
            if (PropGlassDraw.Active && PropGlassDraw.IsChosen(info))
                PropGlassDraw.NoteNear(cameraInfo, info, position, Matrix4x4.TRS(position, rotation, new Vector3(scale, scale, scale)));
            // The prop chosen in a building's props list, outlined where it was drawn (PerAsset.PropOutline).
            if (info == PerAsset.PropOutline.Watched && info != null)
                PerAsset.PropOutline.Note(id, position, scale, angle);
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
        public static void Postfix(RenderManager.CameraInfo cameraInfo, PropInfo info, InstanceID id, Matrix4x4 matrix, Vector3 position, float scale, float angle)
        {
            if (PropGlassDraw.Active)
                PropGlassDraw.NoteNear(cameraInfo, info, position, matrix);
            if (info == PerAsset.PropOutline.Watched && info != null)
                PerAsset.PropOutline.Note(id, position, scale, angle);
        }
    }

    // The props gathered so far drawn after each manager that draws props: its own (PropManager), the
    // buildings' (BuildingManager.EndRenderingImpl -> BuildingAI.RenderProps), the roads' (NetManager).
    // Whatever order the game calls them in, every prop of the frame is flushed before the camera
    // renders. Last, so a prop another mod's postfix draws there is in the same frame.

    /// <summary>
    /// Intersection Marking Tool's props (2.0.0; IL of IMT 2140418403, 2026-11-09): IMT draws the props
    /// of its markings itself, in the static IMT.Utilities.MarkingPropData.RenderInstance(cameraInfo,
    /// info, id, item, objectIndex, active) -- run from its postfixes on NetNode / NetSegment
    /// .RenderInstance, inside the game's road drawing -- ending in Graphics.DrawMesh(info.m_mesh, its
    /// own matrix, info.m_material, the road layer, null, 0, block) near, the LOD's further: never
    /// through PropInstance.RenderInstance. A transpiler hands each of its draws, with the PropInfo
    /// (argument 1), to PropGlassDraw.NoteImt (the vehicles' hook: the call itself never touched).
    /// Found by name: without IMT, or with an IMT whose method is gone, Prepare says no and nothing
    /// is patched (one line). Flushed with the roads' props (NetManager.EndRenderingImpl).
    /// <para>The type is asked of IMT's own assembly alone, found by its name among the loaded ones
    /// (IntersectionMarkingTool, read from the DLL 2026-11-14), and only once per loaded copy of this
    /// mod (a copy Steam swaps in while the game runs looks again): never a sweep
    /// of every loaded assembly's types (Harmony's AccessTools.TypeByName), which Harmony would run
    /// several times here (Prepare twice, TargetMethod) and which can run in the middle of a city
    /// when Steam updates a mod while the game runs.</para>
    /// </summary>
    [HarmonyPatch]
    public static class PropImtPatch
    {
        private const string AssemblyName = "IntersectionMarkingTool";
        private const string TypeName = "IMT.Utilities.MarkingPropData";

        // The one lookup, shared by both Prepare calls and TargetMethod: the method, or null.
        private static bool _looked;
        private static bool _installed;
        private static MethodBase _target;

        private static MethodBase Target()
        {
            if (_looked)
                return _target;
            _looked = true;

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                bool ours = false;
                try
                {
                    if (assembly.GetName().Name != AssemblyName)
                        continue;
                    ours = true;
                    _installed = true;
                    Type type = assembly.GetType(TypeName, false);
                    if (type == null)
                        continue;   // another assembly of that name may have it
                    foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        // The name first: only this method's parameter types are resolved.
                        if (m.Name != "RenderInstance")
                            continue;
                        ParameterInfo[] parameters = m.GetParameters();
                        if (parameters.Length >= 2 && parameters[1].ParameterType == typeof(PropInfo))
                        {
                            _target = m;
                            break;
                        }
                    }
                    if (_target != null)
                        break;
                }
                catch (Exception e)
                {
                    // An assembly that cannot be asked is not the one we look for; IMT's own failing is said.
                    if (ours)
                        Log.Warn("props: looking for Intersection Marking Tool's prop drawing: " + e.Message);
                }
            }

            // Said once per loaded copy of this mod; silence when IMT is not installed.
            if (_installed)
                Log.Msg("props: Intersection Marking Tool " + (_target != null ? "found: its props get ours too" : "found, but not its prop drawing: its props stay as it draws them"));
            return _target;
        }

        public static bool Prepare()
        {
            try
            {
                return Target() != null;
            }
            catch (Exception e)
            {
                Log.Warn("props: looking for Intersection Marking Tool's prop drawing: " + e.Message);
                return false;
            }
        }

        public static MethodBase TargetMethod()
        {
            return Target();
        }

        [HarmonyPriority(Priority.Last)]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            return Vehicles.VehicleDrawHook.AfterEachDraw(instructions, il, AccessTools.Method(typeof(PropGlassDraw), nameof(PropGlassDraw.NoteImt)),
                () => new CodeInstruction(OpCodes.Ldarg_1), "Intersection Marking Tool's MarkingPropData.RenderInstance", "props");
        }
    }

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
