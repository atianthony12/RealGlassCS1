using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealGlass.Vehicles
{
    // The vehicles' glass hooks (IL, 2026-10-23). The game draws a vehicle near enough to be seen in full
    // with Graphics.DrawMesh(mesh, body matrix, material, layer, null, 0, VehicleManager.m_materialBlock)
    // -- the 7-argument overload -- in the static Vehicle.RenderInstance (a moving vehicle: its main mesh
    // and each sub-mesh its flags and variation allow; underground ones with m_undergroundMaterial) and in
    // VehicleParked.RenderInstance. The body matrix (VehicleAI.CalculateBodyMatrix: its sway included) is
    // a local there, gone by the time a postfix runs: so a transpiler, which after each of those calls
    // hands the same mesh, matrix, material and layer to VehicleGlassDraw, with the vehicle's id.
    //
    // The game's call is never removed, replaced or moved: its seven arguments are copied to locals and
    // put back before it, and ours runs after it. In their own namespace: a hook that cannot be applied
    // takes the vehicles' glass with it and nothing else (Patcher: only RealGlass.Glass's failures stop
    // the buildings' glass).
    //
    // Other mods first (2026-10-23, his call: an unmaintained mod must never be broken by ours). Both
    // transpilers are Priority.Last: Harmony runs a method's transpilers highest priority first, each on
    // the one before's output, and runs them all again whenever any mod patches the method -- so every
    // other mod's transpiler meets the game's own code, exactly as without Real Glass, whatever order the
    // mods were enabled in, and ours meets theirs. One that looks for the exact instructions round the
    // draw (FrameDoctor swaps the same kind of call in PropInstance.RenderInstance) finds them; one that
    // took the draw away leaves ours nothing to find: no glass on those vehicles. Ours fits round any
    // code: it only wraps the call, the stack as it was. And it never throws back at Harmony (that would
    // fail the OTHER mod's patching, the one that set the transpilers running): on anything unexpected
    // the code goes back as it came.

    /// <summary>The shared transpiler: after each of the game's 7-argument DrawMesh calls, our note with the same draw.</summary>
    internal static class VehicleDrawHook
    {
        internal static readonly MethodInfo GameDraw = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), new[]
        {
            typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock),
        });

        /// <param name="loadId">Loads the vehicle's id for <paramref name="note"/>'s last parameter (an argument of the original).</param>
        internal static IEnumerable<CodeInstruction> AfterEachDraw(IEnumerable<CodeInstruction> instructions, ILGenerator il, MethodInfo note,
                                                                  Func<CodeInstruction> loadId, string where)
        {
            const string subject = "vehicles";
            // The instructions as they came, never changed (new ones round them): on anything
            // unexpected they go back exactly so.
            var original = new List<CodeInstruction>(instructions);
            try
            {
                var result = new List<CodeInstruction>(original.Count + 64);
                LocalBuilder mesh = null, matrix = null, material = null, layer = null, camera = null, submesh = null, block = null;
                int hooked = 0, left = 0;
                foreach (CodeInstruction instruction in original)
                {
                    bool isDraw = GameDraw != null && note != null && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                                  instruction.operand is MethodInfo called && called == GameDraw;
                    if (!isDraw)
                    {
                        result.Add(instruction);
                        continue;
                    }

                    // A call never opens or closes an exception block (a try or a handler starts with
                    // an empty stack, and ends on a leave): should one ever carry a block, left alone.
                    if (instruction.blocks.Count > 0)
                    {
                        result.Add(instruction);
                        left++;
                        continue;
                    }

                    if (mesh == null)
                    {
                        mesh = il.DeclareLocal(typeof(Mesh));
                        matrix = il.DeclareLocal(typeof(Matrix4x4));
                        material = il.DeclareLocal(typeof(Material));
                        layer = il.DeclareLocal(typeof(int));
                        camera = il.DeclareLocal(typeof(Camera));
                        submesh = il.DeclareLocal(typeof(int));
                        block = il.DeclareLocal(typeof(MaterialPropertyBlock));
                    }

                    // The seven arguments are on the stack, the last on top: copy each into its local (the
                    // first store takes over any label on the call, with the stack exactly as the call had
                    // it), load them back, let the game's own call take them.
                    var first = new CodeInstruction(OpCodes.Stloc, block);
                    first.labels.AddRange(instruction.labels);
                    result.Add(first);
                    result.Add(new CodeInstruction(OpCodes.Stloc, submesh));
                    result.Add(new CodeInstruction(OpCodes.Stloc, camera));
                    result.Add(new CodeInstruction(OpCodes.Stloc, layer));
                    result.Add(new CodeInstruction(OpCodes.Stloc, material));
                    result.Add(new CodeInstruction(OpCodes.Stloc, matrix));
                    result.Add(new CodeInstruction(OpCodes.Stloc, mesh));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, mesh));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, matrix));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, material));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, layer));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, camera));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, submesh));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, block));
                    result.Add(new CodeInstruction(instruction.opcode, instruction.operand));   // the game's call, its labels now on the first store

                    // Then ours, with the draw the game just made.
                    result.Add(new CodeInstruction(OpCodes.Ldloc, mesh));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, matrix));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, material));
                    result.Add(new CodeInstruction(OpCodes.Ldloc, layer));
                    result.Add(loadId());
                    result.Add(new CodeInstruction(OpCodes.Call, note));
                    hooked++;
                }

                Log.Msg(subject + ": the glass's hook in " + where + ": after " + hooked + " of the game's draws (the game's own calls unchanged)" +
                        (left > 0 ? "; " + left + " left alone (in an exception block)" : "") +
                        (hooked == 0 ? " -- NONE FOUND: no glass on those " + subject : ""));
                return result;
            }
            catch (Exception e)
            {
                try
                {
                    Log.Error(subject + ": the glass's hook in " + where + " could not be made; the game's code is left as it was (no glass on those " +
                              subject + ")", e);
                }
                catch
                {
                    // the log itself failed: the code still goes back unchanged
                }
                return original;
            }
        }
    }

    /// <summary>A moving vehicle: the static Vehicle.RenderInstance, whose InstanceID (argument 13) names it.</summary>
    [HarmonyPatch]
    public static class VehicleRenderInstancePatch
    {
        public static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Vehicle), nameof(Vehicle.RenderInstance), new[]
            {
                typeof(RenderManager.CameraInfo), typeof(VehicleInfo), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Vector4),
                typeof(Vector4), typeof(Vector3), typeof(float), typeof(Color), typeof(Vehicle.Flags), typeof(Vehicle.Flags2), typeof(int),
                typeof(InstanceID), typeof(bool), typeof(bool),
            });
        }

        [HarmonyPriority(Priority.Last)]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            return VehicleDrawHook.AfterEachDraw(instructions, il, AccessTools.Method(typeof(VehicleGlassDraw), nameof(VehicleGlassDraw.NoteMoving)),
                () => new CodeInstruction(OpCodes.Ldarg_S, (byte)13), "Vehicle.RenderInstance");
        }
    }

    /// <summary>A parked vehicle: VehicleParked.RenderInstance(cameraInfo, parkedVehicleID), an instance method of the struct (argument 0 is it).</summary>
    [HarmonyPatch(typeof(VehicleParked), nameof(VehicleParked.RenderInstance), new[] { typeof(RenderManager.CameraInfo), typeof(ushort) })]
    public static class VehicleParkedRenderInstancePatch
    {
        [HarmonyPriority(Priority.Last)]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            return VehicleDrawHook.AfterEachDraw(instructions, il, AccessTools.Method(typeof(VehicleGlassDraw), nameof(VehicleGlassDraw.NoteParked)),
                () => new CodeInstruction(OpCodes.Ldarg_2), "VehicleParked.RenderInstance");
        }
    }

    /// <summary>
    /// The game has drawn every vehicle of the frame: their glass now, one draw per model. Last too, so
    /// a vehicle another mod's postfix draws there gets its glass in the same frame.
    /// </summary>
    [HarmonyPatch(typeof(VehicleManager), "EndRenderingImpl", new[] { typeof(RenderManager.CameraInfo) })]
    public static class VehicleManagerEndRenderingPatch
    {
        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            VehicleGlassDraw.Flush();
        }
    }
}
