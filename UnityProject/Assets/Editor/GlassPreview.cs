using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using RealGlass.Glass;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Debug = UnityEngine.Debug;

/// <summary>
/// Renders a block of towers with the REAL glass shader on this machine's GPU, inside the editor
/// that builds the bundles, through the same Graphics.DrawMesh + MaterialPropertyBlock path the
/// mod uses, with the mod's own GlassUniforms, GlassScreen (the copy after the deferred final
/// pass) and GlassSky (the live cube) -- copied into Assets/Sim by tools/preview/sync-sim.ps1 --
/// and reads the pixels back. tools/preview/gpu-render.ps1 drives it:
///   Unity.exe -batchmode -quit -projectPath UnityProject -executeMethod GlassPreview.Render
/// (no -nographics: it needs a Direct3D 11 device).
///
/// The scene is the game's: a LEGACY DEFERRED camera with HDR in linear colour space, fov 45
/// (the game's), a sun that casts shadows, a trilight ambient, the procedural skybox standing in
/// for the game's scattering sky, towers painted the way Cities: Skylines assets are (a diffuse,
/// an _XYSMap whose z is 1 - specular, an _ACIMap whose b marks the windows lit at night), drawn by
/// a stand-in of the game's building shader (Assets/Editor/PreviewBuilding.shader), and ground
/// with roads.
///
/// Parameters come in as environment variables (executeMethod takes none):
///   RG_OUT        directory for the frames (rg-[row]-[col].png)
///   RG_W, RG_H    frame size
///   RG_VIEWS      "aerial,steep,street,low,close,night,nightclose,nightstreet"  one row each
///   RG_VARIANTS   "vanilla|glass|glass:Trace=0;Neighbours=0"  one column each; "glass:" takes
///                 GlassParams field overrides (bool 0/1, numbers in the settings' units), and
///                 the preview's own: Debug=n (the shader's check views), Level=n (view 9's
///                 level), Tier=near|lod|group, Budget=0..3 (the GlassQuality tier), HiZ=0 (the
///                 tier without the depth pyramid), Q.Field=n (one GlassQuality field)
///   RG_SUN        the sun's intensity by day (1.3)
///   RG_EXPOSURE   the picture's exposure (0.6 by day; night x6)
///   RG_STOREY     the storey the stand-ins are painted with (3.8 m)
///   RG_SHAPES     1: four more towers east of the city (views "shapes", "nightshapes"): round and
///                 shaded smooth, a box with smoothed corners, a glass pyramid, 12-sided and flat
///   RG_FRAMED     1: five towers west of the city (views "framed", "framedmid", "framedfar"), glass
///                 with frames as their own geometry FramedGaps in front ("Ofs=f,u", "Mark=k")
/// Every frame logs "RG FRAME row=.. col=.. view=.. variant=.. file=.. | ..." with its mean colour.
/// </summary>
public static class GlassPreview
{
    private const string GlassShaderPath = "Assets/RealGlass/Glass.shader";
    private const string UtilShaderPath = "Assets/RealGlass/GlassUtil.shader";
    private const string StandInPath = "Assets/Editor/PreviewBuilding.shader";
    private const string CloudsPath = "Assets/Editor/PreviewClouds.shader";
    private const string VehicleGlassPath = "Assets/RealGlass/VehicleGlass.shader";
    private const string VehicleStandInPath = "Assets/Editor/PreviewVehicle.shader";
    private const string PropGlassPath = "Assets/RealGlass/PropGlass.shader";
    private const string PropStandInPath = "Assets/Editor/PreviewRotors.shader";

    /// <summary>
    /// The props' glass and water (1.1.9), the variant's keys: Prop=0 (none of ours on them), Prop.Refl=x
    /// and Prop.Clarity=x (0..1; the surface's default otherwise), Prop.Shine=x (shininess needed, 0 all
    /// of it), Prop.Surface=1|2 (every prop glass, or water, whatever RG_PROPS says).
    /// </summary>
    private static bool _propOff;
    private static float _propRefl = -1f, _propClarity = -1f, _propShine;
    private static int _propSurface;

    /// <summary>
    /// Decals (1.1.9), the variant's key Decal=1|2: our glass or water over the plaza's decals --
    /// boxes projected as the game projects its decals (PropGlass's RG_PROP_DECAL), a soft round alpha
    /// each: one square, one long and turned 30 degrees, one at 1.5x scale. Decal=0 (default) none.
    /// </summary>
    private static int _decalSurface;

    /// <summary>
    /// Round 3's prop rows (keys prop.polish / glint / soft / strength (water's) / ripple / ripplesize
    /// / ripplespeed, 1 = 100%; prop.color=#RRGGBB): -1 / unset = PropRecord.Resolve's default.
    /// Prop.surface=3 (Decal=3) a reflective finish, Aluminium by default.
    /// </summary>
    private static float _propPolish = -1f, _propGlint = -1f, _propSoft = -1f, _propWaterStrength = -1f;
    private static Vector4 _propRipple = Vector4.one;
    private static Color _propColour;
    private static bool _propHasColour;

    /// <summary>
    /// The block's look as PropGlassDraw writes it from PropRecord.Resolve's defaults: Clear low-E
    /// glass (#D9E4E6, 11%, 64%, clear), water (2% x Strength, 70%, the Water tab's #0E222B),
    /// Aluminium (F0 0.913, 0.922, 0.924; polish 85%) -- each overridable by the prop.* keys.
    /// </summary>
    private static void SetPropLook(MaterialPropertyBlock block, int surface, float shine)
    {
        bool water = surface == 2, finish = surface == 3;
        Color colour = _propHasColour ? _propColour.linear
            : (water ? ((Color)new Color32(0x0E, 0x22, 0x2B, 255)).linear
            : (finish ? new Color(0.913f, 0.922f, 0.924f) : ((Color)new Color32(0xD9, 0xE4, 0xE6, 255)).linear));
        float refl = _propRefl >= 0f ? _propRefl : (water ? 0.02f : (finish ? 0.924f : 0.11f));
        float clarity = _propClarity >= 0f ? _propClarity : (water ? 0.7f : (finish ? 0f : 0.64f));
        float polish = _propPolish >= 0f ? _propPolish : (finish ? 0.85f : 1f);
        float rough = water ? 0.05f : Mathf.Lerp(0.5f, 0.02f, Mathf.Clamp01(polish));
        Shader.SetGlobalVector("_RGPropWaterTab", new Vector4(colour.r, colour.g, colour.b, 1f));
        block.SetFloat("_RGPropMode", surface);
        block.SetVector("_RGPropOptics", new Vector4(refl, clarity, 1f, finish || !water ? shine : 0f));
        block.SetVector("_RGPropTint", new Vector4(colour.r, colour.g, colour.b, 1f));
        block.SetVector("_RGPropLook", new Vector4(rough, _propGlint >= 0f ? _propGlint : 1f, _propSoft >= 0f ? _propSoft : 0f,
            water && _propWaterStrength >= 0f ? _propWaterStrength : 1f));
        block.SetVector("_RGPropRipples", _propRipple);
    }

    private sealed class DecalStandIn
    {
        public Mesh Box;
        public Matrix4x4 Matrix;
        public Vector3 Size;
    }

    private sealed class Prop
    {
        public PreviewVehicleAsset Asset;
        public Matrix4x4 Matrix;
        /// <summary>1 glass, 2 water (PropRecord's values).</summary>
        public int Surface;
    }

    /// <summary>
    /// The vehicles' glass, the variant's "Veh.*" keys: Veh.Tinted=0..1 (VehicleGlassLook: the share of
    /// vehicles with tinted windows), Veh.Strength / Veh.Refl (the Reflections slider: 1 = 3x physics) / Veh.Glint /
    /// Veh.Edge / Veh.Rough (numbers), Veh.Lenses=0 (no lens on the lamps), Veh=0 (no vehicle glass),
    /// Inst=0 (one DrawMesh a vehicle instead of DrawMeshInstanced); Bld=0: no glass on the buildings.
    /// </summary>
    /// <summary>The view is by night (the vehicles' lights).</summary>
    private static bool _night;

    private static float _vehTinted = VehicleGlassLook.DefaultTintedShare;
    private static float _vehStrength = 1f, _vehRefl = 1f, _vehGlint = 1f, _vehEdge = 1f, _vehRough = 0.03f;
    private static bool _vehLenses = true, _vehOff, _vehSingle, _bldOff;
    // The paint's clear coat: Veh.Coat=0..1, Veh.CoatRough=x, Veh.Liveries=1 (also an unmasked
    // body light enough to be a livery: it coats the wheels too), Veh.Cabin=x (the cabin's light behind
    // a window, a share of the sky's).
    private static float _vehCoat, _vehCoatRough = VehicleGlassLook.CoatRoughness, _vehCabin = VehicleGlassLook.Cabin;
    private static bool _vehLiveries;

    private sealed class Vehicle
    {
        public PreviewVehicleAsset Asset;
        public Matrix4x4 Matrix;
        public Color Paint;

        /// <summary>Its own random number (VehicleGlassLook.Seed of its id): whether its windows are tinted, and how dark.</summary>
        public float Seed;

        /// <summary>The game's _LightState: front, rear, left, right (by day the brake lights of a few, an indicator).</summary>
        public Vector4 DayLights, NightLights;
    }

    /// <summary>The variant's "Debug=n": the shader's check view (0 = the picture); "Level=n": the level view 9 shows.</summary>
    private static float _debug, _debugLevel;

    /// <summary>The variant's "Overcast=x": the clouds' stand-in from its broken layer (0) to a closed deck (1).</summary>
    private static float _overcast;

    /// <summary>The variant's "Lit=x": the share of the stand-ins' windows the game lights at night (0.25; 0 = every building's lights off).</summary>
    private static float _litShare = 0.25f;

    /// <summary>
    /// RG_GAMELIGHTS=1 (the whole run, vanilla too): the stand-ins light their windows by the game's own
    /// rule (PreviewBuilding.shader) and paint every kind of illumination value; RG_SIMTIME (seconds,
    /// the game's _SimulationTime2.x) and RG_NIGHTLEVEL (its _GiantMarshmallow; -3 at 21:00) step it.
    /// </summary>
    private static bool GameLights
    {
        get { return Env("RG_GAMELIGHTS", "0") == "1"; }
    }

    /// <summary>The variant's "Guards=0": the rooms drawn as before RoomShape kept them off sloped, curved and smooth-shaded glass.</summary>
    private static bool _guardsOff;

    /// <summary>The game's object colour map (RenderManager.OBJECT_COLORMAP_WIDTH x HEIGHT in the game: 512 x 256).</summary>
    private const int ColorMapWidth = 512, ColorMapHeight = 256;

    private sealed class Building
    {
        public Mesh Mesh;
        public Matrix4x4 Matrix;
        public Texture2D Main, Xys, Aci;
        public string Kind;

        /// <summary>Its colour index, as RenderManager.GetColorLocation(id) makes it: its texel in the colour map.</summary>
        public Vector4 ObjectIndex;

        /// <summary>The middle tier's test: 8 copies of this building's LOD mesh (uv in the atlas), as BuildingInfoBase.InitMeshData makes them.</summary>
        public Mesh Combined8;

        /// <summary>"Lod=turned": this building's LOD is laid out otherwise (its texture and its uv turned
        /// about the diagonal) -- it looks the same, but its model's maps through its uv are elsewhere.</summary>
        public bool Turned;
        public Mesh Combined8Turned;

        /// <summary>Its rectangle in the LOD atlases (uv).</summary>
        public Vector4 AtlasRect;

        /// <summary>"-Framed": its number (1..5, FramedGaps' order; 0 for the rest) and its colour with the frames red ("Mark=k").</summary>
        public int Framed;
        public Texture2D MainMarked;
    }

    private sealed class Scene
    {
        public Camera Camera;
        public RenderTexture Target;
        public Texture2D Readback;
        public int Width, Height;
        public Light Sun;
        public Light Moon;
        public Material Sky;
        public Material StandIn;
        public Material Glass;
        public Material Util;
        public MaterialPropertyBlock Block;
        public List<Building> Buildings = new List<Building>();
        public Mesh Ground;
        public Texture2D GroundMain, GroundXys, GroundAci;
        public GlassScreen Screen = new GlassScreen();
        public GlassPrePass PrePass;
        public GlassSky SkyCube = new GlassSky();

        // The middle and far tiers' tests: the LOD atlases, the materials, and the whole city
        // baked into one world-space mesh (what RenderGroup draws).
        public Texture2D AtlasMain, AtlasXys, AtlasAci;
        public Material GlassLod, GlassGroup;
        public Mesh Baked;

        // "Lod=dull": the LOD textures without a shine map (the game's dull default, as an asset whose
        // LOD has no _lod_s gets); "Lod=turned": that, and every third building's LOD laid out
        // otherwise; "Lod=wrong": the LOD's shine moved half a slot along (glass where its model has wall).
        public Texture2D AtlasXysDull, AtlasMainTurned, AtlasXysTurnedDull, AtlasAciTurned, AtlasXysWrong;
        public Mesh BakedTurned;

        /// <summary>The model's glass on its LOD (the mod's LodGlass, same file).</summary>
        public LodGlass LodGlass = new LodGlass();

        /// <summary>The stand-in for Volumetric Weather's cloud volume (PreviewClouds.shader), found by its name.</summary>
        public GameObject Clouds;

        /// <summary>The city from above (the mod's CityMap, same file): every tower as its box.</summary>
        public CityMap City = new CityMap();

        /// <summary>Each building's own glass (the mod's AssetTextures, same file): what "A.*" sets.</summary>
        public AssetTextures Assets = new AssetTextures();

        /// <summary>The id of the middle block's first full-glass tower: the building "A.*" gives its own glass.</summary>
        public int FullGlassId;

        /// <summary>The rooms behind the glass (the mod's RoomAtlas, same file, the mod's own pictures).</summary>
        public RoomAtlas Rooms = new RoomAtlas();

        /// <summary>The rooms fitted to each stand-in's windows (the mod's RoomFit and RoomFitTextures, same files).</summary>
        public RoomFitTextures RoomFits = new RoomFitTextures();

        /// <summary>Real vehicle assets (RG_VEHICLES: Workshop .crp files) along the street at StreetZ.</summary>
        public List<Vehicle> Vehicles = new List<Vehicle>();
        public List<PreviewVehicleAsset> VehicleAssets = new List<PreviewVehicleAsset>();
        public Material VehicleStandIn, VehicleGlass;
        public MaterialPropertyBlock VehicleBlock;
        public int VehicleDraws;

        /// <summary>Real see-through props (RG_PROPS: "surface:path" entries, 1 glass 2 water) beside the street.</summary>
        public List<Prop> Props = new List<Prop>();
        public Material PropStandIn, PropGlass;
        public MaterialPropertyBlock PropBlock;
        public Texture2D PropRipple;
        public int PropDraws;

        /// <summary>The plaza's decals (Decal=1|2) and their material: PropGlass with RG_PROP_DECAL, as PropGlassDraw's.</summary>
        public List<DecalStandIn> Decals = new List<DecalStandIn>();
        public Material DecalGlass;
        public Texture2D DecalAlpha;
    }

    /// <summary>The variant's "Fit=0": the rooms on the storey grid, as before they were fitted to the windows.</summary>
    private static bool _fitOff;

    /// <summary>The variant's "Hour=h": the time of day the rooms' lights follow (21:00 by night, 13:00 by day).</summary>
    private static float? _hour;

    /// <summary>The kind of rooms a stand-in has (Interiors.Kind*): low punched-window blocks homes and shops, towers offices and flats.</summary>
    private static int RoomKindOf(Building b, int id)
    {
        switch (b.Kind)
        {
            case "punched": return id % 3 == 0 ? Interiors.KindShopLow : Interiors.KindHome;
            case "fins": return Interiors.KindHome;
            case "glassbox": return id % 2 == 0 ? Interiors.KindHotel : Interiors.KindOffice;
            default: return Interiors.KindOffice;
        }
    }

    /// <summary>The variant's "A.Field=value": the full-glass tower's own settings (its asset's, in the mod).</summary>
    private sealed class AssetOverride
    {
        public float? Strength, Detail, Reflectivity, Clarity, Own, Threshold;
        public bool? Night, Curtains, IllumMap;
        public Color? Tint;

        public bool HasGlass
        {
            get { return Reflectivity.HasValue || Clarity.HasValue || Own.HasValue || Tint.HasValue || Threshold.HasValue || Night.HasValue; }
        }
    }

    private static AssetOverride _asset;

    /// <summary>The variant's "Tier=near|lod|group": which of the game's three paths the glass is drawn through.</summary>
    private static string _tier = "near";

    /// <summary>The variant's "Lod=own|dull|turned|wrong": the LOD atlases (above); "LodMode=auto|model|own": every asset's say.</summary>
    private static string _lod = "own";
    private static int _lodMode = LodGlass.ModeAuto;

    /// <summary>The variant's "Budget=0..3": the GlassQuality tier (the mod runs Ultra).</summary>
    private static int _quality = GlassQuality.Ultra;

    /// <summary>The variant's "Ofs=f,u": the glass pass's depth Offset factor and units (Glass.shader's 0, -1; -1, -1 before 1.1.6).</summary>
    private static float _offsetFactor = 0f, _offsetUnits = -1f;

    /// <summary>The variant's "Mark=k": the k-th framed tower's frames drawn red (0: none).</summary>
    private static int _mark;

    /// <summary>The variant's "HiZ=0": the tier's budget without the hierarchical depth (the plain trace).</summary>
    private static bool _hizOff;

    /// <summary>The variant's "Q.Field=n": the tier's budget with that GlassQuality field changed (Q.PaneSearchSteps=0...).</summary>
    private static readonly List<KeyValuePair<string, int>> _budgetOverrides = new List<KeyValuePair<string, int>>();


    public static void Render()
    {
        try
        {
            RenderAll();
            Debug.Log("RG PREVIEW OK");
        }
        catch (Exception e)
        {
            Debug.LogError("RG PREVIEW FAILED: " + e);
            EditorApplication.Exit(1);
        }
    }

    private static void RenderAll()
    {
        if (PlayerSettings.colorSpace != ColorSpace.Linear)
            PlayerSettings.colorSpace = ColorSpace.Linear;

        string outDir = Env("RG_OUT", "GlassPreview");
        Directory.CreateDirectory(outDir);
        int width = int.Parse(Env("RG_W", "480"), CultureInfo.InvariantCulture);
        int height = int.Parse(Env("RG_H", "270"), CultureInfo.InvariantCulture);
        string[] views = Env("RG_VIEWS", "aerial").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        string[] variants = Env("RG_VARIANTS", "vanilla|glass").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
        float sunIntensity = ParseFloat(Env("RG_SUN", "1.3"));
        float exposure = ParseFloat(Env("RG_EXPOSURE", "0.6"));

        Shader glass = Load(GlassShaderPath);
        Shader util = Load(UtilShaderPath);
        Shader standIn = Load(StandInPath);
        Debug.Log("RG PREVIEW: glass supported=" + glass.isSupported + " util supported=" + util.isSupported +
                  " stand-in passes=" + new Material(standIn).passCount + " | device " + SystemInfo.graphicsDeviceType + " '" +
                  SystemInfo.graphicsDeviceName + "' | colour space " + QualitySettings.activeColorSpace);
        if (!glass.isSupported)
            throw new Exception("the glass shader is not supported here: it did not compile (see the log's Shader error lines)");

        Scene s = BuildScene(width, height, glass, util, standIn);
        Debug.Log("RG PREVIEW: " + s.Buildings.Count + " buildings");

        // The rooms' pictures, as the mod ships them (tools/interiors/build-rooms.ps1).
        string resources = Path.Combine(Path.GetDirectoryName(Application.dataPath), "../RealGlass/Resources");
        string nightFile = Path.Combine(resources, "rooms-night.bytes"), dayFile = Path.Combine(resources, "rooms-day.bytes");
        if (File.Exists(nightFile) && File.Exists(dayFile) && s.Rooms.Load(File.ReadAllBytes(nightFile), File.ReadAllBytes(dayFile)))
            Debug.Log("RG PREVIEW: " + s.Rooms.Tiles + " rooms");
        else
            Debug.Log("RG PREVIEW: no rooms (" + (File.Exists(nightFile) ? s.Rooms.Problem : "no " + nightFile) + ")");

        for (int row = 0; row < views.Length; row++)
        {
            string view = views[row].Trim().ToLowerInvariant();
            for (int col = 0; col < variants.Length; col++)
            {
                string variant = variants[col].Trim();
                string info;
                Color[] pixels = RenderView(s, view, variant, sunIntensity, out info);
                float exp = view.StartsWith("night") ? exposure * 6f : exposure;
                if (Seq != null)
                {
                    // The harness: the last resolved frame, then its flicker map beside it.
                    string picture = Path.Combine(outDir, "rg-" + row + "-" + (2 * col) + ".png");
                    string map = Path.Combine(outDir, "rg-" + row + "-" + (2 * col + 1) + ".png");
                    SavePng(pixels, width, height, exp, picture);
                    var png = new Texture2D(width, height, TextureFormat.RGB24, false, false);
                    png.SetPixels(_flickerMap);
                    png.Apply(false);
                    File.WriteAllBytes(map, png.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(png);
                    Debug.Log("RG FLICKER view=" + view + " variant=" + variant + " | " + _flickerStats);
                    Debug.Log("RG FRAME row=" + row + " col=" + (2 * col) + " view=" + view + " variant=" + variant + " file=" + picture + " | " + info);
                    Debug.Log("RG FRAME row=" + row + " col=" + (2 * col + 1) + " view=" + view + " variant=" + _flickerStats + " file=" + map + " | flicker");
                    continue;
                }
                string file = Path.Combine(outDir, "rg-" + row + "-" + col + ".png");
                SavePng(pixels, width, height, exp, file);
                Debug.Log("RG FRAME row=" + row + " col=" + col + " view=" + view + " variant=" + variant + " file=" + file + " | " + info);
            }
        }

        s.Screen.Detach();
        s.Screen.PrePass = null;
        s.PrePass.Destroy();
        s.SkyCube.Destroy();
        s.City.Destroy();
        s.Assets.Destroy();
        s.RoomFits.Destroy();
        s.LodGlass.Destroy();
        s.Camera.targetTexture = null;
        UnityEngine.Object.DestroyImmediate(s.Camera.gameObject);
        UnityEngine.Object.DestroyImmediate(s.Target);
    }

    private static Shader Load(string path)
    {
        Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
        if (shader == null)
            throw new Exception("shader not found at " + path);
        return shader;
    }

    // ---------------------------------------------------------------------------------------
    // The scene.
    // ---------------------------------------------------------------------------------------

    private static Scene BuildScene(int width, int height, Shader glass, Shader util, Shader standIn)
    {
        var s = new Scene { Width = width, Height = height };

        QualitySettings.shadows = ShadowQuality.All;
        QualitySettings.shadowResolution = ShadowResolution.VeryHigh;
        QualitySettings.shadowDistance = 2500f;
        QualitySettings.shadowCascades = 4;
        QualitySettings.pixelLightCount = 4;

        GameObject camObject = new GameObject("RGPreviewCamera");
        s.Camera = camObject.AddComponent<Camera>();
        s.Camera.allowHDR = true;
        s.Camera.allowMSAA = false;
        s.Camera.renderingPath = RenderingPath.DeferredLighting;
        s.Camera.fieldOfView = 45f;
        s.Camera.nearClipPlane = 1f;
        s.Camera.farClipPlane = 12000f;
        s.Camera.clearFlags = CameraClearFlags.Skybox;
        s.Camera.depthTextureMode = DepthTextureMode.Depth;
        s.Target = new RenderTexture(width, height, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        s.Target.antiAliasing = 1;
        s.Target.Create();
        s.Camera.targetTexture = s.Target;
        s.Readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);

        GameObject sunObject = new GameObject("RGPreviewSun");
        s.Sun = sunObject.AddComponent<Light>();
        s.Sun.type = LightType.Directional;
        s.Sun.shadows = LightShadows.Soft;
        s.Sun.shadowStrength = 0.9f;
        RenderSettings.sun = s.Sun;

        GameObject moonObject = new GameObject("RGPreviewMoon");
        s.Moon = moonObject.AddComponent<Light>();
        s.Moon.type = LightType.Directional;
        s.Moon.shadows = LightShadows.None;
        s.Moon.color = new Color(0.55f, 0.65f, 1f);
        s.Moon.intensity = 0f;

        s.Sky = new Material(Shader.Find("Skybox/Procedural"));
        RenderSettings.skybox = s.Sky;
        RenderSettings.ambientMode = AmbientMode.Trilight;

        s.StandIn = new Material(standIn);
        s.Glass = new Material(glass);
        s.Glass.EnableKeyword("RG_NEAR");
        s.GlassLod = new Material(glass);
        s.GlassLod.EnableKeyword("RG_LOD");
        s.GlassGroup = new Material(glass);
        s.GlassGroup.EnableKeyword("RG_GROUP");
        s.Util = new Material(util);
        s.Block = new MaterialPropertyBlock();
        // The reflection pass, as the mod runs it (GlassController): GlassScreen attaches it after its copies.
        s.PrePass = new GlassPrePass(Load("Assets/RealGlass/GlassPrePass.shader"), Load("Assets/RealGlass/GlassReflection.shader"));
        s.Screen.PrePass = s.PrePass;

        BuildCity(s);
        BuildLodTiers(s);
        BuildCityMap(s);
        s.Assets.Create(ColorMapWidth, ColorMapHeight);
        BuildRoomFits(s);
        Debug.Log("RG PREVIEW: city heights " + s.City.LiveCount + " buildings, highest roof " + s.City.LiveTop.ToString("F0") + " m");

        // Volumetric Weather's cloud volume, as the mod finds it: a mesh renderer by that name, a
        // box round the camera with a Transparent+100 material and _UseDepth.
        s.Clouds = GameObject.CreatePrimitive(PrimitiveType.Cube);
        s.Clouds.name = GlassSky.CloudVolumeName;
        UnityEngine.Object.DestroyImmediate(s.Clouds.GetComponent<Collider>());
        s.Clouds.GetComponent<MeshRenderer>().sharedMaterial = new Material(Load(CloudsPath));
        s.Clouds.transform.localScale = Vector3.one * 9000f;

        BuildVehicles(s);
        BuildProps(s);
        BuildDecals(s);
        return s;
    }

    /// <summary>
    /// The plaza's decals, 10 m south of the props (z = StreetZ - 26), on the flat ground: each a box
    /// mesh of its size (its own space, as the game's decal meshes are, _DecalSize the same), a soft
    /// round alpha (the game's _ACIMap.r = 1 - alpha). Square 6 x 6; long 9 x 3 turned 30 degrees; a
    /// 4 x 4 at 1.5x scale (the projection in the box's own space). 2 m tall, centred on the ground.
    /// </summary>
    private static void BuildDecals(Scene s)
    {
        const int n = 128;
        var alpha = new Texture2D(n, n, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2f - 1f, dy = (y + 0.5f) / n * 2f - 1f;
                float a = Mathf.Clamp01((0.92f - Mathf.Sqrt(dx * dx + dy * dy)) / 0.12f);
                byte r = (byte)Mathf.RoundToInt((1f - a) * 255f);
                px[y * n + x] = new Color32(r, 0, 0, 255);
            }
        }
        alpha.SetPixels32(px);
        alpha.Apply(true);
        s.DecalAlpha = alpha;

        float z = StreetZ - 26f;
        s.Decals.Add(new DecalStandIn { Size = new Vector3(6f, 2f, 6f), Matrix = Matrix4x4.TRS(new Vector3(0f, 0f, z), Quaternion.identity, Vector3.one) });
        s.Decals.Add(new DecalStandIn { Size = new Vector3(9f, 2f, 3f), Matrix = Matrix4x4.TRS(new Vector3(10f, 0f, z), Quaternion.Euler(0f, 30f, 0f), Vector3.one) });
        s.Decals.Add(new DecalStandIn { Size = new Vector3(4f, 2f, 4f), Matrix = Matrix4x4.TRS(new Vector3(20f, 0f, z), Quaternion.identity, Vector3.one * 1.5f) });
        foreach (DecalStandIn d in s.Decals)
            d.Box = BoxMesh(d.Size);

        s.DecalGlass = new Material(Load(PropGlassPath)) { enableInstancing = true, renderQueue = 2499 };
        s.DecalGlass.EnableKeyword("RG_PROP_DECAL");
        s.DecalGlass.SetFloat("_RGPropCull", (float)CullMode.Front);
        s.DecalGlass.SetFloat("_RGPropZTest", (float)CompareFunction.Always);
        if (s.PropRipple == null)
            s.PropRipple = RippleStandIn();
    }

    /// <summary>A box of this size centred on its origin, its faces outward (a decal's mesh).</summary>
    private static Mesh BoxMesh(Vector3 size)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Mesh unit = cube.GetComponent<MeshFilter>().sharedMesh;
        Vector3[] v = unit.vertices;
        for (int i = 0; i < v.Length; i++)
            v[i] = Vector3.Scale(v[i], size);
        var m = new Mesh { vertices = v, triangles = unit.triangles, normals = unit.normals, uv = unit.uv };
        m.RecalculateBounds();
        UnityEngine.Object.DestroyImmediate(cube);
        return m;
    }

    /// <summary>
    /// Ours over the plaza's decals (Decal=1 glass, 2 water), drawn as PropGlassDraw draws a decal: its
    /// box instanced with the decals' material (RG_PROP_DECAL, back faces, no depth test), its alpha
    /// map, size, tiling and fade in the block, the prop surface's defaults (Clear low-E glass, water).
    /// </summary>
    private static void DrawDecals(Scene s, bool glass)
    {
        if (!glass || _decalSurface == 0 || s.Decals.Count == 0)
            return;
        s.DecalGlass.SetTexture("_RGPropRipple", s.PropRipple);
        foreach (DecalStandIn d in s.Decals)
        {
            var block = new MaterialPropertyBlock();
            block.SetTexture("_ACIMap", s.DecalAlpha);
            block.SetVector("_RGDecalSize", d.Size);
            block.SetVector("_RGDecalTiling", Vector4.one);
            block.SetVector("_RGDecalFade", new Vector4(1f, 2.6f / (1000f * 1000f), 0f, 0f));
            block.SetVector("_RGPropST", new Vector4(1f, 1f, 0f, 0f));
            SetPropLook(block, _decalSurface, 0f);
            Graphics.DrawMeshInstanced(d.Box, 0, s.DecalGlass, new[] { d.Matrix }, 1, block, ShadowCastingMode.Off, false, 0, s.Camera);
            s.PropDraws++;
        }
    }

    /// <summary>
    /// The props' plaza south of the street (StreetZ - 16): RG_PROPS's props side by side, 4 m apart,
    /// each as the mod sees it -- a see-through prop (the game's Rotors shader, its stand-in here) with
    /// our glass or water over it. A flat one (a water surface) lies 3 cm over the ground.
    /// </summary>
    private static void BuildProps(Scene s)
    {
        string list = Env("RG_PROPS", "");
        float x = -16f;
        foreach (string entry in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = entry.IndexOf(':');
            int surface = colon == 1 && (entry[0] == '1' || entry[0] == '2') ? entry[0] - '0' : 1;
            string path = colon == 1 ? entry.Substring(2) : entry;
            try
            {
                PreviewVehicleAsset a = PreviewVehicleAsset.Load(path.Trim());
                Bounds bb = a.Mesh.bounds;
                var p = new Prop { Asset = a, Surface = surface };
                // Side by side along x, 4 m apart, each centred on the plaza's line.
                float left = x - bb.min.x;
                p.Matrix = Matrix4x4.TRS(new Vector3(left, 0.03f - bb.min.y, StreetZ - 16f - bb.center.z), Quaternion.identity, Vector3.one);
                x += bb.size.x + 4f;
                s.Props.Add(p);
                Debug.Log("RG PREVIEW: prop '" + a.Name + "' " + (surface == 2 ? "water" : "glass") + ", " + a.Mesh.vertexCount + " vertices, " +
                          bb.size.ToString("F1") + " m, mean xys " + MeanOf(a.Xys));
            }
            catch (Exception e)
            {
                Debug.Log("RG PREVIEW: prop not loaded: " + e.Message);
            }
        }
        if (s.Props.Count == 0)
            return;

        s.PropStandIn = new Material(Load(PropStandInPath));
        s.PropGlass = new Material(Load(PropGlassPath)) { enableInstancing = true, renderQueue = 3001 };
        s.PropBlock = new MaterialPropertyBlock();
        s.PropRipple = RippleStandIn();
        Debug.Log("RG PREVIEW: prop glass supported=" + s.PropGlass.shader.isSupported);
    }

    /// <summary>
    /// A ripple texture as the game's water reads it (.w the x slope, .y the z slope, x 2 - 1): smooth
    /// noise, tiling, ~0.3 rms in the decoded units -- the preview has no water of the game's.
    /// </summary>
    private static Texture2D RippleStandIn()
    {
        const int n = 256;
        var t = new Texture2D(n, n, TextureFormat.RGBA32, true, true) { wrapMode = TextureWrapMode.Repeat };
        var pixels = new Color32[n * n];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                float sx = 0f, sy = 0f;
                for (int o = 1; o <= 4; o++)
                {
                    float f = 2f * Mathf.PI * o * 2f / n;
                    sx += Mathf.Sin(f * x + o * 1.7f) * Mathf.Cos(f * y * 0.7f + o) * 0.3f / o;
                    sy += Mathf.Cos(f * y + o * 2.3f) * Mathf.Sin(f * x * 0.8f + o * 0.5f) * 0.3f / o;
                }
                byte bx = (byte)Mathf.Clamp(Mathf.RoundToInt((sx * 0.5f + 0.5f) * 255f), 0, 255);
                byte by = (byte)Mathf.Clamp(Mathf.RoundToInt((sy * 0.5f + 0.5f) * 255f), 0, 255);
                pixels[y * n + x] = new Color32(128, by, 128, bx);
            }
        }
        t.SetPixels32(pixels);
        t.Apply(true);
        return t;
    }

    private static string MeanOf(Texture2D t)
    {
        Color[] px = t.GetPixels(Mathf.Min(4, t.mipmapCount - 1));
        Color m = Color.black;
        foreach (Color c in px)
            m += c;
        return Fmt(m / Mathf.Max(px.Length, 1));
    }

    /// <summary>The vehicles' street: the road along z = -300 (14 m of asphalt), the low blocks of the
    /// city's south edge on its sunny side, the towers of the next blocks north behind it.</summary>
    private const float StreetZ = -300f;

    /// <summary>
    /// The vehicles' street (StreetZ) with real cars from RG_VEHICLES (";"-separated .crp paths): two
    /// lanes of traffic and a row parked at each kerb, each car its own paint. A vehicle faces +z in its
    /// own space (the game's lights tell front from rear by it).
    /// </summary>
    private static void BuildVehicles(Scene s)
    {
        string list = Env("RG_VEHICLES", "");
        foreach (string path in list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                PreviewVehicleAsset a = PreviewVehicleAsset.Load(path.Trim());
                s.VehicleAssets.Add(a);
                Bounds bb = a.Mesh.bounds;
                Debug.Log("RG PREVIEW: vehicle '" + a.Name + "' " + a.Mesh.vertexCount + " vertices, " + bb.size.ToString("F1") +
                          " m (min " + bb.min.ToString("F2") + "), floor " + a.Floor.ToString("F2") + ", textures " + a.Main.width + "x" + a.Main.height +
                          " " + a.Xys.format + ", mean main " + MeanOf(a.Main) + " xys " + MeanOf(a.Xys) + " aci " + MeanOf(a.Aci));
            }
            catch (Exception e)
            {
                Debug.Log("RG PREVIEW: vehicle not loaded: " + e.Message);
            }
        }
        if (s.VehicleAssets.Count == 0)
            return;

        s.VehicleStandIn = new Material(Load(VehicleStandInPath));
        s.VehicleGlass = new Material(Load(VehicleGlassPath));
        s.VehicleGlass.enableInstancing = true;
        s.VehicleBlock = new MaterialPropertyBlock();
        Debug.Log("RG PREVIEW: vehicle glass supported=" + s.VehicleGlass.shader.isSupported + " instancing supported=" + SystemInfo.supportsInstancing);

        Color[] paints =
        {
            new Color(0.85f, 0.85f, 0.83f), new Color(0.05f, 0.05f, 0.06f), new Color(0.55f, 0.56f, 0.58f), new Color(0.30f, 0.31f, 0.33f),
            new Color(0.55f, 0.06f, 0.05f), new Color(0.08f, 0.16f, 0.38f), new Color(0.75f, 0.74f, 0.70f), new Color(0.14f, 0.22f, 0.16f),
        };
        // (x, z from the street's centre line, heading in degrees: 90 = towards +x). The hero car of the
        // "car" views sits at x = 0 in the westbound lane.
        float[,] spots =
        {
            { 0f, 1.8f, -90f }, { -14f, 1.8f, -90f }, { 13f, 1.8f, -90f }, { -31f, 1.8f, -90f },
            { -7f, -1.8f, 90f }, { 7f, -1.8f, 90f }, { -24f, -1.8f, 90f }, { 22f, -1.8f, 90f },
            { -20f, -5.6f, 90f }, { -12.5f, -5.6f, 90f }, { 4f, -5.6f, 90f }, { 11.5f, -5.6f, 90f },
            { -18f, 5.6f, -90f }, { -3f, 5.6f, -90f }, { 18f, 5.6f, -90f }, { 30f, 5.6f, -90f },
        };
        for (int k = 0; k < spots.GetLength(0); k++)
        {
            // In traffic (the first eight): every third braking, the hero car (0) signalling left; parked
            // cars dark. At night the moving ones with their headlights and tail lights on.
            bool moving = k < 8;
            // Its id as the game's (InstanceID.RawData): a moving vehicle's 0x02000000 + its index, a parked one's 0x07000000 + its own.
            uint id = (moving ? 0x02000000u : 0x07000000u) | (uint)(k + 1);
            PreviewVehicleAsset a = s.VehicleAssets[k % s.VehicleAssets.Count];
            var v = new Vehicle { Asset = a, Paint = paints[(k * 3) % paints.Length], Seed = VehicleGlassLook.Seed(id) };
            v.Matrix = Matrix4x4.TRS(new Vector3(spots[k, 0], 0f, StreetZ + spots[k, 1]), Quaternion.Euler(0f, spots[k, 2], 0f), Vector3.one);
            float brake = moving && k % 3 == 0 ? 1f : 0f;
            v.DayLights = new Vector4(0f, brake, k == 0 ? 1f : 0f, 0f);
            v.NightLights = moving ? new Vector4(1f, Mathf.Max(0.5f, brake), k == 0 ? 1f : 0f, 0f) : Vector4.zero;
            s.Vehicles.Add(v);
        }
    }

    /// <summary>
    /// The mod's city height map (CityMap, the same file) for the preview's city: every building as
    /// its mesh's bounding box, at its matrix, under its id. The game gives each its prefab's height
    /// grid; a box is a box.
    /// </summary>
    private static void BuildCityMap(Scene s)
    {
        s.City.Begin(0f, 0f);
        for (int i = 0; i < s.Buildings.Count; i++)
        {
            Building b = s.Buildings[i];
            Vector3 position = b.Matrix.GetColumn(3);
            Quaternion rotation = Quaternion.LookRotation(b.Matrix.GetColumn(2), b.Matrix.GetColumn(1));
            Bounds bounds = b.Mesh.bounds;
            s.City.AddBuilding(position, rotation, bounds.min, bounds.max, null, i + 1);
        }
        s.City.Finish();
    }

    /// <summary>
    /// The rooms fitted to every stand-in's windows, as the mod's RoomFitMap fits a city's assets: its
    /// model and its shine map at 512 x 512 (each texel the average of what it covers, as the GPU's
    /// read-back of a mip gives) through RoomFit.Measure, a row each; every building's place.
    /// </summary>
    private static void BuildRoomFits(Scene s)
    {
        if (!s.RoomFits.Create(ColorMapWidth, ColorMapHeight))
            throw new Exception("RoomFitTextures.Create: " + s.RoomFits.Problem);

        const int side = 512;
        int found = 0, sides = 0, off = 0;
        var storeys = new List<string>();

        // Every fit goes through the mod's cache file's text and back (RoomFitCache: as a later load
        // finds them), and its fingerprint is taken as the mod takes it (Mesh.GetIndexCount, probed here).
        var cache = new RoomFitCache();
        var fingerprints = new string[s.Buildings.Count];
        var results = new RoomFitResult[s.Buildings.Count];
        for (int i = 0; i < s.Buildings.Count; i++)
        {
            Building b = s.Buildings[i];
            int id = i + 1;
            Color[] xys = b.Xys.GetPixels(0);
            int w = b.Xys.width, h = b.Xys.height;
            var shine = new byte[side * side];
            for (int y = 0; y < side; y++)
            {
                for (int x = 0; x < side; x++)
                {
                    int x0 = x * w / side, x1 = Mathf.Max(x0 + 1, (x + 1) * w / side), y0 = y * h / side, y1 = Mathf.Max(y0 + 1, (y + 1) * h / side);
                    float sum = 0f;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        for (int xx = x0; xx < x1; xx++)
                            sum += 1f - xys[yy * w + xx].b;
                    }
                    shine[y * side + x] = (byte)Mathf.RoundToInt(Mathf.Clamp01(sum / ((x1 - x0) * (y1 - y0))) * 255f);
                }
            }

            Vector3[] v = b.Mesh.vertices;
            Vector2[] uv = b.Mesh.uv;
            var pos = new float[v.Length * 3];
            var uvs = new float[uv.Length * 2];
            for (int j = 0; j < v.Length; j++)
            {
                pos[3 * j] = v[j].x;
                pos[3 * j + 1] = v[j].y;
                pos[3 * j + 2] = v[j].z;
            }
            for (int j = 0; j < uv.Length; j++)
            {
                uvs[2 * j] = uv[j].x;
                uvs[2 * j + 1] = uv[j].y;
            }
            float cellWidth = RoomFit.CellWidth(RoomKindOf(b, id));
            RoomFitResult measured = RoomFit.Measure(pos, uvs, b.Mesh.triangles, shine, side, 1f, 1f, 0f, 0f, b.Xys.wrapMode == TextureWrapMode.Clamp, cellWidth);
            fingerprints[i] = RoomFitCache.Fingerprint(b.Mesh, b.Xys, new Vector4(1f, 1f, 0f, 0f), cellWidth);
            cache.Put("stand-in " + id, fingerprints[i], measured);
            results[i] = measured;
        }

        int skipped, back = 0;
        string reason;
        RoomFitCache read = RoomFitCache.Parse(cache.Format(), out skipped, out reason);
        if (reason.Length > 0)
            throw new Exception("RoomFitCache.Parse: " + reason);
        for (int i = 0; i < s.Buildings.Count; i++)
        {
            RoomFitResult again;
            if (read.TryGet("stand-in " + (i + 1), fingerprints[i], out again))
            {
                results[i] = again;
                back++;
            }
        }
        Debug.Log("RG PREVIEW: room fits through the cache's text: " + back + " of " + s.Buildings.Count + " read back, " + skipped +
                  " lines skipped; a fingerprint: " + fingerprints[0]);

        for (int i = 0; i < s.Buildings.Count; i++)
        {
            Building b = s.Buildings[i];
            int id = i + 1;
            RoomFitResult r = results[i];
            int row = r.Measured ? id : 0;
            if (r.Measured)
            {
                s.RoomFits.SetFit(row, r);
                if (r.Storey > 0f)
                {
                    found++;
                    if (storeys.Count < 6)
                        storeys.Add(b.Kind + " " + r.Storey.ToString("F3", CultureInfo.InvariantCulture) + " m from " +
                                    r.GroundTop.ToString("F2", CultureInfo.InvariantCulture) + " m");
                }
                sides += r.SidesFound;
                if (!r.Rooms)
                    off++;
            }
            Vector3 origin = b.Matrix.GetColumn(3);
            s.RoomFits.SetPlace(id, origin, ((Vector3)b.Matrix.GetColumn(0)).normalized, ((Vector3)b.Matrix.GetColumn(2)).normalized, row);
        }
        if (!s.RoomFits.Upload())
            throw new Exception("RoomFitTextures.Upload: " + s.RoomFits.Problem);
        Debug.Log("RG PREVIEW: rooms fitted: storeys found on " + found + " of " + s.Buildings.Count + " (" + string.Join("; ", storeys.ToArray()) +
                  "), " + sides + " sides, " + off + " with no rooms");
    }

    /// <summary>RenderManager.GetColorLocation: the texel centre of object id in the colour map.</summary>
    private static Vector4 ColorIndex(int id)
    {
        return new Vector4((id % ColorMapWidth + 0.5f) / ColorMapWidth, (id / ColorMapWidth + 0.5f) / ColorMapHeight, 1f, 0f);
    }

    /// <summary>
    /// The middle and far tiers as the game builds them: every building's textures packed into
    /// LOD atlases (BuildingManager.m_lodRgb/Xys/AciAtlas), its LOD mesh with uv remapped into its
    /// rectangle and the rectangle itself in uv3 (x, y) / uv4 (w, h), eight copies of it with
    /// Color32.a = k x 32 (BuildingInfoBase.InitMeshData), and the whole city baked into one
    /// world-space mesh (BuildingAI.PopulateGroupData: world normals and tangents).
    /// </summary>
    private static void BuildLodTiers(Scene s)
    {
        const int slotW = 64, slotH = 256, atlasW = 1024, atlasH = 2048, perRow = atlasW / slotW;
        var main = new Color[atlasW * atlasH];
        var xys = new Color[atlasW * atlasH];
        var aci = new Color[atlasW * atlasH];
        var mainT = new Color[atlasW * atlasH];
        var xysT = new Color[atlasW * atlasH];
        var aciT = new Color[atlasW * atlasH];
        var bakedSet = new BakedLists();
        var bakedTurnedSet = new BakedLists();

        for (int i = 0; i < s.Buildings.Count; i++)
        {
            Building b = s.Buildings[i];
            int sx = (i % perRow) * slotW, sy = (i / perRow) * slotH;
            if (sy + slotH > atlasH)
                throw new Exception("the preview's LOD atlas is full");

            // Every third tower's LOD laid out otherwise ("Lod=turned"); the full-glass towers stay.
            b.Turned = b.Kind != "punched" && b.Kind != "fullglass" && b.Kind != "fullglass2" && i % 3 == 0;

            // Each atlas texel the AVERAGE of the facade texels it covers, as a baked LOD texture
            // is a downsampled one: a point sample of every fourth mullion would draw moire that no
            // game atlas has.
            Color[] srcMain = b.Main.GetPixels(0), srcXys = b.Xys.GetPixels(0), srcAci = b.Aci.GetPixels(0);
            int srcW = b.Main.width, srcH = b.Main.height;
            FillSlot(main, atlasW, sx, sy, slotW, slotH, srcMain, srcW, srcH, false);
            FillSlot(xys, atlasW, sx, sy, slotW, slotH, srcXys, srcW, srcH, false);
            FillSlot(aci, atlasW, sx, sy, slotW, slotH, srcAci, srcW, srcH, false);
            FillSlot(mainT, atlasW, sx, sy, slotW, slotH, srcMain, srcW, srcH, b.Turned);
            FillSlot(xysT, atlasW, sx, sy, slotW, slotH, srcXys, srcW, srcH, b.Turned);
            FillSlot(aciT, atlasW, sx, sy, slotW, slotH, srcAci, srcW, srcH, b.Turned);

            var rect = new Rect((float)sx / atlasW, (float)sy / atlasH, (float)slotW / atlasW, (float)slotH / atlasH);
            b.AtlasRect = new Vector4(rect.x, rect.y, rect.width, rect.height);
            b.Combined8 = Combined(b, rect, false);
            b.Combined8Turned = b.Turned ? Combined(b, rect, true) : b.Combined8;
            Bake(bakedSet, b, rect, false);
            Bake(bakedTurnedSet, b, rect, b.Turned);
        }

        s.AtlasMain = Tex(atlasW, atlasH, main, false);
        s.AtlasXys = Tex(atlasW, atlasH, xys, true);
        s.AtlasAci = Tex(atlasW, atlasH, aci, true);
        s.AtlasXysDull = Tex(atlasW, atlasH, Dull(xys), true);
        s.AtlasMainTurned = Tex(atlasW, atlasH, mainT, false);
        s.AtlasXysTurnedDull = Tex(atlasW, atlasH, Dull(xysT), true);
        s.AtlasAciTurned = Tex(atlasW, atlasH, aciT, true);
        s.AtlasXysWrong = Tex(atlasW, atlasH, Moved(xys, atlasW, atlasH, slotW), true);
        foreach (Material m in new[] { s.GlassLod, s.GlassGroup })
        {
            m.SetTexture("_MainTex", s.AtlasMain);
            m.SetTexture("_XYSMap", s.AtlasXys);
            m.SetTexture("_ACIMap", s.AtlasAci);
        }

        s.Baked = bakedSet.Mesh();
        s.BakedTurned = bakedTurnedSet.Mesh();
    }

    /// <summary>
    /// One building's slot of an atlas: each texel the average of the source texels it covers. turned:
    /// the slot laid out about the diagonal -- its texel at (s, t) is the source's at (t, s), as an
    /// LOD whose uv are the model's turned (u, v) -> (v, u) needs.
    /// </summary>
    private static void FillSlot(Color[] dst, int atlasW, int sx, int sy, int slotW, int slotH, Color[] src, int srcW, int srcH, bool turned)
    {
        for (int y = 0; y < slotH; y++)
        {
            for (int x = 0; x < slotW; x++)
            {
                float s0 = (float)x / slotW, s1 = (float)(x + 1) / slotW, t0 = (float)y / slotH, t1 = (float)(y + 1) / slotH;
                float u0 = turned ? t0 : s0, u1 = turned ? t1 : s1, v0 = turned ? s0 : t0, v1 = turned ? s1 : t1;
                int x0 = Mathf.Min(srcW - 1, (int)(u0 * srcW)), x1 = Mathf.Max(x0 + 1, Mathf.Min(srcW, (int)(u1 * srcW)));
                int y0 = Mathf.Min(srcH - 1, (int)(v0 * srcH)), y1 = Mathf.Max(y0 + 1, Mathf.Min(srcH, (int)(v1 * srcH)));
                Color sum = Color.clear;
                for (int yy = y0; yy < y1; yy++)
                {
                    for (int xx = x0; xx < x1; xx++)
                        sum += src[yy * srcW + xx];
                }
                dst[(sy + y) * atlasW + sx + x] = sum / ((y1 - y0) * (x1 - x0));
            }
        }
    }

    /// <summary>The same shine map with no shine anywhere (z = 1): what an LOD with no _lod_s gets.</summary>
    private static Color[] Dull(Color[] xys)
    {
        var dull = new Color[xys.Length];
        for (int i = 0; i < xys.Length; i++)
            dull[i] = new Color(xys[i].r, xys[i].g, 1f, xys[i].a);
        return dull;
    }

    /// <summary>Each slot's shine moved half a slot along (wrapping round it), and a third up: glass where its model has wall.</summary>
    private static Color[] Moved(Color[] xys, int atlasW, int atlasH, int slotW)
    {
        var moved = new Color[xys.Length];
        for (int y = 0; y < atlasH; y++)
        {
            for (int x = 0; x < atlasW; x++)
            {
                int slot = x / slotW * slotW, from = slot + (x - slot + slotW / 2) % slotW;
                int fromY = (y / 256) * 256 + (y % 256 + 85) % 256;
                Color c = xys[y * atlasW + x];
                moved[y * atlasW + x] = new Color(c.r, c.g, xys[fromY * atlasW + from].b, c.a);
            }
        }
        return moved;
    }

    /// <summary>A building's LOD uv: its model's, or turned about the diagonal.</summary>
    private static Vector2 LodUv(Vector2 uv, bool turned)
    {
        return turned ? new Vector2(uv.y, uv.x) : uv;
    }

    /// <summary>Eight copies of a building's LOD mesh, as the game's combined LOD mesh.</summary>
    private static Mesh Combined(Building b, Rect rect, bool turned)
    {
        Vector3[] v0 = b.Mesh.vertices;
        Vector3[] n0 = b.Mesh.normals;
        Vector4[] t0 = b.Mesh.tangents;
        Vector2[] uv0 = b.Mesh.uv;
        int[] tri0 = b.Mesh.triangles;
        var cv = new List<Vector3>();
        var cn = new List<Vector3>();
        var ct = new List<Vector4>();
        var cuv = new List<Vector2>();
        var cuv3 = new List<Vector2>();
        var cuv4 = new List<Vector2>();
        var cc = new List<Color32>();
        var ctri = new List<int>();
        for (int copy = 0; copy < 8; copy++)
        {
            int start = cv.Count;
            for (int j = 0; j < v0.Length; j++)
            {
                Vector2 uv = LodUv(uv0[j], turned);
                cv.Add(v0[j]);
                cn.Add(n0[j]);
                ct.Add(t0[j]);
                cuv.Add(new Vector2(rect.x + uv.x * rect.width, rect.y + uv.y * rect.height));
                cuv3.Add(new Vector2(rect.x, rect.y));
                cuv4.Add(new Vector2(rect.width, rect.height));
                cc.Add(new Color32(255, 255, 255, (byte)(copy * 32)));
            }
            foreach (int t in tri0)
                ctri.Add(start + t);
        }

        var mesh = new Mesh();
        mesh.SetVertices(cv);
        mesh.SetNormals(cn);
        mesh.SetTangents(ct);
        mesh.SetUVs(0, cuv);
        mesh.SetUVs(2, cuv3);
        mesh.SetUVs(3, cuv4);
        mesh.colors32 = cc.ToArray();
        mesh.SetTriangles(ctri, 0);
        return mesh;
    }

    /// <summary>The city baked into one world-space mesh, as BuildingAI.PopulateGroupData bakes a block.</summary>
    private sealed class BakedLists
    {
        public readonly List<Vector3> V = new List<Vector3>(), N = new List<Vector3>();
        public readonly List<Vector4> T = new List<Vector4>();
        public readonly List<Vector2> Uv = new List<Vector2>(), Uv2 = new List<Vector2>(), Uv3 = new List<Vector2>(), Uv4 = new List<Vector2>();
        public readonly List<int> Tris = new List<int>();

        public Mesh Mesh()
        {
            var m = new Mesh();
            m.SetVertices(V);
            m.SetNormals(N);
            m.SetTangents(T);
            m.SetUVs(0, Uv);
            m.SetUVs(1, Uv2);
            m.SetUVs(2, Uv3);
            m.SetUVs(3, Uv4);
            var white = new Color32[V.Count];
            for (int i = 0; i < white.Length; i++)
                white[i] = new Color32(255, 255, 255, 255);
            m.colors32 = white;
            m.SetTriangles(Tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    private static void Bake(BakedLists l, Building b, Rect rect, bool turned)
    {
        Vector3[] v0 = b.Mesh.vertices;
        Vector3[] n0 = b.Mesh.normals;
        Vector4[] t0 = b.Mesh.tangents;
        Vector2[] uv0 = b.Mesh.uv;
        int bs = l.V.Count;
        for (int j = 0; j < v0.Length; j++)
        {
            Vector2 uv = LodUv(uv0[j], turned);
            l.V.Add(b.Matrix.MultiplyPoint3x4(v0[j]));
            l.N.Add(b.Matrix.MultiplyVector(n0[j]).normalized);
            Vector3 tw = b.Matrix.MultiplyVector(new Vector3(t0[j].x, t0[j].y, t0[j].z)).normalized;
            l.T.Add(new Vector4(tw.x, tw.y, tw.z, t0[j].w));
            l.Uv.Add(new Vector2(rect.x + uv.x * rect.width, rect.y + uv.y * rect.height));
            l.Uv2.Add(new Vector2(b.ObjectIndex.x, b.ObjectIndex.y));   // PopulateGroupData: uvs2 = the colour index
            l.Uv3.Add(new Vector2(rect.x, rect.y));
            l.Uv4.Add(new Vector2(rect.width, rect.height));
        }
        foreach (int t in b.Mesh.triangles)
            l.Tris.Add(bs + t);
    }

    /// <summary>
    /// The variant's LOD atlases ("Lod=own|dull|turned|wrong") on the LOD and far materials, and the
    /// mod's LodGlass run over them as it runs in a city: every building's LOD read back, compared
    /// with its model by their geometry, decided at the variant's settings (LodGlass, Threshold,
    /// "LodMode"), the mask made, uploaded and bound. Its verdicts for the frame's log line.
    /// </summary>
    private static string PrepareLod(Scene s, GlassParams p)
    {
        bool turned = _lod == "turned";
        Texture2D atlasMain = turned ? s.AtlasMainTurned : s.AtlasMain;
        Texture2D atlasXys = _lod == "dull" ? s.AtlasXysDull : (turned ? s.AtlasXysTurnedDull : (_lod == "wrong" ? s.AtlasXysWrong : s.AtlasXys));
        Texture2D atlasAci = turned ? s.AtlasAciTurned : s.AtlasAci;
        foreach (Material m in new[] { s.GlassLod, s.GlassGroup })
        {
            m.SetTexture("_MainTex", atlasMain);
            m.SetTexture("_XYSMap", atlasXys);
            m.SetTexture("_ACIMap", atlasAci);
        }

        int maskW, maskH;
        LodGlass.MaskSize(atlasXys.width, atlasXys.height, out maskW, out maskH);
        var entries = new List<LodGlassEntry>();
        foreach (Building b in s.Buildings)
        {
            bool t = turned && b.Turned;
            Vector3[] v = b.Mesh.vertices;
            Vector2[] uv = b.Mesh.uv;
            var pos = new float[v.Length * 3];
            var modelUv = new float[uv.Length * 2];
            var lodUv = new float[uv.Length * 2];
            for (int j = 0; j < v.Length; j++)
            {
                pos[3 * j] = v[j].x;
                pos[3 * j + 1] = v[j].y;
                pos[3 * j + 2] = v[j].z;
            }
            for (int j = 0; j < uv.Length; j++)
            {
                Vector2 l = LodUv(uv[j], t);
                modelUv[2 * j] = uv[j].x;
                modelUv[2 * j + 1] = uv[j].y;
                lodUv[2 * j] = l.x;
                lodUv[2 * j + 1] = l.y;
            }
            var e = new LodGlassEntry
            {
                Asset = b.Kind + (t ? " (turned)" : ""),
                Name = b.Kind,
                Rect = b.AtlasRect,
                ModelXys = b.Xys,
                ModelSt = new Vector4(1f, 1f, 0f, 0f),
                ModelClamp = b.Xys.wrapMode == TextureWrapMode.Clamp,
                ModelPos = pos,
                ModelUv = modelUv,
                ModelTri = b.Mesh.triangles,
                LodPos = (float[])pos.Clone(),
                LodUv = lodUv,
                LodTri = b.Mesh.triangles,
                Mode = _lodMode,
                Threshold = p.Threshold,
            };
            LodGlass.SideFor(e, maskW, maskH);
            entries.Add(e);
        }

        for (int start = 0; start < entries.Count; start += LodGlass.PerBatch)
        {
            if (!s.LodGlass.ReadBack(entries, start, Math.Min(LodGlass.PerBatch, entries.Count - start), atlasXys, s.Util))
                throw new Exception("LodGlass.ReadBack: " + s.LodGlass.Problem);
        }
        s.LodGlass.FreeReadBack();
        foreach (LodGlassEntry e in entries)
            LodGlass.Transfer(e);
        LodGlass.DecideAll(entries, p.LodGlass);
        var borrowing = new List<LodGlassEntry>();
        foreach (LodGlassEntry e in entries)
        {
            if (LodGlass.Borrows(e.Verdict))
                borrowing.Add(e);
        }
        if (!s.LodGlass.Upload(borrowing.Count > 0 ? LodGlass.BuildMask(borrowing, maskW, maskH) : null, maskW, maskH))
            throw new Exception("LodGlass.Upload: " + s.LodGlass.Problem);
        s.LodGlass.Bind();

        var counts = new int[9];
        float reachMin = 2f, agreeMin = 2f, agreeMax = -2f;
        foreach (LodGlassEntry e in entries)
        {
            counts[e.Verdict]++;
            reachMin = Mathf.Min(reachMin, e.Reach);
            if (e.ModelShare >= LodGlass.MinShare)
            {
                agreeMin = Mathf.Min(agreeMin, e.Agreement);
                agreeMax = Mathf.Max(agreeMax, e.Agreement);
            }
        }
        return "lod " + _lod + " mode " + _lodMode + ": agree " + counts[LodGlass.VerdictAgrees] + ", no glass " + counts[LodGlass.VerdictNoGlass] +
               ", the model's " + counts[LodGlass.VerdictBorrowed] + ", shape " + counts[LodGlass.VerdictShapeDiffers] +
               ", forced " + counts[LodGlass.VerdictForced] + ", kept " + counts[LodGlass.VerdictKeptOwn] + ", off " + counts[LodGlass.VerdictOff] +
               ", failed " + counts[LodGlass.VerdictFailed] + "; reach >= " + Fmt2(reachMin) + ", alike " + Fmt2(agreeMin) + ".." + Fmt2(agreeMax) +
               "; mask " + s.LodGlass.MaskWidth + "x" + s.LodGlass.MaskHeight + " for " + borrowing.Count + (s.LodGlass.Flipped ? ", read back flipped" : "");
    }

    private static string Fmt2(float v)
    {
        return v > 1.5f || v < -1.5f ? "-" : v.ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A downtown on a 120 m grid (100 m blocks, 20 m streets): towers in the middle, lower blocks
    /// round them, one tower turned 30 degrees. Deterministic.
    /// </summary>
    private static void BuildCity(Scene s)
    {
        var rnd = new System.Random(20260928);
        Color[] glassColours =
        {
            new Color(0.25f, 0.36f, 0.45f), new Color(0.30f, 0.40f, 0.38f), new Color(0.17f, 0.20f, 0.23f),
            new Color(0.36f, 0.32f, 0.27f), new Color(0.34f, 0.43f, 0.52f), new Color(0.22f, 0.30f, 0.40f),
        };

        for (int bx = -3; bx <= 3; bx++)
        {
            for (int bz = -3; bz <= 3; bz++)
            {
                float cx = bx * 120f, cz = bz * 120f;
                float centre = Mathf.Sqrt(bx * bx + bz * bz);
                int count = centre < 1.6f ? 2 : 1 + rnd.Next(2);
                for (int k = 0; k < count; k++)
                {
                    bool tower = centre < 2.3f && rnd.NextDouble() < 0.85;
                    float w = tower ? 34f + (float)rnd.NextDouble() * 20f : 40f + (float)rnd.NextDouble() * 30f;
                    float h = tower ? 90f + (float)rnd.NextDouble() * (220f - centre * 50f) : 14f + (float)rnd.NextDouble() * 26f;
                    float ox = count == 1 ? 0f : (k == 0 ? -24f : 24f);
                    float oz = count == 1 ? 0f : (k == 0 ? -20f : 22f);
                    if (count > 1) w = Mathf.Min(w, 44f);
                    float turn = (bx == 1 && bz == 0 && k == 0) ? 30f : 0f;
                    string kind = tower ? (rnd.NextDouble() < 0.7 ? "curtain" : "fins") : "punched";
                    Color glassColour = glassColours[rnd.Next(glassColours.Length)];

                    // Off the random stream (the city stays the same): its id, whether a curtain
                    // wall is a plain glass box (no floors painted in, lit all alike).
                    int id = s.Buildings.Count + 1;
                    if (kind == "curtain" && Hash(id * 3.7f, 11.3f) < 0.45f)
                        kind = "glassbox";

                    // The middle block's two towers are FULL-GLASS assets: the whole facade marked
                    // shiny, its floors painted in its colour map only (what PaintedWall is for) --
                    // dark slab bands and light mullions; light floor lines over a painted sky.
                    if (bx == 0 && bz == 0)
                        kind = k == 0 ? "fullglass" : "fullglass2";
                    if (kind == "fullglass")
                        s.FullGlassId = id;

                    Building b = new Building();
                    b.Kind = kind;
                    b.ObjectIndex = ColorIndex(id);
                    b.Mesh = BoxMesh(w, h, w);
                    b.Matrix = Matrix4x4.TRS(new Vector3(cx + ox, 0f, cz + oz), Quaternion.Euler(0f, turn, 0f), Vector3.one);
                    MakeFacade(kind, w, h, glassColour, rnd.Next(), out b.Main, out b.Xys, out b.Aci);
                    s.Buildings.Add(b);
                }
            }
        }

        if (Env("RG_SHAPES", "0") == "1")
            AddShapes(s, rnd);
        if (Env("RG_FRAMED", "0") == "1")
            AddFramed(s);

        s.Ground = GroundMesh(4000f);
        MakeGround(out s.GroundMain, out s.GroundXys, out s.GroundAci);
    }

    /// <summary>
    /// "-Shapes": four towers in a row east of the city, on glass a room must not stand behind as the
    /// mod draws rooms (RoomShape): round and shaded smooth (its normal turns all the way round), a box
    /// whose corners are smoothed (its normal turns across every face), a glass pyramid (sloped), and a
    /// 12-sided tower shaded flat -- the control, whose faces are flat and upright.
    /// </summary>
    private static void AddShapes(Scene s, System.Random rnd)
    {
        var colour = new Color(0.25f, 0.36f, 0.45f);
        AddShape(s, rnd, "curtain", Prism(40, 18f, 120f, true), 2f * Mathf.PI * 18f, 120f, new Vector3(600f, 0f, -150f), colour);
        AddShape(s, rnd, "curtain", Prism(4, 25f, 100f, true), 4f * 35.4f, 100f, new Vector3(600f, 0f, -50f), colour);
        AddShape(s, rnd, "glassbox", Pyramid(50f, 35f), 50f, 35f, new Vector3(600f, 0f, 50f), colour);
        AddShape(s, rnd, "curtain", Prism(12, 20f, 90f, false), 12f * 10.35f, 90f, new Vector3(600f, 0f, 150f), colour);
    }

    /// <summary>The "-Framed" towers' gaps: how far each one's frames stand in front of its glass (m).</summary>
    private static readonly float[] FramedGaps = { 0.02f, 0.05f, 0.1f, 0.25f, 1f };

    /// <summary>
    /// "-Framed": five towers in a row west of the city (views "framed", "framedmid", "framedfar"), glass
    /// all over, with window frames as their own geometry (spec 0, the roof's light texels) standing
    /// FramedGaps in front of the glass -- an asset whose frames are modelled, or an alpha-mapped plane,
    /// in front of a shiny one.
    /// </summary>
    private static void AddFramed(Scene s)
    {
        var rnd = new System.Random(20261029);
        var colour = new Color(0.17f, 0.20f, 0.23f);
        for (int k = 0; k < FramedGaps.Length; k++)
        {
            var b = new Building();
            b.Kind = "framed";
            b.ObjectIndex = ColorIndex(s.Buildings.Count + 1);
            b.Mesh = FramedMesh(30f, 60f, FramedGaps[k]);
            b.Matrix = Matrix4x4.TRS(new Vector3(-700f, 0f, -240f + 120f * k), Quaternion.identity, Vector3.one);
            MakeFacade("framed", 30f, 60f, colour, rnd.Next(), out b.Main, out b.Xys, out b.Aci);
            b.Framed = k + 1;

            // "Mark=k": the same with its frames pure red, to count the frame pixels the glass leaves.
            Color[] px = b.Main.GetPixels();
            int tw = b.Main.width, th = b.Main.height;
            for (int y = 0; y < th; y++)
            {
                if ((y + 0.5f) / th < 0.995f)
                    continue;
                for (int x = 0; x < tw; x++)
                    px[y * tw + x] = new Color(1f, 0f, 0f, 1f);
            }
            b.MainMarked = Tex(tw, th, px, false);
            s.Buildings.Add(b);
        }
    }

    /// <summary>A box (BoxMesh) and, gap in front of each side, a grid of flat frames: mullions 0.12 m wide every 1.5 m, transoms 0.25 m tall every storey.</summary>
    private static Mesh FramedMesh(float w, float h, float gap)
    {
        Mesh box = BoxMesh(w, h, w);
        var verts = new List<Vector3>(box.vertices);
        var norms = new List<Vector3>(box.normals);
        var tans = new List<Vector4>(box.tangents);
        var uvs = new List<Vector2>(box.uv);
        var tris = new List<int>(box.triangles);
        float floorH = ParseFloat(Env("RG_STOREY", "3.8"));
        float e = w * 0.5f;
        Vector3[] normals = { new Vector3(0, 0, -1), new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(-1, 0, 0) };
        for (int side = 0; side < 4; side++)
        {
            Vector3 n = normals[side];
            Vector3 along = new Vector3(-n.z, 0f, n.x);   // left to right seen from outside
            Vector3 origin = n * (e + gap) - along * e;
            for (float u = 0f; u <= w + 0.01f; u += 1.5f)
                FrameQuad(verts, norms, tans, uvs, tris, origin + along * (u - 0.06f), along * 0.12f, Vector3.up * h, n);
            for (float v = 0f; v <= h + 0.01f; v += floorH)
                FrameQuad(verts, norms, tans, uvs, tris, origin + Vector3.up * (v - 0.125f), along * w, Vector3.up * 0.25f, n);
        }
        return Finish(verts, norms, tans, uvs, tris);
    }

    /// <summary>A flat frame from corner p across a (left to right) and b (up), facing n, mapped to the texels above the roof's.</summary>
    private static void FrameQuad(List<Vector3> verts, List<Vector3> norms, List<Vector4> tans, List<Vector2> uvs, List<int> tris,
                                  Vector3 p, Vector3 a, Vector3 b, Vector3 n)
    {
        int i0 = verts.Count;
        verts.Add(p); verts.Add(p + a); verts.Add(p + a + b); verts.Add(p + b);
        Vector3 t = a.normalized;
        for (int i = 0; i < 4; i++) { norms.Add(n); tans.Add(new Vector4(t.x, t.y, t.z, 1f)); }
        // One texel row above the roof's (v >= 0.995): no uv slope, so mip 0 at every distance, and
        // "Mark=k" can paint the frames alone.
        for (int i = 0; i < 4; i++)
            uvs.Add(new Vector2(0.5f, 0.997f));
        tris.AddRange(new[] { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 });
    }

    private static void AddShape(Scene s, System.Random rnd, string kind, Mesh mesh, float w, float h, Vector3 at, Color colour)
    {
        var b = new Building();
        b.Kind = kind;
        b.ObjectIndex = ColorIndex(s.Buildings.Count + 1);
        b.Mesh = mesh;
        b.Matrix = Matrix4x4.TRS(at, Quaternion.identity, Vector3.one);
        MakeFacade(kind, w, h, colour, rnd.Next(), out b.Main, out b.Xys, out b.Aci);
        s.Buildings.Add(b);
    }

    /// <summary>
    /// An upright prism of n sides round its axis (a square box at n = 4, turned 45 degrees), its sides
    /// mapped u 0..1 round, v 0..0.9 up, a flat roof. smooth: each corner's normal the average of the
    /// two sides meeting there, as a mesh shaded smooth has it; else each side its own.
    /// </summary>
    private static Mesh Prism(int n, float radius, float h, bool smooth)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var tans = new List<Vector4>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        for (int k = 0; k < n; k++)
        {
            float a0 = 2f * Mathf.PI * k / n, a1 = 2f * Mathf.PI * (k + 1) / n, am = 0.5f * (a0 + a1);
            Vector3 p0 = new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) * radius, p1 = new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) * radius;
            Vector3 face = new Vector3(Mathf.Cos(am), 0f, Mathf.Sin(am));
            Vector3 n0 = smooth ? new Vector3(Mathf.Cos(a0), 0f, Mathf.Sin(a0)) : face, n1 = smooth ? new Vector3(Mathf.Cos(a1), 0f, Mathf.Sin(a1)) : face;
            Vector3 t = (p1 - p0).normalized;
            int i0 = verts.Count;
            verts.Add(p0); verts.Add(p1); verts.Add(p1 + Vector3.up * h); verts.Add(p0 + Vector3.up * h);
            norms.Add(n0); norms.Add(n1); norms.Add(n1); norms.Add(n0);
            for (int i = 0; i < 4; i++)
                tans.Add(new Vector4(t.x, t.y, t.z, 1f));
            float u0 = (float)k / n, u1 = (float)(k + 1) / n;
            uvs.Add(new Vector2(u0, 0f)); uvs.Add(new Vector2(u1, 0f)); uvs.Add(new Vector2(u1, 0.9f)); uvs.Add(new Vector2(u0, 0.9f));
            tris.AddRange(new[] { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 });
        }

        // The roof: a fan round its middle.
        int c = verts.Count;
        verts.Add(Vector3.up * h);
        norms.Add(Vector3.up);
        tans.Add(new Vector4(1f, 0f, 0f, 1f));
        uvs.Add(new Vector2(0.5f, 0.95f));
        for (int k = 0; k < n; k++)
        {
            float a = 2f * Mathf.PI * k / n;
            verts.Add(new Vector3(Mathf.Cos(a) * radius, h, Mathf.Sin(a) * radius));
            norms.Add(Vector3.up);
            tans.Add(new Vector4(1f, 0f, 0f, 1f));
            uvs.Add(new Vector2(0.5f + 0.4f * Mathf.Cos(a) * 0.5f, 0.95f));
        }
        for (int k = 0; k < n; k++)
            tris.AddRange(new[] { c, c + 1 + (k + 1) % n, c + 1 + k });
        return Finish(verts, norms, tans, uvs, tris);
    }

    /// <summary>A glass pyramid on a square base, its four faces mapped u 0..1 along the base, v 0..0.9 up to the apex.</summary>
    private static Mesh Pyramid(float side, float h)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var tans = new List<Vector4>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float e = side * 0.5f;
        Vector3 apex = Vector3.up * h;
        Vector3[] corners = { new Vector3(-e, 0f, -e), new Vector3(e, 0f, -e), new Vector3(e, 0f, e), new Vector3(-e, 0f, e) };
        for (int k = 0; k < 4; k++)
        {
            Vector3 a = corners[k], b = corners[(k + 1) % 4];
            Vector3 n = Vector3.Cross(b - a, apex - a).normalized;
            Vector3 mid = (a + b) * 0.5f;
            if (Vector3.Dot(n, new Vector3(mid.x, 0f, mid.z)) < 0f)
                n = -n;
            Vector3 t = (b - a).normalized;
            int i0 = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(apex);
            for (int i = 0; i < 3; i++)
            {
                norms.Add(n);
                tans.Add(new Vector4(t.x, t.y, t.z, 1f));
            }
            uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(1f, 0f)); uvs.Add(new Vector2(0.5f, 0.9f));
            tris.AddRange(new[] { i0, i0 + 2, i0 + 1 });
        }
        return Finish(verts, norms, tans, uvs, tris);
    }

    private static Mesh Finish(List<Vector3> verts, List<Vector3> norms, List<Vector4> tans, List<Vector2> uvs, List<int> tris)
    {
        Mesh m = new Mesh();
        m.SetVertices(verts);
        m.SetNormals(norms);
        m.SetTangents(tans);
        m.SetUVs(0, uvs);
        var colours = new Color[verts.Count];
        for (int i = 0; i < colours.Length; i++)
            colours[i] = Color.white;
        m.colors = colours;
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        return m;
    }

    // A box on its base (y = 0 .. h), the four facades mapped u 0..1 across, v 0..0.9 up; the
    // roof into v 0.9..1 -- one texture per building, the way an asset has one.
    private static Mesh BoxMesh(float w, float h, float d)
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var tans = new List<Vector4>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float x = w * 0.5f, z = d * 0.5f;

        // Four sides: corners (a -> b) along the facade, outward normal n.
        AddQuad(verts, norms, tans, uvs, tris, new Vector3(-x, 0, -z), new Vector3(x, 0, -z), h, new Vector3(0, 0, -1), 0f, 0.9f);
        AddQuad(verts, norms, tans, uvs, tris, new Vector3(x, 0, -z), new Vector3(x, 0, z), h, new Vector3(1, 0, 0), 0f, 0.9f);
        AddQuad(verts, norms, tans, uvs, tris, new Vector3(x, 0, z), new Vector3(-x, 0, z), h, new Vector3(0, 0, 1), 0f, 0.9f);
        AddQuad(verts, norms, tans, uvs, tris, new Vector3(-x, 0, z), new Vector3(-x, 0, -z), h, new Vector3(-1, 0, 0), 0f, 0.9f);

        // Roof.
        int i0 = verts.Count;
        verts.Add(new Vector3(-x, h, -z)); verts.Add(new Vector3(-x, h, z)); verts.Add(new Vector3(x, h, z)); verts.Add(new Vector3(x, h, -z));
        for (int i = 0; i < 4; i++) { norms.Add(Vector3.up); tans.Add(new Vector4(1, 0, 0, 1)); }
        uvs.Add(new Vector2(0, 0.905f)); uvs.Add(new Vector2(0, 0.995f)); uvs.Add(new Vector2(1, 0.995f)); uvs.Add(new Vector2(1, 0.905f));
        tris.AddRange(new[] { i0, i0 + 1, i0 + 2, i0, i0 + 2, i0 + 3 });

        Mesh m = new Mesh();
        m.SetVertices(verts);
        m.SetNormals(norms);
        m.SetTangents(tans);
        m.SetUVs(0, uvs);
        var colours = new Color[verts.Count];
        for (int i = 0; i < colours.Length; i++)
            colours[i] = Color.white;
        m.colors = colours;
        m.SetTriangles(tris, 0);
        m.RecalculateBounds();
        return m;
    }

    private static void AddQuad(List<Vector3> verts, List<Vector3> norms, List<Vector4> tans, List<Vector2> uvs, List<int> tris,
                                Vector3 a, Vector3 b, float h, Vector3 n, float v0, float v1)
    {
        int i0 = verts.Count;
        verts.Add(a); verts.Add(b); verts.Add(b + Vector3.up * h); verts.Add(a + Vector3.up * h);
        Vector3 t = (b - a).normalized;
        for (int i = 0; i < 4; i++) { norms.Add(n); tans.Add(new Vector4(t.x, t.y, t.z, 1)); }
        uvs.Add(new Vector2(0, v0)); uvs.Add(new Vector2(1, v0)); uvs.Add(new Vector2(1, v1)); uvs.Add(new Vector2(0, v1));
        // Clockwise seen from outside (Unity's front face).
        tris.AddRange(new[] { i0, i0 + 2, i0 + 1, i0, i0 + 3, i0 + 2 });
    }

    private static Mesh GroundMesh(float size)
    {
        Mesh m = new Mesh();
        float e = size * 0.5f;
        m.vertices = new[] { new Vector3(-e, 0, -e), new Vector3(-e, 0, e), new Vector3(e, 0, e), new Vector3(e, 0, -e) };
        m.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
        m.tangents = new[] { new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1), new Vector4(1, 0, 0, 1) };
        // One texture tile = one 120 m block and its street.
        float tiles = size / 120f;
        m.uv = new[] { new Vector2(-tiles * 0.5f + 0.5f, -tiles * 0.5f + 0.5f), new Vector2(-tiles * 0.5f + 0.5f, tiles * 0.5f + 0.5f),
                       new Vector2(tiles * 0.5f + 0.5f, tiles * 0.5f + 0.5f), new Vector2(tiles * 0.5f + 0.5f, -tiles * 0.5f + 0.5f) };
        m.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
        m.RecalculateBounds();
        return m;
    }

    // ---------------------------------------------------------------------------------------
    // Textures painted the way the game's assets are.
    // ---------------------------------------------------------------------------------------

    private static void MakeFacade(string kind, float w, float h, Color glassColour, int seed,
                                   out Texture2D main, out Texture2D xys, out Texture2D aci)
    {
        int tw = 256, th = 1024;
        var rnd = new System.Random(seed);
        var cMain = new Color[tw * th];
        var cXys = new Color[tw * th];
        var cAci = new Color[tw * th];
        float floorH = ParseFloat(Env("RG_STOREY", "3.8"));
        float colW = kind == "punched" ? 3.0f : 1.5f;
        Color concrete = new Color(0.60f, 0.57f, 0.52f);
        Color mullion = new Color(0.34f, 0.35f, 0.37f);
        float panelSeed = (float)rnd.NextDouble() * 100f;

        for (int y = 0; y < th; y++)
        {
            float v = (y + 0.5f) / th;
            for (int x = 0; x < tw; x++)
            {
                float u = (x + 0.5f) / tw;
                int i = y * tw + x;
                if (v >= 0.9f)
                {
                    float n = Noise(x * 0.3f, y * 0.3f, seed);
                    // "framed": the roof's texels are also its frames' (light, so a frame the glass
                    // draws over shows at once).
                    cMain[i] = kind == "framed" ? new Color(0.85f, 0.84f, 0.80f) : concrete * (0.75f + 0.1f * n);
                    cXys[i] = new Color(0.5f, 0.5f, 0.95f, 1f);
                    cAci[i] = new Color(0f, 0f, 0f, 1f);
                    continue;
                }

                float um = u * w, vm = v / 0.9f * h;
                int floorIndex = Mathf.FloorToInt(vm / floorH);
                float inFloor = vm / floorH - floorIndex;
                int column = Mathf.FloorToInt(um / colW);
                float inCol = um / colW - column;
                float cell = Hash(column * 7.13f + panelSeed, floorIndex * 3.17f);
                float illum = 0.5f + 0.48f * Hash(column * 1.7f + 31f, floorIndex * 9.1f + panelSeed);
                if (GameLights)
                {
                    // Every kind of value an asset's illumination map holds: none, a constant glow,
                    // never lit (192), and the game's two bands.
                    float pick = Hash(column * 1.7f + 31f, floorIndex * 9.1f + panelSeed);
                    illum = pick < 0.12f ? 0f : (pick < 0.22f ? 0.2f : (pick < 0.4f ? 0.75f : 0.5f + 0.48f * (pick - 0.4f) / 0.6f));
                }

                Color c;
                float spec;
                float lit = 0f;
                if (kind == "framed")
                {
                    // Glass all over: the frames are their own mesh in front of it (FramedMesh).
                    c = glassColour * (0.9f + 0.15f * cell + 0.12f * inFloor);
                    spec = 0.9f;
                    lit = illum;
                }
                else if (kind == "fullglass")
                {
                    // Shiny all over (spec 0.92, a little less on the slab band: 0.84, still glass
                    // to the 70% threshold); the floors and the mullions are in the colour only.
                    bool slab = inFloor < 0.24f;
                    bool mull = inCol < 0.04f || inCol > 0.96f;
                    if (mull)
                        c = new Color(0.56f, 0.58f, 0.61f);
                    else if (slab)
                        c = glassColour * 0.42f;
                    else
                        c = glassColour * (0.9f + 0.1f * cell + 0.12f * inFloor);
                    spec = slab ? 0.84f : 0.92f;
                    lit = slab || mull ? 0f : illum;
                }
                else if (kind == "fullglass2")
                {
                    // A painted sky across the whole facade (lighter up high, soft clouds: what
                    // some asset painters put on glass), thin light floor lines, no mullions,
                    // shiny everywhere alike. The sky must stay glass; the lines must not.
                    float up = vm / h;
                    Color skyLow = new Color(0.22f, 0.27f, 0.32f), skyHigh = new Color(0.50f, 0.60f, 0.70f);
                    float cloud = Mathf.Clamp01((Noise(um * 0.08f, vm * 0.05f, seed) - 0.45f) * 2.5f);
                    c = Color.Lerp(skyLow, skyHigh, up) * (1f + 0.25f * cloud);
                    if (inFloor < 0.05f)
                        c = new Color(0.78f, 0.80f, 0.82f);
                    spec = 0.95f;
                    lit = inFloor < 0.05f ? 0f : 0.85f;
                }
                else if (kind == "punched")
                {
                    bool window = inCol > 0.2f && inCol < 0.8f && inFloor > 0.3f && inFloor < 0.86f && floorIndex > 0;
                    if (window)
                    {
                        c = new Color(0.20f, 0.27f, 0.34f) * (0.9f + 0.2f * cell);
                        spec = 0.86f;
                        lit = illum;
                    }
                    else
                    {
                        c = concrete * (0.92f + 0.12f * Noise(x * 0.5f, y * 0.5f, seed));
                        spec = 0.06f;
                    }
                }
                else
                {
                    // A glass box: thin mullions only, no spandrels painted in, one illumination
                    // value for all its glass (a whole tower lights at once, as such assets do).
                    bool box = kind == "glassbox";
                    bool fin = kind == "fins" && (column % 2 == 0) && inCol < 0.12f;
                    bool mull = box ? (inCol < 0.03f || inCol > 0.97f) : (inCol < 0.05f || inCol > 0.95f);
                    bool spandrel = !box && inFloor < 0.22f;
                    if (fin)
                    {
                        c = concrete * 1.05f;
                        spec = 0.1f;
                    }
                    else if (mull)
                    {
                        c = mullion;
                        spec = 0.45f;
                    }
                    else if (spandrel)
                    {
                        c = glassColour * 0.55f;
                        spec = 0.58f;
                    }
                    else if (box)
                    {
                        c = glassColour * (0.95f + 0.05f * cell);
                        spec = 0.9f;
                        lit = 0.85f;
                    }
                    else
                    {
                        // Asset painters shade glass: a little lighter at the top of each pane.
                        c = glassColour * (0.9f + 0.15f * cell + 0.12f * inFloor);
                        spec = 0.9f;
                        lit = illum;
                    }
                }

                cMain[i] = new Color(c.r, c.g, c.b, 1f);
                cXys[i] = new Color(0.5f, 0.5f, 1f - spec, 1f);
                cAci[i] = new Color(0f, 0f, lit, 1f);
            }
        }

        main = Tex(tw, th, cMain, false);
        xys = Tex(tw, th, cXys, true);
        aci = Tex(tw, th, cAci, true);
    }

    private static void MakeGround(out Texture2D main, out Texture2D xys, out Texture2D aci)
    {
        int n = 512;
        var cMain = new Color[n * n];
        var cXys = new Color[n * n];
        var cAci = new Color[n * n];
        Color asphalt = new Color(0.12f, 0.12f, 0.13f);
        Color walk = new Color(0.42f, 0.41f, 0.39f);
        Color paved = new Color(0.33f, 0.32f, 0.30f);
        Color grass = new Color(0.16f, 0.24f, 0.10f);
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                // One tile = 120 m: a 20 m street on two edges (centred on the tile's border).
                float mx = (x + 0.5f) / n * 120f, my = (y + 0.5f) / n * 120f;
                float dx = Mathf.Min(mx, 120f - mx), dy = Mathf.Min(my, 120f - my);
                float d = Mathf.Min(dx, dy);
                Color c;
                if (d < 7f)
                {
                    c = asphalt * (0.9f + 0.2f * Noise(x * 0.8f, y * 0.8f, 5));
                    // Lane markings.
                    if ((d < 0.25f) && (Mathf.Repeat(dx < dy ? my : mx, 9f) < 4.5f))
                        c = new Color(0.7f, 0.7f, 0.65f);
                }
                else if (d < 10f)
                    c = walk;
                else
                {
                    float p = Noise(x * 0.05f, y * 0.05f, 9);
                    c = p > 0.55f ? grass * (0.8f + 0.4f * Noise(x * 0.4f, y * 0.4f, 3)) : paved * (0.9f + 0.2f * Noise(x * 0.6f, y * 0.6f, 4));
                }

                int i = y * n + x;
                cMain[i] = c;
                cXys[i] = new Color(0.5f, 0.5f, 1f, 1f);
                cAci[i] = new Color(0f, 0f, 0f, 1f);
            }
        }

        main = Tex(n, n, cMain, false);
        xys = Tex(n, n, cXys, true);
        aci = Tex(n, n, cAci, true);
        main.wrapMode = TextureWrapMode.Repeat;
        xys.wrapMode = TextureWrapMode.Repeat;
        aci.wrapMode = TextureWrapMode.Repeat;
    }

    private static Texture2D Tex(int w, int h, Color[] pixels, bool linear)
    {
        var t = new Texture2D(w, h, TextureFormat.RGBA32, true, linear);
        t.SetPixels(pixels);
        t.Apply(true);
        t.wrapMode = TextureWrapMode.Clamp;
        t.filterMode = FilterMode.Trilinear;
        t.anisoLevel = 4;
        return t;
    }

    private static float Hash(float x, float y)
    {
        float h = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
        return h - Mathf.Floor(h);
    }

    private static float Noise(float x, float y, int seed)
    {
        float ix = Mathf.Floor(x), iy = Mathf.Floor(y);
        float fx = x - ix, fy = y - iy;
        fx = fx * fx * (3f - 2f * fx);
        fy = fy * fy * (3f - 2f * fy);
        float a = Hash(ix + seed, iy), b = Hash(ix + 1f + seed, iy), c = Hash(ix + seed, iy + 1f), d = Hash(ix + 1f + seed, iy + 1f);
        return Mathf.Lerp(Mathf.Lerp(a, b, fx), Mathf.Lerp(c, d, fx), fy);
    }

    // ---------------------------------------------------------------------------------------
    // One frame.
    // ---------------------------------------------------------------------------------------

    private static Color[] RenderView(Scene s, string view, string variant, float sunIntensity, out string info)
    {
        // The light of the moment.
        // "night" (the aerial camera) and "nightclose" (the close one) are both by night. "view@e"
        // (e.g. "street@4"): that camera with the sun e degrees up, lit as the game lights it then --
        // its sun and its sky x DayTime (GlassUniforms.GameSkyDay: noon's above 3.16 degrees), the
        // low sun's colour below 10.
        float? duskElev = null;
        int at = view.IndexOf('@');
        if (at > 0)
        {
            duskElev = float.Parse(view.Substring(at + 1), CultureInfo.InvariantCulture);
            view = view.Substring(0, at);
        }
        bool night = view.StartsWith("night");
        float sunElev = duskElev ?? (view == "low" ? 7f : (night ? -12f : 42f));
        float sunYaw = view == "low" ? 200f : 140f;
        Vector3 toSun = Quaternion.Euler(-sunElev, sunYaw, 0f) * Vector3.forward;
        float gameDay = duskElev.HasValue ? GlassUniforms.GameSkyDay(toSun.y) : 1f;
        bool lowSun = view == "low" || (duskElev.HasValue && sunElev < 10f);
        s.Sun.transform.rotation = Quaternion.LookRotation(-toSun);
        s.Sun.color = lowSun ? new Color(1f, 0.72f, 0.45f) : new Color(1f, 0.96f, 0.9f);
        s.Sun.intensity = night ? 0f : (duskElev.HasValue ? sunIntensity * gameDay : (view == "low" ? sunIntensity * 0.8f : sunIntensity));
        s.Moon.transform.rotation = Quaternion.LookRotation(-(Quaternion.Euler(-35f, 20f, 0f) * Vector3.forward));
        s.Moon.intensity = night ? 0.06f : 0f;
        s.Sky.SetFloat("_Exposure", night ? 0.05f : (duskElev.HasValue ? 1.3f * gameDay : (view == "low" ? 1.0f : 1.3f)));
        s.Sky.SetFloat("_SunSize", 0.035f);
        s.Sky.SetFloat("_AtmosphereThickness", view == "low" ? 1.3f : 1.0f);
        RenderSettings.ambientSkyColor = night ? new Color(0.012f, 0.015f, 0.03f) : (view == "low" ? new Color(0.42f, 0.44f, 0.55f) : new Color(0.45f, 0.55f, 0.72f));
        RenderSettings.ambientEquatorColor = night ? new Color(0.01f, 0.011f, 0.02f) : new Color(0.38f, 0.42f, 0.45f);
        RenderSettings.ambientGroundColor = night ? new Color(0.005f, 0.005f, 0.006f) : new Color(0.2f, 0.2f, 0.17f);
        Shader.SetGlobalFloat("_PreviewNight", night ? 1f : 0f);

        // The camera, city-builder style.
        Vector3 target;
        float yaw, pitch, dist;
        switch (view)
        {
            case "steep": target = new Vector3(0f, 40f, 0f); yaw = 60f; pitch = 55f; dist = 1000f; break;
            case "street":
            case "nightstreet": target = new Vector3(20f, 140f, 30f); yaw = 20f; pitch = -28f; dist = 160f; break;
            case "close":
            case "nightclose": target = new Vector3(0f, 60f, 0f); yaw = 35f; pitch = 20f; dist = 260f; break;
            case "near":
            case "nightnear": target = new Vector3(0f, 50f, 0f); yaw = 35f; pitch = 12f; dist = 110f; break;
            // The middle block's other full-glass tower, from the street on its far side.
            case "near2":
            case "nightnear2": target = new Vector3(0f, 50f, 0f); yaw = 215f; pitch = 12f; dist = 110f; break;
            // "-Shapes": the four towers east of the city, from further east, the city behind them.
            case "shapes":
            case "nightshapes": target = new Vector3(600f, 45f, 0f); yaw = 270f; pitch = 8f; dist = 300f; break;
            case "shapesnear":
            case "nightshapesnear": target = new Vector3(600f, 40f, -100f); yaw = 255f; pitch = 6f; dist = 130f; break;
            // "-Framed": the five framed towers from the south-west, a city-builder's pitch: their west
            // sides seen 45 degrees off, their south sides 66.
            case "framed": target = new Vector3(-700f, 30f, -120f); yaw = 60f; pitch = 35f; dist = 120f; break;
            case "framedmid": target = new Vector3(-700f, 30f, -60f); yaw = 60f; pitch = 35f; dist = 300f; break;
            case "framedfar": target = new Vector3(-700f, 30f, 0f); yaw = 60f; pitch = 35f; dist = 700f; break;
            default: target = new Vector3(0f, 70f, 0f); yaw = 30f; pitch = 32f; dist = 650f; break;
        }
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Vector3 camPos = target - rot * Vector3.forward * dist;
        bool street = view == "street" || view == "nightstreet";
        if (street)
            camPos = new Vector3(-40f, 3f, -95f);
        // The vehicles' street (BuildVehicles): "car" the hero car from 7 m, "cars" along the street from
        // the pavement, "carsabove" from 40 m up and back, "carsfar" from a city-builder's 140 m.
        string baseView = night ? view.Substring(5) : view;
        bool lookAt = street;
        switch (baseView)
        {
            // From the street's north side (the cars against the sun) and its south side (the sun behind
            // the camera, the towers behind the cars).
            case "car": camPos = new Vector3(-5.5f, 1.6f, StreetZ + 6.0f); target = new Vector3(0f, 0.9f, StreetZ + 1.8f); lookAt = true; break;
            case "carsouth": camPos = new Vector3(-4.5f, 1.6f, StreetZ - 4.0f); target = new Vector3(0f, 0.9f, StreetZ + 1.8f); lookAt = true; break;
            case "carback": camPos = new Vector3(6.0f, 1.6f, StreetZ - 3.5f); target = new Vector3(0f, 0.9f, StreetZ + 1.8f); lookAt = true; break;
            case "cars": camPos = new Vector3(-34f, 1.7f, StreetZ + 7.2f); target = new Vector3(0f, 1.0f, StreetZ - 1f); lookAt = true; break;
            case "carssouth": camPos = new Vector3(-34f, 1.7f, StreetZ - 7.2f); target = new Vector3(0f, 1.0f, StreetZ + 1f); lookAt = true; break;
            case "carsabove": camPos = new Vector3(-25f, 22f, StreetZ - 25f); target = new Vector3(0f, 0f, StreetZ); lookAt = true; break;
            case "carsfar": camPos = new Vector3(-80f, 90f, StreetZ - 50f); target = new Vector3(0f, 0f, StreetZ); lookAt = true; break;
            // The props' plaza (BuildProps, z = StreetZ - 16): from 12 m up, from eye height, from a
            // city-builder's 90 m; the sun behind the towers or behind the camera.
            case "props": camPos = new Vector3(-18f, 12f, StreetZ - 40f); target = new Vector3(6f, 0.5f, StreetZ - 16f); lookAt = true; break;
            case "propslow": camPos = new Vector3(-10f, 1.7f, StreetZ - 30f); target = new Vector3(4f, 0.6f, StreetZ - 16f); lookAt = true; break;
            case "propsnorth": camPos = new Vector3(-12f, 10f, StreetZ + 6f); target = new Vector3(6f, 0.5f, StreetZ - 16f); lookAt = true; break;
            case "propsfar": camPos = new Vector3(10f, 110f, StreetZ - 95f); target = new Vector3(14f, 0f, StreetZ - 16f); lookAt = true; break;
            case "propsclose": camPos = new Vector3(14f, 5f, StreetZ - 30f); target = new Vector3(24f, 0.6f, StreetZ - 16f); lookAt = true; break;
            // The decals (BuildDecals, z = StreetZ - 26): from 14 m up, and from eye height (the floor
            // mirrors at a grazing angle).
            case "decals": camPos = new Vector3(10f, 14f, StreetZ - 44f); target = new Vector3(10f, 0f, StreetZ - 26f); lookAt = true; break;
            case "decalslow": camPos = new Vector3(4f, 1.7f, StreetZ - 38f); target = new Vector3(10f, 0f, StreetZ - 24f); lookAt = true; break;
        }
        s.Camera.transform.position = camPos;
        s.Camera.transform.rotation = lookAt ? Quaternion.LookRotation(target - camPos) : rot;

        // The glass, as the mod sets it.
        bool drawGlass = variant.StartsWith("glass", StringComparison.OrdinalIgnoreCase);
        GlassParams p = GlassParams.Default();
        _debug = 0f;
        _debugLevel = 0f;
        _overcast = 0f;
        _litShare = 0.25f;
        _guardsOff = false;
        _fitOff = false;
        _tier = "near";
        _lod = "own";
        _lodMode = LodGlass.ModeAuto;
        _quality = GlassQuality.Ultra;
        _hizOff = false;
        _offsetFactor = 0f;
        _offsetUnits = -1f;
        GlassPrePass.HistoryWeight = HistoryDefault;
        _mark = 0;
        _budgetOverrides.Clear();
        _asset = new AssetOverride();
        _hour = null;
        _vehTinted = VehicleGlassLook.DefaultTintedShare;
        _vehStrength = 1f;
        _vehRefl = 1f;   // the mod's default (Settings.Defaults.VehicleReflections: 3x physics)
        _vehGlint = 1f;
        _vehEdge = 1f;
        _vehRough = 0.03f;
        _vehLenses = true;
        _vehCoat = 1f;   // the mod's default (Settings.Defaults.VehicleCoat)
        _vehCoatRough = VehicleGlassLook.CoatRoughness;
        _vehLiveries = false;
        _vehCabin = VehicleGlassLook.Cabin;
        _vehOff = false;
        _vehSingle = false;
        _bldOff = false;
        _propOff = false;
        _propRefl = _propClarity = -1f;
        _propShine = 0f;
        _propSurface = 0;
        _decalSurface = 0;
        _propPolish = _propGlint = _propSoft = _propWaterStrength = -1f;
        _propRipple = Vector4.one;
        _propHasColour = false;
        if (drawGlass)
            p = Override(p, variant);
        VehicleGlassLook.Apply(_vehTinted, _vehStrength, _vehRefl, _vehGlint, _vehEdge, _vehRough, _vehLenses,
                               _vehCoat, _vehCoatRough, _vehLiveries, _vehCabin);
        _night = night;
        foreach (Material m in new[] { s.Glass, s.GlassLod, s.GlassGroup, s.PrePass.Material(0), s.PrePass.Material(1), s.PrePass.Material(2) })
        {
            m.SetFloat("_RGOffsetFactor", _offsetFactor);
            m.SetFloat("_RGOffsetUnits", _offsetUnits);
        }

        // Every building's kind of rooms, as the mod's AssetMap writes it while there are rooms;
        // then the full-glass tower's own settings, as it writes an asset's.
        s.Assets.ClearAll();
        if (p.Interiors)
        {
            for (int i = 0; i < s.Buildings.Count; i++)
            {
                byte a = Interiors.Texel(RoomKindOf(s.Buildings[i], i + 1), Interiors.FollowGlobal);
                s.Assets.Set(i + 1, new Color32(AssetTextures.Global, AssetTextures.Global, AssetTextures.Global, a));
            }
        }
        if (_asset.Strength.HasValue || _asset.Detail.HasValue || _asset.HasGlass || _asset.Curtains.HasValue || _asset.IllumMap.HasValue)
        {
            int curtains = !_asset.Curtains.HasValue ? Interiors.FollowGlobal
                : (_asset.Curtains.Value ? Interiors.AssetOn : Interiors.AssetOff);
            int illumMap = !_asset.IllumMap.HasValue ? Interiors.FollowGlobal
                : (_asset.IllumMap.Value ? Interiors.AssetOn : Interiors.AssetOff);
            byte slot = AssetTextures.Global;
            if (_asset.HasGlass)
            {
                Color c = _asset.Tint ?? new Color(p.TintR, p.TintG, p.TintB);
                s.Assets.SetGlass(0, GlassUniforms.Tint(c.r, c.g, c.b), _asset.Own ?? p.OwnColour,
                    _asset.Reflectivity ?? p.Reflectivity, _asset.Clarity ?? p.Clarity, _asset.Threshold ?? p.Threshold,
                    _asset.Night ?? p.NightWindows);
                slot = 0;
            }
            s.Assets.Set(s.FullGlassId, new Color32(
                _asset.Strength.HasValue ? AssetTextures.Encode(_asset.Strength.Value) : AssetTextures.Global,
                _asset.Detail.HasValue ? AssetTextures.Encode(_asset.Detail.Value) : AssetTextures.Global,
                slot, p.Interiors ? Interiors.Texel(Interiors.KindOffice, Interiors.FollowGlobal, curtains, illumMap) : AssetTextures.Global));
        }
        s.Assets.Upload();
        s.Assets.Bind();
        if (_fitOff)
            s.RoomFits.Unbind();
        else
            s.RoomFits.Bind();
        string lodInfo = PrepareLod(s, p);
        Shader.SetGlobalVector("_RGDebug", new Vector4(_debug, _debugLevel, _guardsOff ? 1f : 0f, 0f));
        GlassFrame f = new GlassFrame();
        f.SunX = toSun.x; f.SunY = toSun.y; f.SunZ = toSun.z;
        Color sunLin = s.Sun.color.linear * s.Sun.intensity;
        f.SunR = sunLin.r; f.SunG = sunLin.g; f.SunB = sunLin.b;
        Color sky = RenderSettings.ambientSkyColor.linear, ground = RenderSettings.ambientGroundColor.linear;
        f.AmbSkyR = sky.r; f.AmbSkyG = sky.g; f.AmbSkyB = sky.b;
        f.AmbGroundR = ground.r; f.AmbGroundG = ground.g; f.AmbGroundB = ground.b;
        f.GroundY = 0f;
        f.Temperature = view == "low" ? 8f : 24f;
        f.ScreenHeight = s.Height;
        f.ColorMapWidth = ColorMapWidth;
        f.ColorMapHeight = ColorMapHeight;
        f.Hour = _hour ?? (night ? 21f : 13f);
        f.Clock = 24f * 10f + f.Hour;

        // The clouds' stand-in round the camera, lit by the sun of the moment (VW follows the camera
        // the same way).
        s.Clouds.transform.position = camPos;
        Color cloudLight = night ? new Color(0.01f, 0.012f, 0.02f) : sunLin * 0.55f + sky;
        Shader.SetGlobalVector("_PreviewCloudLight", new Vector4(cloudLight.r, cloudLight.g, cloudLight.b, 0f));
        Shader.SetGlobalFloat("_PreviewOvercast", _overcast);
        Shader.SetGlobalFloat("_PreviewLitShare", _litShare);
        // The game's clock and its buildings' lights level, as SimulationManager.Update and
        // LightSystem.SetDaylightIntensity set them (Interiors.cginc reads both).
        float simTime = ParseFloat(Env("RG_SIMTIME", "0"));
        Shader.SetGlobalVector("_SimulationTime2", new Vector4(simTime, simTime / 3600f, simTime * 2f * Mathf.PI,
                                                               simTime * 2f * Mathf.PI / 3600f));
        Shader.SetGlobalFloat("_GiantMarshmallow", ParseFloat(Env("RG_NIGHTLEVEL", night ? "-3" : "6")));
        Shader.SetGlobalFloat("_PreviewGameLights", GameLights ? 1f : 0f);

        // No game map here: its stand-in is the live sky WITHOUT clouds (the game's map is a
        // static picture); the live sky proper carries them.
        s.SkyCube.Update(true, 128, 6, null, camPos, p.SkySource == 0 && p.SkyClouds, true, s.Util, GlassUniforms.Dusk(toSun.y, p.SkyDusk));
        f.CloudAlpha = s.SkyCube.CloudAlpha;
        f.CubeMips = s.SkyCube.Mips;
        s.City.Bind(p.Neighbours);
        GlassQuality q = GlassQuality.ForTier(_quality);
        object boxedQ = q;
        foreach (KeyValuePair<string, int> o in _budgetOverrides)
        {
            FieldInfo qf = typeof(GlassQuality).GetField(o.Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (qf == null)
                throw new Exception("GlassQuality has no field '" + o.Key + "'");
            qf.SetValue(boxedQ, o.Value);
        }
        q = (GlassQuality)boxedQ;
        GlassUniforms.Apply(p, f, q);
        Interiors.Apply(p, f, s.Rooms, p.Interiors && s.Rooms.Loaded);
        bool screenOk = false;

        Color[] pixels;
        if (Seq != null)
        {
            float exposure = ParseFloat(Env("RG_EXPOSURE", "0.6")) * (night ? 6f : 1f);
            pixels = Sequence(s, p, q, drawGlass, exposure, out screenOk);
        }
        else
        {
            // Two renders: the first leaves the finished picture and the camera's matrices for the
            // second, which reads them as the mod's glass reads last frame's.
            for (int pass = 0; pass < 2; pass++)
            {
                screenOk = s.Screen.Update(s.Camera, s.Util, p.Finished, _hizOff ? 0 : q.HiZLevels);
                DrawCity(s, drawGlass);
                s.Camera.Render();
            }
            pixels = ReadPixels(s);
        }

        Color mean = Color.black;
        for (int i = 0; i < pixels.Length; i++)
            mean += pixels[i];
        mean /= pixels.Length;
        info = "mean " + Fmt(mean) + " | screen copy " + (screenOk ? s.Screen.Width + "x" + s.Screen.Height : "FAILED " + s.Screen.Problem) +
               " | sky cube " + (p.SkySource == 0 ? s.SkyCube.FacesDone + " faces, " + s.SkyCube.Mips + " mips" + (s.SkyCube.Problem.Length > 0 ? " " + s.SkyCube.Problem : "") : "game map") +
               " | stride " + GlassUniforms.Stride(p.Quality, s.Height).ToString("0", CultureInfo.InvariantCulture) + " x " +
               GlassUniforms.StepCount(p.Quality).ToString("0", CultureInfo.InvariantCulture) +
               " | budget " + q + " | hi-z " + s.Screen.HiZLevels + " levels | " + lodInfo +
               " | vehicles " + s.Vehicles.Count + ", glass draws " + s.VehicleDraws + (_vehSingle ? " (one by one)" : " (instanced)");
        return pixels;
    }

    // ---------------------------------------------------------------------------------------
    // The flicker harness (RG_SEQ; tools/preview/glass-flicker.ps1), the water's (WaterPreview's
    // Sequences) for the glass: the same jitter, resolve and metric, so the numbers compare.
    // ---------------------------------------------------------------------------------------

    /// <summary>The reflection pass's history weight as the mod ships it (a view's "Hist=w" changes it for that view).</summary>
    private static readonly float HistoryDefault = GlassPrePass.HistoryWeight;

    /// <summary>RG_SEQ "frames,warm,jitter,blending,sharpen,motionBlending,pan" (pan 0 still, 1 moving); null: the plain two renders.</summary>
    private static readonly float[] Seq = ParseSeq(Env("RG_SEQ", ""));

    /// <summary>The last sequence's flicker map (displayed colours) and its numbers, for RenderAll.</summary>
    private static Color[] _flickerMap;
    private static string _flickerStats = "";

    private static float[] ParseSeq(string text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        string[] parts = text.Split(',');
        var v = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            v[i] = float.Parse(parts[i].Trim(), CultureInfo.InvariantCulture);
        return v;
    }

    /// <summary>A linear colour as the PNGs show it (SavePng's curve), its luminance only.</summary>
    private static float Displayed(Color c, float exposure)
    {
        float white = 1f / Hable(11.2f);
        float l = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) * exposure * 2f;
        return Srgb(Hable(l) * white);
    }

    /// <summary>
    /// The camera of frame f of a pan: slid sideways along its right, ~0.3 px of parallax a frame at
    /// the distance it looks to (its pixel's size there), centred on the view's own camera.
    /// </summary>
    private static void PanCamera(Camera camera, Vector3 basePos, Quaternion baseRot, float pixelMetres, int f, int frames)
    {
        float k = f - (frames - 1) * 0.5f;
        camera.transform.position = basePos + baseRot * Vector3.right * (0.3f * pixelMetres * k);
        camera.transform.rotation = baseRot;
    }

    /// <summary>
    /// The glass pixels: a frame with the glass in its mask view (Glass.shader's _RGMask.z: magenta
    /// over what counts as glass) against the same frame without glass.
    /// </summary>
    private static bool[] GlassPixels(Scene s, GlassParams p, GlassQuality q)
    {
        s.Screen.Update(s.Camera, s.Util, p.Finished, _hizOff ? 0 : q.HiZLevels);
        DrawCity(s, false);
        s.Camera.Render();
        Color[] bare = ReadPixels(s);
        Vector4 mask = Shader.GetGlobalVector("_RGMask");
        Shader.SetGlobalVector("_RGMask", new Vector4(mask.x, mask.y, 1f, mask.w));
        s.Screen.Update(s.Camera, s.Util, p.Finished, _hizOff ? 0 : q.HiZLevels);
        DrawCity(s, true);
        s.Camera.Render();
        Color[] magenta = ReadPixels(s);
        Shader.SetGlobalVector("_RGMask", mask);
        var glass = new bool[bare.Length];
        for (int i = 0; i < bare.Length; i++)
            glass[i] = (magenta[i].r - magenta[i].g) - (bare[i].r - bare[i].g) > 0.1f;
        return glass;
    }

    private static Color[] Sequence(Scene s, GlassParams p, GlassQuality q, bool drawGlass, float exposure, out bool screenOk)
    {
        int frames = (int)Seq[0], warm = (int)Seq[1];
        float spread = Seq[2], blend = Seq[3], sharpen = Seq[4], motionBlend = Seq.Length > 5 ? Seq[5] : 0.85f;
        bool pan = Seq.Length > 6 && Seq[6] > 0.5f;
        int width = s.Width, height = s.Height, n = width * height;
        Camera camera = s.Camera;
        Vector3 basePos = camera.transform.position;
        Quaternion baseRot = camera.transform.rotation;
        float lookDistance = Mathf.Max(50f, Vector3.Distance(basePos, new Vector3(0f, 50f, 0f)));
        float pixelMetres = 2f * lookDistance * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / height;

        // What counts: glass at the first measured frame and at the last (a pan moves it).
        if (pan) PanCamera(camera, basePos, baseRot, pixelMetres, warm, frames);
        bool[] mask = GlassPixels(s, p, q);
        if (pan)
        {
            PanCamera(camera, basePos, baseRot, pixelMetres, frames - 1, frames);
            bool[] end = GlassPixels(s, p, q);
            for (int i = 0; i < n; i++)
                mask[i] = mask[i] && end[i];
        }

        var resolve = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/Editor/PreviewTAA.shader"));
        RenderTexture resolved = WaterPreview.Target(width, height, false), history = WaterPreview.Target(width, height, false);
        var lum = new float[3][];
        var raw = new float[3][];
        for (int k = 0; k < 3; k++)
        {
            lum[k] = new float[n];
            raw[k] = new float[n];
        }
        var flicker = new double[n];
        var flickerRaw = new double[n];
        int measured = 0;
        Matrix4x4 prevVP = Matrix4x4.identity;
        screenOk = false;

        for (int f = 0; f < frames; f++)
        {
            if (pan) PanCamera(camera, basePos, baseRot, pixelMetres, f, frames);
            int i = f % 8;
            float jx = (WaterPreview.Halton(i + 1, 2) - 0.5f) * spread, jy = (WaterPreview.Halton(i + 1, 3) - 0.5f) * spread;
            camera.ResetProjectionMatrix();
            Matrix4x4 plain = camera.projectionMatrix;
            Matrix4x4 projection = plain;
            projection.m02 += 2f * jx / width;
            projection.m12 += 2f * jy / height;
            camera.projectionMatrix = projection;

            screenOk = s.Screen.Update(camera, s.Util, p.Finished, _hizOff ? 0 : q.HiZLevels);
            DrawCity(s, drawGlass);
            camera.Render();

            resolve.SetTexture("_HistoryTex", history);
            resolve.SetVector("_Jitter", new Vector4(jx / width, jy / height, 0f, 0f));
            resolve.SetVector("_TAA", new Vector4(sharpen, blend, f == 0 ? 1f : 0f, motionBlend));
            if (pan)
            {
                resolve.SetMatrix("_CurInvP", plain.inverse);
                resolve.SetMatrix("_CurCamToWorld", camera.cameraToWorldMatrix);
                resolve.SetMatrix("_PrevVP", f == 0 ? plain * camera.worldToCameraMatrix : prevVP);
            }
            Graphics.Blit(s.Target, resolved, resolve, pan ? 1 : 0);
            Graphics.Blit(resolved, history);
            prevVP = plain * camera.worldToCameraMatrix;

            if (f >= warm - 2)
            {
                Color[] r = ReadRT(s, resolved);
                Color[] a = ReadPixels(s);
                float[] lr = lum[f % 3], ra = raw[f % 3];
                for (int px = 0; px < n; px++)
                {
                    lr[px] = Displayed(r[px], exposure);
                    ra[px] = Displayed(a[px], exposure);
                }
                if (f >= warm)
                {
                    float[] la = lum[f % 3], lb = lum[(f + 2) % 3], lc = lum[(f + 1) % 3];
                    float[] rb = raw[(f + 2) % 3], rc = raw[(f + 1) % 3];
                    for (int px = 0; px < n; px++)
                    {
                        flicker[px] += Math.Abs(la[px] - 2f * lb[px] + lc[px]);
                        flickerRaw[px] += Math.Abs(ra[px] - 2f * rb[px] + rc[px]);
                    }
                    measured++;
                }
            }
        }
        camera.ResetProjectionMatrix();
        camera.transform.position = basePos;
        camera.transform.rotation = baseRot;

        int glass = 0, over2 = 0, over8 = 0, rawOver2 = 0, rawOver8 = 0;
        double sum = 0.0, rawSum = 0.0;
        for (int px = 0; px < n; px++)
        {
            if (!mask[px])
                continue;
            glass++;
            double v = flicker[px] / Math.Max(1, measured), rv = flickerRaw[px] / Math.Max(1, measured);
            sum += v;
            rawSum += rv;
            if (v > 2.0 / 255.0) over2++;
            if (v > 8.0 / 255.0) over8++;
            if (rv > 2.0 / 255.0) rawOver2++;
            if (rv > 8.0 / 255.0) rawOver8++;
        }
        double w = Math.Max(1, glass);
        _flickerStats = string.Format(CultureInfo.InvariantCulture,
            "glass {0:F1}% | TAA mean {1:F2}/255 >2: {2:F2}% >8: {3:F2}% | raw mean {4:F2}/255 >2: {5:F2}% >8: {6:F2}%",
            100.0 * glass / n, 255.0 * sum / w, 100.0 * over2 / w, 100.0 * over8 / w, 255.0 * rawSum / w, 100.0 * rawOver2 / w, 100.0 * rawOver8 / w);

        Color[] shown = ReadRT(s, resolved);
        _flickerMap = new Color[n];
        for (int px = 0; px < n; px++)
        {
            float v = (float)(flicker[px] / Math.Max(1, measured));
            float d = Displayed(shown[px], exposure);
            _flickerMap[px] = !mask[px] ? new Color(d * 0.33f, d * 0.33f, d * 0.33f, 1f)
                : v > 8f / 255f ? new Color(1f, 0.15f, 0.1f, 1f) : new Color(Mathf.Min(1f, 25f * v), Mathf.Min(1f, 25f * v), Mathf.Min(1f, 25f * v), 1f);
        }
        resolved.Release();
        history.Release();
        return shown;
    }

    private static Color[] ReadRT(Scene s, RenderTexture rt)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = rt;
        s.Readback.ReadPixels(new Rect(0, 0, s.Width, s.Height), 0, 0, false);
        s.Readback.Apply(false);
        RenderTexture.active = previous;
        return s.Readback.GetPixels();
    }

    /// <summary>The game's draws, then ours, for the camera's next render.</summary>
    private static void DrawCity(Scene s, bool drawGlass)
    {
        s.Block.Clear();
        s.Block.SetTexture("_MainTex", s.GroundMain);
        s.Block.SetTexture("_XYSMap", s.GroundXys);
        s.Block.SetTexture("_ACIMap", s.GroundAci);
        Graphics.DrawMesh(s.Ground, Matrix4x4.identity, s.StandIn, 0, s.Camera, 0, s.Block, true, true);
        // The game's own unused LOD slots: 100 km behind the camera (Building.RenderLod).
        Matrix4x4 away = Matrix4x4.TRS(s.Camera.transform.forward * -100000f, Quaternion.identity, Vector3.one);
        var locations = new Matrix4x4[8];
        var states = new Vector4[8];
        var indices = new Vector4[8];
        foreach (Building b in s.Buildings)
        {
            s.Block.Clear();
            s.Block.SetTexture("_MainTex", _mark > 0 && b.Framed == _mark ? b.MainMarked : b.Main);
            s.Block.SetTexture("_XYSMap", b.Xys);
            s.Block.SetTexture("_ACIMap", b.Aci);
            s.Block.SetVector("_ObjectIndex", b.ObjectIndex);
            Graphics.DrawMesh(b.Mesh, b.Matrix, s.StandIn, 0, s.Camera, 0, s.Block, true, true);
            if (!drawGlass || _bldOff)
                continue;

            if (_tier == "lod")
            {
                // The building in slot 5 of its combined mesh: tests the index (color.a x 8),
                // the location and state arrays and the atlas rectangle.
                for (int k = 0; k < 8; k++)
                {
                    locations[k] = away;
                    states[k] = Vector4.zero;
                    indices[k] = Vector4.zero;
                }
                locations[5] = b.Matrix;
                states[5] = new Vector4(0f, 100000f, 0f, 1f);
                indices[5] = b.ObjectIndex;
                var lodBlock = new MaterialPropertyBlock();
                lodBlock.SetMatrixArray("_BuildingLocations", locations);
                lodBlock.SetVectorArray("_BuildingStates", states);
                lodBlock.SetVectorArray("_ObjectIndices", indices);
                Vector3 c = b.Matrix.MultiplyPoint3x4(Vector3.zero);
                Mesh lodMesh = _lod == "turned" ? b.Combined8Turned : b.Combined8;
                lodMesh.bounds = new Bounds(c + Vector3.up * 150f, new Vector3(400f, 400f, 400f));
                Graphics.DrawMesh(lodMesh, Matrix4x4.identity, s.GlassLod, 0, s.Camera, 0, lodBlock, false, false);
                s.PrePass.Record(lodMesh, Matrix4x4.identity, GlassPrePass.TierLod, lodBlock, 0);
            }
            else if (_tier == "near")
            {
                s.Block.SetVector("_BuildingState", new Vector4(0f, 100000f, 0f, 1f));
                Graphics.DrawMesh(b.Mesh, b.Matrix, s.Glass, 0, s.Camera, 0, s.Block, false, false);
                s.PrePass.Record(b.Mesh, b.Matrix, GlassPrePass.TierNear, s.Block, 0);
            }
        }

        if (drawGlass && !_bldOff && _tier == "group")
        {
            Mesh baked = _lod == "turned" ? s.BakedTurned : s.Baked;
            Graphics.DrawMesh(baked, Matrix4x4.identity, s.GlassGroup, 0, s.Camera, 0, null, false, false);
            s.PrePass.Record(baked, Matrix4x4.identity, GlassPrePass.TierGroup, null, 0);
        }

        DrawVehicles(s, drawGlass && !_vehOff);
        DrawProps(s, drawGlass && !_propOff);
        DrawDecals(s, drawGlass);
    }

    /// <summary>
    /// The props: the game's draw of each (the Rotors stand-in, see-through), then -- glass -- ours, as
    /// PropGlassDraw draws a see-through prop: instanced per model, after the transparent queue, its
    /// cover rule 2 (all but the near-opaque parts), the surface and reflections in the block.
    /// </summary>
    private static void DrawProps(Scene s, bool glass)
    {
        s.PropDraws = 0;
        if (s.Props.Count == 0)
            return;
        Texture cube = Shader.GetGlobalTexture("_RGSky");
        foreach (Prop p in s.Props)
        {
            s.PropBlock.Clear();
            s.PropBlock.SetTexture("_MainTex", p.Asset.Main);
            s.PropBlock.SetTexture("_XYSMap", p.Asset.Xys);
            if (cube != null)
                s.PropBlock.SetTexture("_Cube", cube);
            Graphics.DrawMesh(p.Asset.Mesh, p.Matrix, s.PropStandIn, 0, s.Camera, 0, s.PropBlock, false, false);
        }
        if (!glass)
            return;

        s.PropGlass.SetTexture("_RGPropRipple", s.PropRipple);
        foreach (Prop p in s.Props)
        {
            var block = new MaterialPropertyBlock();
            block.SetTexture("_XYSMap", p.Asset.Xys);
            block.SetVector("_RGPropST", new Vector4(1f, 1f, 0f, 0f));
            int surface = _propSurface != 0 ? _propSurface : p.Surface;
            SetPropLook(block, surface, _propShine);
            Graphics.DrawMeshInstanced(p.Asset.Mesh, 0, s.PropGlass, new[] { p.Matrix }, 1, block, ShadowCastingMode.Off, false, 0, s.Camera);
            s.PropDraws++;
        }
    }

    /// <summary>
    /// The vehicles: the game's draw of each (the stand-in, its paint and floor), then -- glass -- ours,
    /// as the mod will draw it: every copy of one model in ONE Graphics.DrawMeshInstanced with the
    /// model's maps in a block ("Inst=0": one DrawMesh each, the fallback where instancing is missing).
    /// </summary>
    private static void DrawVehicles(Scene s, bool glass)
    {
        s.VehicleDraws = 0;
        if (s.Vehicles.Count == 0)
            return;
        foreach (Vehicle v in s.Vehicles)
        {
            s.VehicleBlock.Clear();
            s.VehicleBlock.SetTexture("_MainTex", v.Asset.Main);
            s.VehicleBlock.SetTexture("_XYSMap", v.Asset.Xys);
            s.VehicleBlock.SetTexture("_ACIMap", v.Asset.Aci);
            s.VehicleBlock.SetVector("_FloorParams", v.Asset.Floor);
            s.VehicleBlock.SetColor("_Color", v.Paint.linear);
            s.VehicleBlock.SetVector("_LightState", _night ? v.NightLights : v.DayLights);
            Graphics.DrawMesh(v.Asset.Mesh, v.Matrix, s.VehicleStandIn, 0, s.Camera, 0, s.VehicleBlock, true, true);
        }
        if (!glass)
            return;

        foreach (PreviewVehicleAsset a in s.VehicleAssets)
        {
            var matrices = new List<Matrix4x4>();
            var seeds = new List<float>();
            foreach (Vehicle v in s.Vehicles)
            {
                if (v.Asset != a)
                    continue;
                matrices.Add(v.Matrix);
                seeds.Add(v.Seed);
            }
            if (matrices.Count == 0)
                continue;
            if (_vehSingle || !SystemInfo.supportsInstancing)
            {
                for (int k = 0; k < matrices.Count; k++)
                {
                    var block = VehicleGlassBlock(a);
                    block.SetFloat("_RGVehicleSeed", seeds[k]);
                    Graphics.DrawMesh(a.Mesh, matrices[k], s.VehicleGlass, 0, s.Camera, 0, block, false, false);
                    s.VehicleDraws++;
                }
            }
            else
            {
                // One number per instance: the block's array, read through UNITY_ACCESS_INSTANCED_PROP.
                var block = VehicleGlassBlock(a);
                block.SetFloatArray("_RGVehicleSeed", seeds);
                Graphics.DrawMeshInstanced(a.Mesh, 0, s.VehicleGlass, matrices.ToArray(), matrices.Count, block, ShadowCastingMode.Off, false, 0, s.Camera);
                s.VehicleDraws++;
            }
        }
    }

    /// <summary>A model's maps for its glass draw (the game's vehicle shader: Default).</summary>
    private static MaterialPropertyBlock VehicleGlassBlock(PreviewVehicleAsset a)
    {
        var block = new MaterialPropertyBlock();
        block.SetTexture("_MainTex", a.Main);
        block.SetTexture("_XYSMap", a.Xys);
        block.SetTexture("_ACIMap", a.Aci);
        block.SetVector("_FloorParams", a.Floor);
        block.SetFloat("_RGVehicleShader", 0f);
        block.SetVector("_RGVehicleST", new Vector4(1f, 1f, 0f, 0f));
        return block;
    }

    /// <summary>"glass:Trace=0;Reflectivity=0.3" -> the defaults with those GlassParams fields set.</summary>
    private static GlassParams Override(GlassParams p, string variant)
    {
        int colon = variant.IndexOf(':');
        if (colon < 0)
            return p;
        object boxed = p;
        foreach (string pair in variant.Substring(colon + 1).Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = pair.Split('=');
            if (kv.Length != 2)
                continue;
            string vkey = kv[0].Trim().ToLowerInvariant();
            if (vkey == "decal")
            {
                _decalSurface = int.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (vkey == "prop" || vkey.StartsWith("prop."))
            {
                string text = kv[1].Trim();
                switch (vkey)
                {
                    case "prop": _propOff = text == "0"; break;
                    case "prop.refl": _propRefl = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.clarity": _propClarity = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.shine": _propShine = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.surface": _propSurface = int.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.polish": _propPolish = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.glint": _propGlint = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.soft": _propSoft = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.strength": _propWaterStrength = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.ripple": _propRipple.x = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.ripplesize": _propRipple.y = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.ripplespeed": _propRipple.z = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "prop.color":
                        Color c;
                        if (!ColorUtility.TryParseHtmlString(text, out c))
                            throw new Exception("prop.color '" + text + "' is no #RRGGBB");
                        _propColour = c;
                        _propHasColour = true;
                        break;
                    default: throw new Exception("no prop key '" + kv[0] + "'");
                }
                continue;
            }
            if (vkey == "veh" || vkey.StartsWith("veh.") || vkey == "inst" || vkey == "bld")
            {
                string text = kv[1].Trim();
                switch (vkey)
                {
                    case "veh": _vehOff = text == "0"; break;
                    case "inst": _vehSingle = text == "0"; break;
                    case "bld": _bldOff = text == "0"; break;
                    case "veh.tinted": _vehTinted = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.strength": _vehStrength = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.refl": _vehRefl = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.glint": _vehGlint = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.edge": _vehEdge = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.rough": _vehRough = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.lenses": _vehLenses = text != "0"; break;
                    case "veh.coat": _vehCoat = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.coatrough": _vehCoatRough = float.Parse(text, CultureInfo.InvariantCulture); break;
                    case "veh.liveries": _vehLiveries = text != "0"; break;
                    case "veh.cabin": _vehCabin = float.Parse(text, CultureInfo.InvariantCulture); break;
                    default: throw new Exception("no vehicle key '" + kv[0] + "'");
                }
                continue;
            }
            if (kv[0].Trim().Equals("Debug", StringComparison.OrdinalIgnoreCase))
            {
                // Not a setting: the shader's check views (GlassCommon.cginc, _RGDebug).
                _debug = float.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("Level", StringComparison.OrdinalIgnoreCase))
            {
                _debugLevel = float.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("Ofs", StringComparison.OrdinalIgnoreCase))
            {
                // Not a setting: "Ofs=factor,units", the glass pass's depth Offset (-1,-1).
                string[] fu = kv[1].Split(',');
                _offsetFactor = float.Parse(fu[0].Trim(), CultureInfo.InvariantCulture);
                _offsetUnits = float.Parse(fu[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("Mark", StringComparison.OrdinalIgnoreCase))
            {
                _mark = int.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("Hist", StringComparison.OrdinalIgnoreCase))
            {
                // Not a setting: "Hist=w", the reflection pass's history weight (GlassPrePass.HistoryWeight; 0 none).
                GlassPrePass.HistoryWeight = float.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("HiZ", StringComparison.OrdinalIgnoreCase))
            {
                _hizOff = kv[1].Trim() == "0";
                continue;
            }
            if (kv[0].Trim().Equals("Fit", StringComparison.OrdinalIgnoreCase))
            {
                // Not a setting: "Fit=0" lays the rooms out on the storey grid, as before RoomFit.
                _fitOff = kv[1].Trim() == "0";
                continue;
            }
            if (kv[0].Trim().Equals("Guards", StringComparison.OrdinalIgnoreCase))
            {
                // Not a setting: "Guards=0" draws the rooms as before RoomShape (the shader's _RGDebug.z).
                _guardsOff = kv[1].Trim() == "0";
                continue;
            }
            if (kv[0].Trim().StartsWith("Q.", StringComparison.OrdinalIgnoreCase))
            {
                _budgetOverrides.Add(new KeyValuePair<string, int>(kv[0].Trim().Substring(2), int.Parse(kv[1].Trim(), CultureInfo.InvariantCulture)));
                continue;
            }
            if (kv[0].Trim().Equals("Tier", StringComparison.OrdinalIgnoreCase))
            {
                _tier = kv[1].Trim().ToLowerInvariant();
                continue;
            }
            if (kv[0].Trim().Equals("Lod", StringComparison.OrdinalIgnoreCase))
            {
                _lod = kv[1].Trim().ToLowerInvariant();
                if (_lod != "own" && _lod != "dull" && _lod != "turned" && _lod != "wrong")
                    throw new Exception("Lod=own|dull|turned|wrong, not '" + kv[1] + "'");
                continue;
            }
            if (kv[0].Trim().Equals("LodMode", StringComparison.OrdinalIgnoreCase))
            {
                string mode = kv[1].Trim().ToLowerInvariant();
                _lodMode = mode == "model" ? LodGlass.ModeModel : (mode == "own" ? LodGlass.ModeOwn : LodGlass.ModeAuto);
                continue;
            }
            if (kv[0].Trim().Equals("Overcast", StringComparison.OrdinalIgnoreCase))
            {
                _overcast = float.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("Lit", StringComparison.OrdinalIgnoreCase))
            {
                _litShare = float.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("Hour", StringComparison.OrdinalIgnoreCase))
            {
                _hour = float.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().Equals("Budget", StringComparison.OrdinalIgnoreCase))
            {
                // Not a setting: the GlassQuality tier (GlassParams.Quality is the screen trace's).
                _quality = int.Parse(kv[1].Trim(), CultureInfo.InvariantCulture);
                continue;
            }
            if (kv[0].Trim().StartsWith("A.", StringComparison.OrdinalIgnoreCase))
            {
                // The full-glass tower's own settings (its asset's): A.Strength, A.Detail,
                // A.Reflectivity, A.Clarity, A.Own, A.Threshold (0..1), A.Night (0/1), A.Tint (#RRGGBB),
                // A.Curtains (0/1: its rooms' curtains, with Interiors=1), A.IllumMap (0/1: its rooms lit
                // by its illumination map, with Interiors=1).
                string key = kv[0].Trim().Substring(2).ToLowerInvariant();
                string text = kv[1].Trim();
                if (key == "curtains")
                {
                    _asset.Curtains = text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (key == "illummap")
                {
                    _asset.IllumMap = text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (key == "tint")
                {
                    Color tint;
                    if (!ColorUtility.TryParseHtmlString(text, out tint))
                        throw new Exception("A.Tint: not a colour: " + text);
                    _asset.Tint = tint;
                    continue;
                }
                if (key == "night")
                {
                    _asset.Night = text == "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                float number = float.Parse(text, CultureInfo.InvariantCulture);
                switch (key)
                {
                    case "strength": _asset.Strength = number; break;
                    case "detail": _asset.Detail = number; break;
                    case "reflectivity": _asset.Reflectivity = number; break;
                    case "clarity": _asset.Clarity = number; break;
                    case "own": _asset.Own = number; break;
                    case "threshold": _asset.Threshold = number; break;
                    default: throw new Exception("no asset setting 'A." + key + "'");
                }
                continue;
            }
            FieldInfo field = typeof(GlassParams).GetField(kv[0].Trim(), BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field == null)
                throw new Exception("GlassParams has no field '" + kv[0] + "'");
            string value = kv[1].Trim();
            if (field.FieldType == typeof(bool))
                field.SetValue(boxed, value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));
            else if (field.FieldType == typeof(int))
                field.SetValue(boxed, int.Parse(value, CultureInfo.InvariantCulture));
            else
                field.SetValue(boxed, float.Parse(value, CultureInfo.InvariantCulture));
        }
        return (GlassParams)boxed;
    }

    private static Color[] ReadPixels(Scene s)
    {
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = s.Target;
        s.Readback.ReadPixels(new Rect(0, 0, s.Width, s.Height), 0, 0, false);
        s.Readback.Apply(false);
        RenderTexture.active = previous;
        return s.Readback.GetPixels();
    }

    /// <summary>Filmic (Hable 2010, the Uncharted 2 curve) over the exposure, then sRGB: a picture for the eye.</summary>
    private static void SavePng(Color[] pixels, int width, int height, float exposure, string file)
    {
        var png = new Texture2D(width, height, TextureFormat.RGB24, false, false);
        var output = new Color[pixels.Length];
        float white = 1f / Hable(11.2f);
        for (int i = 0; i < pixels.Length; i++)
        {
            Color p = pixels[i] * exposure * 2f;
            output[i] = new Color(Srgb(Hable(p.r) * white), Srgb(Hable(p.g) * white), Srgb(Hable(p.b) * white), 1f);
        }

        png.SetPixels(output);
        png.Apply(false);
        File.WriteAllBytes(file, png.EncodeToPNG());
        UnityEngine.Object.DestroyImmediate(png);
    }

    private static float Hable(float x)
    {
        const float A = 0.15f, B = 0.50f, C = 0.10f, D = 0.20f, E = 0.02f, F = 0.30f;
        return ((x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F)) - E / F;
    }

    private static float Srgb(float c)
    {
        c = Mathf.Clamp01(c);
        return c <= 0.0031308f ? 12.92f * c : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
    }

    private static string Fmt(Color c)
    {
        return "(" + c.r.ToString("F3", CultureInfo.InvariantCulture) + " " + c.g.ToString("F3", CultureInfo.InvariantCulture) + " " +
               c.b.ToString("F3", CultureInfo.InvariantCulture) + ")";
    }

    private static string Env(string name, string fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? fallback : value;
    }

    private static float ParseFloat(string text)
    {
        return float.Parse(text, CultureInfo.InvariantCulture);
    }
}
