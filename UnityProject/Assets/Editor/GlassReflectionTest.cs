using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// GPU regression for the glass reflection's HISTORY pass (GlassReflection.shader pass 1), on synthetic
/// inputs, drawn as the mod draws it (a cube round the camera from a camera's command buffer):
///  - THE CLAMP: last frame's value far outside this frame's 3 x 3 neighbours is held to their box;
///    one inside it is blended at the history's weight; no history yet (weight 0) or no glass last
///    frame (alpha 0): this frame's value alone.
///  - THE REPROJECTION: the camera moved sideways between the frames; each pixel's history is fetched
///    where its virtual point (eye + view ray x the reflection's alpha) was on last frame's screen --
///    read back through a history that is a map of its own uv, within a pixel of the CPU's answer.
/// Run by tools/test-reflection-shader.ps1 (D3D11 and -force-glcore); writes glass-reflection-test-output.txt.
/// </summary>
public static class GlassReflectionTest
{
    private static readonly List<string> Results = new List<string>();
    private static int _failed;
    private const int W = 96, H = 64;
    private const float Weight = 0.85f;

    public static void Run()
    {
        try
        {
            PlayerSettings.colorSpace = ColorSpace.Linear;
            var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>("Assets/RealGlass/GlassReflection.shader"));
            var camera = new GameObject("reflection history test camera").AddComponent<Camera>();
            camera.enabled = false;
            camera.renderingPath = RenderingPath.DeferredLighting;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 1f;
            camera.farClipPlane = 5000f;
            camera.fieldOfView = 45f;
            camera.targetTexture = new RenderTexture(W, H, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            camera.targetTexture.Create();
            Mesh cube = Cube();

            RenderTexture current = Target(), history = Target(), output = Target();
            material.SetTexture("_RGReflect", current);
            var cb = new CommandBuffer { name = "reflection history test" };
            cb.SetGlobalTexture("_RGReflectionPrev", history);
            cb.SetRenderTarget(output);
            cb.DrawMesh(cube, Matrix4x4.identity, material, 0, 1);
            cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
            camera.AddCommandBuffer(CameraEvent.AfterFinalPass, cb);

            // ---- the clamp: the camera still, this frame a flat 0.2 (alpha = 100 m), last frame 1.0 ----
            camera.transform.position = new Vector3(0f, 50f, 0f);
            camera.transform.rotation = Quaternion.Euler(10f, 0f, 0f);
            Shader.SetGlobalMatrix("_RGPrevVP", camera.projectionMatrix * camera.worldToCameraMatrix);
            Fill(current, (x, y) => new Color(0.2f, 0.2f, 0.2f, 100f));
            Fill(history, (x, y) => new Color(1f, 1f, 1f, 1f));
            material.SetVector("_RGReflectionInfo", new Vector4(Weight, 1f / W, 1f / H, 0f));
            camera.Render();
            Color c = Read(output)[(H / 2) * W + W / 2];
            Check("clamp: last frame far outside the box is held to it (0.2 flat)", Mathf.Abs(c.r - 0.2f) < 0.002f, c.ToString("F4"));

            // Last frame inside the box (this frame 0.2 / 0.6 alternating, so every 3 x 3 spans 0.2..0.6): blended.
            Fill(current, (x, y) => ((x + y) & 1) == 0 ? new Color(0.2f, 0.2f, 0.2f, 100f) : new Color(0.6f, 0.6f, 0.6f, 100f));
            Fill(history, (x, y) => new Color(0.4f, 0.4f, 0.4f, 1f));
            camera.Render();
            Color[] px = Read(output);
            int at = (H / 2) * W + W / 2;
            float own = ((W / 2 + H / 2) & 1) == 0 ? 0.2f : 0.6f;
            float want = own + (0.4f - own) * Weight;
            Check("  inside the box: blended at the history's weight", Mathf.Abs(px[at].r - want) < 0.002f, px[at].r.ToString("F4") + " (want " + want.ToString("F4") + ")");
            Check("  the reflection's distance kept (alpha)", Mathf.Abs(px[at].a - 100f) < 0.1f, px[at].a.ToString("F2"));

            material.SetVector("_RGReflectionInfo", new Vector4(0f, 1f / W, 1f / H, 0f));
            camera.Render();
            px = Read(output);
            Check("  no history yet (weight 0): this frame's alone", Mathf.Abs(px[at].r - own) < 0.002f, px[at].r.ToString("F4"));
            material.SetVector("_RGReflectionInfo", new Vector4(Weight, 1f / W, 1f / H, 0f));
            Fill(history, (x, y) => new Color(0.4f, 0.4f, 0.4f, 0f));
            camera.Render();
            px = Read(output);
            Check("  no glass there last frame (alpha 0): this frame's alone", Mathf.Abs(px[at].r - own) < 0.002f, px[at].r.ToString("F4"));
            Fill(current, (x, y) => new Color(0f, 0f, 0f, 0f));
            Fill(history, (x, y) => new Color(1f, 1f, 1f, 1f));
            camera.Render();
            px = Read(output);
            Check("  no glass now: nothing written (0, alpha 0)", px[at].r == 0f && px[at].a == 0f, px[at].ToString("F4"));

            // ---- the reprojection: the camera 6 m to the right of last frame's; the reflections 300 m away ----
            const float distance = 300f;
            camera.transform.position = new Vector3(-6f, 50f, 0f);
            Matrix4x4 lastP = camera.projectionMatrix, lastV = camera.worldToCameraMatrix;
            Shader.SetGlobalMatrix("_RGPrevVP", lastP * lastV);
            camera.transform.position = new Vector3(0f, 50f, 0f);
            // This frame 0 / 1 in both channels by pixel parity: every 3 x 3 box spans 0..1, nothing clamps.
            Fill(current, (x, y) => new Color((x & 1), (y & 1), 0f, distance));
            // Last frame: its own uv.
            Fill(history, (x, y) => new Color((x + 0.5f) / W, (y + 0.5f) / H, 0f, 1f));
            camera.Render();
            px = Read(output);
            float worst = 0f;
            string detail = "";
            int checkedPx = 0;
            for (int y = 8; y < H - 8; y += 7)
            {
                for (int x = 8; x < W - 8; x += 9)
                {
                    Color o = px[y * W + x];
                    float hu = (o.r - (1f - Weight) * (x & 1)) / Weight, hv = (o.g - (1f - Weight) * (y & 1)) / Weight;
                    // The CPU's answer: the pixel's ray, the virtual point at the distance, last frame's screen.
                    Ray ray = camera.ViewportPointToRay(new Vector3((x + 0.5f) / W, (y + 0.5f) / H, 0f));
                    Vector3 pv = camera.transform.position + ray.direction.normalized * distance;
                    Vector4 clip = (lastP * lastV) * new Vector4(pv.x, pv.y, pv.z, 1f);
                    float eu = clip.x / clip.w * 0.5f + 0.5f, ev = clip.y / clip.w * 0.5f + 0.5f;
                    if (eu < 1.5f / W || eu > 1f - 1.5f / W)
                        continue;
                    checkedPx++;
                    float err = Mathf.Max(Mathf.Abs(hu - eu) * W, Mathf.Abs(hv - ev) * H);
                    if (err > worst)
                    {
                        worst = err;
                        detail = "pixel " + x + "," + y + ": fetched " + hu.ToString("F4") + "," + hv.ToString("F4") + " want " + eu.ToString("F4") + "," + ev.ToString("F4");
                    }
                }
            }
            Check("reprojection: the camera moved 6 m, reflections 300 m away: history fetched along the virtual point within a pixel (" + checkedPx + " pixels)",
                  checkedPx > 20 && worst < 1f, worst.ToString("F3") + " px worst; " + detail);
        }
        catch (Exception e)
        {
            _failed++;
            Results.Add("EXCEPTION " + e);
        }
        Results.Insert(0, (_failed == 0 ? "GLASS REFLECTION TEST OK" : "GLASS REFLECTION TEST FAILED: " + _failed) + " on " + SystemInfo.graphicsDeviceType);
        File.WriteAllLines("glass-reflection-test-output.txt", Results.ToArray());
        EditorApplication.Exit(_failed == 0 ? 0 : 1);
    }

    private static RenderTexture Target()
    {
        var rt = new RenderTexture(W, H, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        rt.Create();
        return rt;
    }

    private static void Fill(RenderTexture rt, Func<int, int, Color> f)
    {
        var t = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
        var px = new Color[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                px[y * W + x] = f(x, y);
        t.SetPixels(px);
        t.Apply();
        Graphics.Blit(t, rt);
        UnityEngine.Object.DestroyImmediate(t);
    }

    private static Color[] Read(RenderTexture rt)
    {
        RenderTexture.active = rt;
        var t = new Texture2D(W, H, TextureFormat.RGBAFloat, false, true);
        t.ReadPixels(new Rect(0, 0, W, H), 0, 0);
        RenderTexture.active = null;
        Color[] px = t.GetPixels();
        UnityEngine.Object.DestroyImmediate(t);
        return px;
    }

    private static Mesh Cube()
    {
        var mesh = new Mesh();
        mesh.vertices = new[]
        {
            new Vector3(-1, -1, -1), new Vector3(1, -1, -1), new Vector3(1, 1, -1), new Vector3(-1, 1, -1),
            new Vector3(-1, -1, 1), new Vector3(1, -1, 1), new Vector3(1, 1, 1), new Vector3(-1, 1, 1),
        };
        mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5 };
        mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        return mesh;
    }

    private static void Check(string name, bool ok, string detail)
    {
        Results.Add((ok ? "ok " : "FAIL ") + name + "  ->  " + detail);
        if (!ok) _failed++;
    }
}
