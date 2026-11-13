using System;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace RealGlass.Glass
{
    /// <summary>
    /// What Real Glass reads of Procedural Objects (Workshop 1094334744; IL of 1.7.8, 2026-10-21),
    /// by name, with no reference to its DLL and nothing of it patched or changed: its list of
    /// objects, and of each object its kind, asset, mesh, material, place and id, and whether
    /// Procedural Objects draws it this frame -- the three tests its ProceduralObjectsLogic.Update
    /// makes before its Graphics.DrawMesh: the object inside its render distance
    /// (_insideRenderView, which that Update has just set), its layer not hidden, and
    /// RenderOptions.CanRenderSingle(object, night) (its modules' say, its day / night visibility).
    /// </summary>
    /// <remarks>
    /// Each field is read through a small method emitted once (DynamicMethod: a cast and a field
    /// load), not FieldInfo.GetValue: a few hundred buildings are read every frame, and GetValue
    /// boxes every value. Anything missing or of another type (a later Procedural Objects) and
    /// <see cref="Create"/> gives the problem instead.
    /// </remarks>
    public sealed class ProceduralAccess
    {
        public const string LogicTypeName = "ProceduralObjects.ProceduralObjectsLogic";
        public const string OptionsTypeName = "ProceduralObjects.Classes.RenderOptions";
        public const string ModTypeName = "ProceduralObjects.ProceduralObjectsMod";

        /// <summary>An object's baseInfoType when it is a building.</summary>
        public const string BuildingKind = "BUILDING";

        /// <summary>An object's baseInfoType when it is a prop (decals and its surfaces among them).</summary>
        public const string PropKind = "PROP";

        private const BindingFlags InstanceFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags StaticFields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private FieldInfo _logic, _options;

        /// <summary>ProceduralObjectsMod.VERSION, or null.</summary>
        public string Version { get; private set; }

        /// <summary>ProceduralObjectsLogic.proceduralObjects (a List of its objects) of the logic.</summary>
        public Func<object, IList> Objects { get; private set; }

        /// <summary>baseInfoType: <see cref="BuildingKind"/> or <see cref="PropKind"/>.</summary>
        public Func<object, string> Kind { get; private set; }

        /// <summary>_baseBuilding: the building asset (null for a prop, or when the asset is missing).</summary>
        public Func<object, BuildingInfo> Asset { get; private set; }

        /// <summary>
        /// _baseProp: the prop asset (null for a building, or when the asset is missing); null itself
        /// when this Procedural Objects has no such field (its props then keep the game's look, its
        /// buildings get the glass all the same: <see cref="PropsProblem"/>).
        /// </summary>
        public Func<object, PropInfo> PropAsset { get; private set; }

        /// <summary>isPloppableAsphalt: one of its ground surfaces, drawn from a prop but no prop's look.</summary>
        public Func<object, bool> Surface { get; private set; }

        /// <summary>Why <see cref="PropAsset"/> is null; null when the props were read.</summary>
        public string PropsProblem { get; private set; }

        /// <summary>m_mesh: its own copy of the asset's main mesh, reshaped in place by its tools.</summary>
        public Func<object, Mesh> Mesh { get; private set; }

        /// <summary>m_material: its own copy of the asset's material.</summary>
        public Func<object, Material> Material { get; private set; }

        public Func<object, Vector3> Position { get; private set; }

        public Func<object, Quaternion> Rotation { get; private set; }

        /// <summary>id: its number in Procedural Objects, unique among its objects.</summary>
        public Func<object, int> Id { get; private set; }

        /// <summary>(object, RenderOptions.instance, night): Procedural Objects draws it this frame.</summary>
        public Func<object, object, bool, bool> Drawn { get; private set; }

        /// <summary>ProceduralObjectsLogic.instance: null outside a city, or with the mod off.</summary>
        public object Logic()
        {
            return _logic.GetValue(null);
        }

        /// <summary>RenderOptions.instance: what <see cref="Drawn"/> asks.</summary>
        public object Options()
        {
            return _options.GetValue(null);
        }

        /// <summary>Procedural Objects' logic type among the loaded assemblies; null when it is not installed.</summary>
        public static Type FindLogicType()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(LogicTypeName, false);
                    if (type != null)
                        return type;
                }
                catch (Exception)
                {
                    // an assembly that cannot list its types is not Procedural Objects
                }
            }

            return null;
        }

        /// <summary>Everything read of it, or null and the problem.</summary>
        public static ProceduralAccess Create(Type logicType, out string problem)
        {
            problem = null;
            try
            {
                Assembly assembly = logicType.Assembly;
                var a = new ProceduralAccess();
                a._logic = Field(logicType, "instance", StaticFields);
                if (a._logic.FieldType != logicType)
                    throw new InvalidOperationException(logicType.Name + ".instance is a " + a._logic.FieldType.Name);

                FieldInfo list = Field(logicType, "proceduralObjects", InstanceFields);
                if (!list.FieldType.IsGenericType || list.FieldType.GetGenericArguments().Length != 1)
                    throw new InvalidOperationException("proceduralObjects is a " + list.FieldType.Name);
                Type po = list.FieldType.GetGenericArguments()[0];

                Type options = assembly.GetType(OptionsTypeName, false);
                if (options == null)
                    throw new InvalidOperationException("no " + OptionsTypeName);
                a._options = Field(options, "instance", StaticFields);

                a.Objects = Getter<IList>(logicType, "proceduralObjects");
                a.Kind = Getter<string>(po, "baseInfoType");
                a.Asset = Getter<BuildingInfo>(po, "_baseBuilding");
                a.Mesh = Getter<Mesh>(po, "m_mesh");
                a.Material = Getter<Material>(po, "m_material");
                a.Position = Getter<Vector3>(po, "m_position");
                a.Rotation = Getter<Quaternion>(po, "m_rotation");
                a.Id = Getter<int>(po, "id");
                a.Drawn = DrawnTest(po, options);

                // The props (2.0.0) on their own: a Procedural Objects without these fields keeps its
                // buildings' glass.
                try
                {
                    a.PropAsset = Getter<PropInfo>(po, "_baseProp");
                    a.Surface = Getter<bool>(po, "isPloppableAsphalt");
                }
                catch (Exception e)
                {
                    a.PropAsset = null;
                    a.Surface = null;
                    a.PropsProblem = e.GetType().Name + ": " + e.Message;
                }

                Type mod = assembly.GetType(ModTypeName, false);
                FieldInfo version = mod != null ? mod.GetField("VERSION", StaticFields) : null;
                a.Version = version != null && version.FieldType == typeof(string) ? (string)version.GetValue(null) : null;
                return a;
            }
            catch (Exception e)
            {
                problem = e.GetType().Name + ": " + e.Message;
                return null;
            }
        }

        private static FieldInfo Field(Type type, string name, BindingFlags flags)
        {
            FieldInfo f = type.GetField(name, flags);
            if (f == null)
                throw new MissingFieldException(type.FullName, name);
            return f;
        }

        /// <summary>object -> (cast to type).name, as a T (the field's own type, or a reference type it is one of).</summary>
        private static Func<object, T> Getter<T>(Type type, string name)
        {
            FieldInfo f = Field(type, name, InstanceFields);
            bool fits = typeof(T).IsValueType ? f.FieldType == typeof(T) : !f.FieldType.IsValueType && typeof(T).IsAssignableFrom(f.FieldType);
            if (!fits)
                throw new InvalidOperationException(type.Name + "." + name + " is a " + f.FieldType.Name + ", not a " + typeof(T).Name);

            var method = new DynamicMethod("RealGlass_" + type.Name + "_" + name, typeof(T), new[] { typeof(object) }, typeof(ProceduralAccess), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, type);
            il.Emit(OpCodes.Ldfld, f);
            il.Emit(OpCodes.Ret);
            return (Func<object, T>)method.CreateDelegate(typeof(Func<object, T>));
        }

        /// <summary>
        /// Procedural Objects' own three tests, in its order (ProceduralObjectsLogic.Update, IL 1.7.8):
        /// <code>
        /// if (!o._insideRenderView) return false;
        /// if (o.layer != null &amp;&amp; o.layer.m_isHidden) return false;
        /// return options.CanRenderSingle(o, night);
        /// </code>
        /// CanRenderSingle asks the object's modules (Procedural Objects' own module answers yes) and
        /// its day / night visibility; it is called once more a frame, by us, as by Procedural Objects.
        /// </summary>
        private static Func<object, object, bool, bool> DrawnTest(Type po, Type options)
        {
            FieldInfo inside = Field(po, "_insideRenderView", InstanceFields);
            FieldInfo layer = Field(po, "layer", InstanceFields);
            if (inside.FieldType != typeof(bool) || layer.FieldType.IsValueType)
                throw new InvalidOperationException("_insideRenderView or layer is not what it was");
            FieldInfo hidden = Field(layer.FieldType, "m_isHidden", InstanceFields);
            if (hidden.FieldType != typeof(bool))
                throw new InvalidOperationException("m_isHidden is a " + hidden.FieldType.Name);
            MethodInfo can = options.GetMethod("CanRenderSingle", BindingFlags.Public | BindingFlags.Instance, null,
                                               new[] { po, typeof(bool) }, null);
            if (can == null || can.ReturnType != typeof(bool))
                throw new MissingMethodException(options.FullName, "CanRenderSingle");

            var method = new DynamicMethod("RealGlass_ProceduralDrawn", typeof(bool), new[] { typeof(object), typeof(object), typeof(bool) },
                                           typeof(ProceduralAccess), true);
            ILGenerator il = method.GetILGenerator();
            LocalBuilder o = il.DeclareLocal(po);
            LocalBuilder l = il.DeclareLocal(layer.FieldType);
            Label no = il.DefineLabel();
            Label ask = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, po);
            il.Emit(OpCodes.Stloc, o);
            il.Emit(OpCodes.Ldloc, o);
            il.Emit(OpCodes.Ldfld, inside);
            il.Emit(OpCodes.Brfalse, no);

            il.Emit(OpCodes.Ldloc, o);
            il.Emit(OpCodes.Ldfld, layer);
            il.Emit(OpCodes.Stloc, l);
            il.Emit(OpCodes.Ldloc, l);
            il.Emit(OpCodes.Brfalse, ask);
            il.Emit(OpCodes.Ldloc, l);
            il.Emit(OpCodes.Ldfld, hidden);
            il.Emit(OpCodes.Brtrue, no);

            il.MarkLabel(ask);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, options);
            il.Emit(OpCodes.Ldloc, o);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Callvirt, can);
            il.Emit(OpCodes.Ret);

            il.MarkLabel(no);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Ret);
            return (Func<object, object, bool, bool>)method.CreateDelegate(typeof(Func<object, object, bool, bool>));
        }
    }
}
