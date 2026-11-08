using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Prints every error and warning Unity holds against the mod's shaders, and fails if there is
/// one or a shader has the wrong number of passes. A shader that does not compile still yields a
/// bundle and exit code 0 (the messages stay on the imported asset); this is the check that sees
/// it. build-bundle.ps1 runs it first:
///   Unity.exe -batchmode -nographics -quit -projectPath . -executeMethod ShaderCheck.Run
/// (RealFlag's check, with this mod's shaders.)
/// </summary>
public static class ShaderCheck
{
    /// <summary>Every shader the mod ships, and the passes each must have.</summary>
    private static readonly string[] Shaders =
    {
        "Assets/RealGlass/Glass.shader",
        "Assets/RealGlass/GlassUtil.shader",
        "Assets/RealGlass/WaterCube.shader",
        "Assets/RealGlass/CityReflection.shader",
        "Assets/RealGlass/WaterRipples.shader",
        "Assets/RealGlass/WaterFoam.shader",
        "Assets/RealGlass/WaterSurfaceMask.shader",
        "Assets/RealGlass/WaterSky.shader",
        "Assets/RealGlass/WaterWaves.shader",
        "Assets/RealGlass/VehicleGlass.shader",
        "Assets/RealGlass/WaterUnder.shader",
        "Assets/RealGlass/PropGlass.shader",
        "Assets/RealGlass/GlassPrePass.shader",
        "Assets/RealGlass/GlassReflection.shader",
    };

    private static readonly int[] Passes = { 1, 8, 1, 4, 1, 1, 3, 1, 3, 1, 3, 2, 1, 2 };

    public static void Run()
    {
        bool failed = false;
        int index = -1;

        // Internal in 5.6: ShaderUtil.GetShaderErrors(Shader) -> ShaderError[] { message,
        // messageDetails, platform, file, line, warning }.
        MethodInfo getErrors = typeof(ShaderUtil).GetMethod("GetShaderErrors", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        MethodInfo hasError = typeof(ShaderUtil).GetMethod("ShaderHasError", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

        foreach (string path in Shaders)
        {
            index++;
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (shader == null)
            {
                Debug.Log("SHADER CHECK: " + path + " NOT FOUND");
                failed = true;
                continue;
            }

            int passes = new Material(shader).passCount;
            bool error = hasError != null && (bool)hasError.Invoke(null, new object[] { shader });
            Debug.Log("SHADER CHECK: " + shader.name + " passes=" + passes + " (wants " + Passes[index] + ") hasError=" + error +
                      " supported=" + shader.isSupported);
            if (passes != Passes[index])
            {
                Debug.Log("SHADER CHECK ERROR: " + shader.name + " has " + passes + " pass(es), not " + Passes[index]);
                failed = true;
            }

            if (getErrors != null)
            {
                var errors = getErrors.Invoke(null, new object[] { shader }) as Array;
                if (errors != null)
                {
                    foreach (object e in errors)
                    {
                        Type t = e.GetType();
                        string message = Field(t, e, "message");
                        string details = Field(t, e, "messageDetails");
                        string file = Field(t, e, "file");
                        string line = Field(t, e, "line");
                        string warning = Field(t, e, "warning");
                        Debug.Log("SHADER CHECK " + (warning == "1" ? "WARNING" : "ERROR") + ": " + shader.name + " " + file + ":" + line +
                                  " " + message + (string.IsNullOrEmpty(details) ? "" : " | " + details));
                        failed = true;
                    }
                }
            }

            if (error)
                failed = true;
        }

        Debug.Log(failed ? "SHADER CHECK FAILED" : "SHADER CHECK OK");
        EditorApplication.Exit(failed ? 1 : 0);
    }

    private static string Field(Type t, object o, string name)
    {
        FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        object v = f != null ? f.GetValue(o) : null;
        return v != null ? v.ToString() : "";
    }
}
