using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RealGlass.Water
{
    /// <summary>
    /// The foam's hook: in TerrainPatch.Render and TerrainPatch.RenderSubPatch (IL 2026-10-06: each
    /// patch's water is one Graphics.DrawMesh of a generic grid with the water material and the shared
    /// TerrainManager.m_materialBlock2, just filled for that patch), every call of the 9-argument
    /// Graphics.DrawMesh goes through <see cref="WaterFoam.DrawMesh"/> instead, which makes the game's
    /// call exactly as it was and then, for the water, draws the foam. The game's draw is never skipped
    /// or changed. A transpiler, not a postfix: the mesh, matrix and property block the game draws with
    /// are its locals, gone by the time a postfix runs. When the game's code changes and no call is
    /// found, nothing is replaced and the log says so (no foam; nothing else touched).
    /// </summary>
    /// <remarks>
    /// Other mods first, as the vehicles' hooks (VehicleGlassPatches.cs has the why): Priority.Last, so
    /// every other mod's transpiler on these methods meets the game's own code; and on anything
    /// unexpected the code goes back as it came, never an exception into the other mod's patching.
    /// </remarks>
    [HarmonyPatch]
    public static class WaterFoamDrawPatch
    {
        private static readonly MethodInfo GameDraw = AccessTools.Method(typeof(Graphics), nameof(Graphics.DrawMesh), new[]
        {
            typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock),
            typeof(bool), typeof(bool),
        });

        private static readonly MethodInfo OurDraw = AccessTools.Method(typeof(WaterFoam), nameof(WaterFoam.DrawMesh));

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TerrainPatch), nameof(TerrainPatch.Render));
            yield return AccessTools.Method(typeof(TerrainPatch), "RenderSubPatch");
        }

        [HarmonyPriority(Priority.Last)]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            string name = original != null ? original.Name : "?";

            // The instructions as they came, never changed (a swapped call is a new instruction with
            // the old one's labels and blocks): on anything unexpected they go back exactly so.
            var given = new List<CodeInstruction>(instructions);
            try
            {
                var result = new List<CodeInstruction>(given.Count);
                int swapped = 0;
                foreach (CodeInstruction instruction in given)
                {
                    if (GameDraw != null && OurDraw != null && (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) &&
                        instruction.operand is MethodInfo called && called == GameDraw)
                    {
                        result.Add(new CodeInstruction(instruction) { opcode = OpCodes.Call, operand = OurDraw });
                        swapped++;
                        continue;
                    }

                    result.Add(instruction);
                }

                WaterFoam.NoteHooks(name, swapped);
                Log.Msg("water: the foam's hook in TerrainPatch." + name + ": " + swapped +
                        " of the game's draws routed (the game's own call first, unchanged)" + (swapped == 0 ? " -- NONE FOUND: no foam there" : ""));
                Patcher.Transpiled(original, swapped);   // the game check: 0 at start-up = a problem (last: the code is ours now)
                return result;
            }
            catch (Exception e)
            {
                Patcher.Transpiled(original, 0);   // the code goes back as it came: nothing changed
                try
                {
                    WaterFoam.NoteHooks(name, 0);
                    Log.Error("water: the foam's hook in TerrainPatch." + name + " could not be made; the game's code is left as it was (no foam there)", e);
                }
                catch
                {
                    // the log itself failed: the code still goes back unchanged
                }
                return given;
            }
        }
    }
}
