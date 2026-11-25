using System;
using System.Collections.Generic;
using System.Reflection;
using ColossalFramework.DataBinding;
using ColossalFramework.Packaging;
using ColossalFramework.UI;
using ModsLibCS1;
using UnityEngine;

namespace RealGlass
{
    /// <summary>
    /// The game-update safeguard (the mods' folder's GAME-UPDATE-SAFEGUARD-PLAN.md; the author's
    /// call, 2026-11-24: the mod checks what it needs of the game and turns itself off rather than
    /// run half-patched), on ModsLibCS1's GameCheck: at Mod.OnEnabled, before anything of the mod
    /// starts, every game member it patches or reads by name is looked for; Patcher applies each
    /// patch class through the same check, and a transpiler that finds nothing to change is a
    /// problem too. All or nothing: on any problem every patch of ours is undone and the mod is off.
    /// </summary>
    /// <remarks>
    /// OFF IS NOTHING here (no keeper, which only a mod whose save data must outlive it needs): no
    /// patch, no controller (so no glass, water, panel, button, keys or picker), the options page
    /// one line, the API not ready, and no
    /// file written -- RealGlass.xml, the profiles, RealGlassAssets.xml, the room-fit cache and the
    /// creators' definitions are read as usual or not at all, never saved (SettingsXml.ReadOnly).
    /// The city's own keys (painted shores, the dirt's level) are neither read nor written: the game
    /// carries a key no extension writes (ShorePaintSaveData.OnLoadData has the IL), so a city
    /// saved while the mod is off keeps them for the fixed version.
    /// </remarks>
    public static class GameUpdate
    {
        private static GameCheck _check;
        private static readonly List<MethodBase> _targets = new List<MethodBase>();

        /// <summary>The mod has switched itself off (the game changed under it): nothing of it runs.</summary>
        public static bool Off { get; private set; }

        /// <summary>The check, for Patcher (null before <see cref="Check"/>).</summary>
        public static GameCheck Current
        {
            get { return _check; }
        }

        /// <summary>
        /// The Harmony targets the last <see cref="Require"/> found, in its order: the offline test
        /// (tools\test-gamecheck.ps1) checks every [HarmonyPatch] class's target is among them.
        /// </summary>
        public static IList<MethodBase> Targets
        {
            get { return _targets.AsReadOnly(); }
        }

        /// <summary>
        /// Mod.OnEnabled, first: the members the mod reads by name and every Harmony target, looked
        /// for now, whatever Harmony does later. A throw (a type gone: the JIT of
        /// <see cref="Require"/> fails) is a failure too.
        /// </summary>
        public static void Check()
        {
            if (_check != null)
                return;

            _check = new GameCheck(Mod.DisplayName, line => Log.Error(line));
            try
            {
                Require(_check);
            }
            catch (Exception e)
            {
                _check.Problem("the check itself threw: " + e.GetType().Name + ": " + e.Message);
            }

            if (_check.Failed)
                SwitchOff();
            else
                Log.Msg("game check: the game (" + GameCheck.GameVersion + ") has everything Real Glass & Water patches and reads by name (" +
                        _targets.Count + " patch targets)");
        }

        /// <summary>
        /// What Real Glass needs of the game by name. The plan's rule: each Harmony target (its
        /// parameter types, static or not, and the parameter names its patch methods bind by) and
        /// every game member read by name. Not counted, each with its own fallback: other mods'
        /// members -- Procedural Objects' (Glass/ProceduralAccess), Intersection Marking Tool's
        /// (Props.PropImtPatch, patched outside the check), Volumetric Weather's APIs and objects
        /// (Glass/GlassSky, Water/WaterWind), Unified UI's panel (UUIIntegration). Shader and
        /// material property names are not members: a renamed one changes a look, it breaks nothing.
        /// Public for the offline test, which runs it against the game's DLLs.
        /// </summary>
        public static void Require(GameCheck check)
        {
            _targets.Clear();

            const BindingFlags instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            // An override the game might drop: without DeclaredOnly the base's would pass, and Harmony patch that.
            const BindingFlags declared = instance | BindingFlags.DeclaredOnly;
            Type camera = typeof(RenderManager.CameraInfo);
            Type[] cameraOnly = { camera };

            // ---- Glass/GlassPatches.cs: the buildings' three tiers and the asset editor's building.
            // RenderMeshPatch's postfix binds cameraInfo, info, instance.
            Target(check, typeof(BuildingAI), "RenderMesh", statics,
                   new[] { camera, typeof(ushort), typeof(Building).MakeByRefType(), typeof(BuildingInfo), typeof(RenderManager.Instance).MakeByRefType() },
                   "cameraInfo", "info", "instance");
            // RenderSubMeshPatch: cameraInfo, info, subInfo, matrix, instance.
            Target(check, typeof(BuildingAI), "RenderMesh", statics,
                   new[] { camera, typeof(Building).MakeByRefType(), typeof(BuildingInfo), typeof(BuildingInfoBase), typeof(Matrix4x4),
                           typeof(RenderManager.Instance).MakeByRefType(), typeof(bool) },
                   "cameraInfo", "info", "subInfo", "matrix", "instance");
            // RenderLodPatch's prefix and postfix: info.
            Target(check, typeof(Building), "RenderLod", statics, new[] { camera, typeof(BuildingInfoBase) }, "info");
            // GroupRenderMeshPatch: __instance (an instance method), layer.
            Target(check, typeof(RenderGroup), "RenderMesh", instance, new[] { typeof(RenderGroup.MeshLayer) }, "layer");
            // EditorRenderMeshPatch: cameraInfo, info.
            Target(check, typeof(BuildingDecoration), "RenderBuildingMesh", statics, new[] { camera, typeof(BuildingInfo) }, "cameraInfo", "info");
            // MegaGroupRenderMeshPatch: __instance, layer.
            Target(check, typeof(MegaRenderGroup), "RenderMesh", instance, new[] { typeof(MegaRenderGroup.MeshLayer) }, "layer");

            // ---- PerAsset/AssetPicker.cs PickerEscapePatch and Water/ShorePaintTool.cs ShorePaintEscapePatch:
            // GameKeyShortcuts.Escape, found by name (no parameters, none bound).
            Target(check, typeof(GameKeyShortcuts), "Escape", instance, Type.EmptyTypes);

            // ---- PerAsset/CreatorPatches.cs: the asset editor's load, save and share.
            // LoadAssetPatch: OnLoad by name, ___m_SaveList; it calls GetListingData(int) by name.
            Target(check, typeof(LoadAssetPanel), "OnLoad", instance, Type.EmptyTypes);
            check.RequireField(typeof(LoadAssetPanel), "m_SaveList", instance, typeof(UIListBox));
            MethodInfo listing = check.RequireMethod(typeof(LoadAssetPanel).BaseType, "GetListingData", instance, new[] { typeof(int) });
            if (listing != null && listing.ReturnType != typeof(Package.Asset))
                check.Problem(typeof(LoadAssetPanel).BaseType.FullName + ".GetListingData(Int32) returns a " + listing.ReturnType.FullName +
                              " now, not a Package.Asset");
            // SaveAssetPatch: SaveRoutine by name; its postfix binds fileName, mapName.
            Target(check, typeof(SaveAssetPanel), "SaveRoutine", instance, new[] { typeof(string), typeof(string), typeof(string) },
                   "fileName", "mapName");
            // ShareAssetPatch: PrepareStagingArea by name, ___m_TargetAsset, ___m_ContentPath.
            Target(check, typeof(WorkshopAssetUploadPanel), "PrepareStagingArea", instance, new[] { typeof(Texture) });
            check.RequireField(typeof(WorkshopAssetUploadPanel), "m_TargetAsset", instance, typeof(Package.Asset));
            check.RequireField(typeof(WorkshopAssetUploadPanel), "m_ContentPath", instance, typeof(string));

            // ---- PerAsset/PropOutline.cs PropOutlineOverlayPatch: cameraInfo.
            Target(check, typeof(ToolManager), "EndOverlayImpl", declared, cameraOnly, "cameraInfo");

            // ---- Props/PropGlassPatches.cs: the near props' two overloads, then the three flushes (none bound).
            // PropRenderRotatedPatch: cameraInfo, info, id, position, rotation, scale, angle.
            Target(check, typeof(PropInstance), "RenderInstance", statics,
                   new[] { camera, typeof(PropInfo), typeof(InstanceID), typeof(Vector3), typeof(Quaternion), typeof(float), typeof(float),
                           typeof(Color), typeof(Vector4), typeof(bool), typeof(Vector4) },
                   "cameraInfo", "info", "id", "position", "rotation", "scale", "angle");
            // PropRenderMatrixPatch: cameraInfo, info, id, matrix, position, scale, angle.
            Target(check, typeof(PropInstance), "RenderInstance", statics,
                   new[] { camera, typeof(PropInfo), typeof(InstanceID), typeof(Matrix4x4), typeof(Vector3), typeof(float), typeof(float),
                           typeof(Color), typeof(Vector4), typeof(bool) },
                   "cameraInfo", "info", "id", "matrix", "position", "scale", "angle");
            Target(check, typeof(PropManager), "EndRenderingImpl", declared, cameraOnly);
            Target(check, typeof(BuildingManager), "EndRenderingImpl", declared, cameraOnly);
            Target(check, typeof(NetManager), "EndRenderingImpl", declared, cameraOnly);

            // ---- Vehicles/VehicleGlassPatches.cs: two transpilers (their argument indexes ride on the
            // exact parameter types: the moving vehicle's InstanceID is argument 13, the parked one's id
            // argument 2), then the flush.
            Target(check, typeof(Vehicle), "RenderInstance", statics,
                   new[] { camera, typeof(VehicleInfo), typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(Vector4), typeof(Vector4),
                           typeof(Vector3), typeof(float), typeof(Color), typeof(Vehicle.Flags), typeof(Vehicle.Flags2), typeof(int),
                           typeof(InstanceID), typeof(bool), typeof(bool) });
            Target(check, typeof(VehicleParked), "RenderInstance", instance, new[] { camera, typeof(ushort) });
            Target(check, typeof(VehicleManager), "EndRenderingImpl", declared, cameraOnly);

            // ---- Water/WaterFoamDrawPatch.cs: the foam's transpiler on both water draws (Render found
            // by name alone there; RenderSubPatch by name, non-public).
            Target(check, typeof(TerrainPatch), "Render", instance, cameraOnly);
            Target(check, typeof(TerrainPatch), "RenderSubPatch", instance,
                   new[] { typeof(int), typeof(int), typeof(int), camera, typeof(Vector3), typeof(float), typeof(int), typeof(int), typeof(int), typeof(int) });

            // ---- Water/WaterLamps.cs WaterLampPatch: the game's dynamic lights for the water; its postfix
            // binds type, pos, dir, color, intensity, range, spotAngle, spotLeaking.
            Target(check, typeof(LightSystem), "DrawLight", instance,
                   new[] { typeof(LightType), typeof(Vector3), typeof(Vector3), typeof(Vector3), typeof(Color), typeof(float), typeof(float), typeof(float),
                           typeof(float), typeof(bool) },
                   "type", "pos", "dir", "color", "intensity", "range", "spotAngle", "spotLeaking");

            // ---- The draw calls the three transpilers look for (VehicleDrawHook.GameDraw, WaterFoamDrawPatch.GameDraw):
            // the engine's, found by their parameter types. Missing, a transpiler would find nothing.
            check.RequireMethod(typeof(Graphics), "DrawMesh", BindingFlags.Static | BindingFlags.Public,
                                new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock) });
            check.RequireMethod(typeof(Graphics), "DrawMesh", BindingFlags.Static | BindingFlags.Public,
                                new[] { typeof(Mesh), typeof(Matrix4x4), typeof(Material), typeof(int), typeof(Camera), typeof(int), typeof(MaterialPropertyBlock),
                                        typeof(bool), typeof(bool) });

            // ---- By name, as the code reads them: MessageWindow's and GameCheck's own pop-up text
            // (ExceptionPanel's binding, set one key at a time past SetMessage's '&' split).
            check.RequireField(typeof(ExceptionPanel), "m_Binding", BindingFlags.Instance | BindingFlags.NonPublic, typeof(BindPropertyByKey));

            check.ForcedFailure();
        }

        /// <summary>
        /// One Harmony target: there with these flags and parameter types, and each name its patch
        /// methods bind by among its parameters (Harmony binds by NAME, wherever the parameter is:
        /// GameCheck's own name test is positional). Kept in <see cref="Targets"/>.
        /// </summary>
        private static void Target(GameCheck check, Type type, string name, BindingFlags flags, Type[] parameters, params string[] bound)
        {
            MethodInfo method = check.RequireMethod(type, name, flags, parameters);
            if (method == null)
                return;

            _targets.Add(method);
            ParameterInfo[] given = method.GetParameters();
            for (int i = 0; i < bound.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < given.Length && !found; j++)
                    found = given[j].Name == bound[i];
                if (!found)
                    check.Problem(type.FullName + "." + name + ": no parameter '" + bound[i] + "' any more (our patch binds it by name)");
            }
        }

        /// <summary>
        /// A patch class that would not apply, or a transpiler that found nothing (Patcher, when
        /// Harmony is ready): the check has failed late, the patches are undone, and the mod switches
        /// off as at start-up -- the controller taken down if a city is up, the saver stopped.
        /// </summary>
        public static void PatchFailed()
        {
            if (Off)
                return;

            SwitchOff();
            try
            {
                Loader.Shutdown();
                SettingsXml.Shutdown();
            }
            catch (Exception e)
            {
                Log.Error("game check: stopping what had started threw", e);
            }
            Announce();
        }

        private static void SwitchOff()
        {
            Off = true;
            _check.Summary(Mod.Version);
            Log.Error("Real Glass & Water is off until it is updated: none of it runs, and it writes none of its files " +
                      "(settings, profiles, assets) and none of a city's saved data (kept as they are in the save)");

            // Nothing written over the player's files: they wait for the fixed version.
            SettingsXml.ReadOnly = true;
        }

        /// <summary>
        /// The pop-up, once a launch (shown at the main menu). Mod.OnEnabled calls it after
        /// Settings.Init, so the text is in the player's chosen language.
        /// </summary>
        public static void Announce()
        {
            if (!Off || _check == null)
                return;

            string title = Localization.Get("GameUpdate.Title", Mod.DisplayName);
            string text = Localization.Get("GameUpdate.Text");
            _check.ShowOnce(title, text);
        }
    }
}
