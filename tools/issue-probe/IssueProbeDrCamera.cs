using System;
using UnityEngine;

/// <summary>
/// The old Dynamic Resolution mod's own camera (812713438, read 2026-11-16): culling mask 0, and in its
/// OnRenderImage the game's camera is given a target of the screen's size x a factor, rendered, given
/// its null target back, and the result downsampled to the screen. For tools/issue-probe.
/// </summary>
[ExecuteInEditMode]
public sealed class IssueProbeDrCamera : MonoBehaviour
{
    public Camera Game;
    /// <summary>The render targets, one render each in order (two: Lumina's and the old mod's at once).</summary>
    public RenderTexture[] Targets = new RenderTexture[0];
    /// <summary>Called after each render, with its index (read the probes there).</summary>
    public Action<int> AfterRender;
    public int Ran;

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        Ran++;
        if (Game == null || Targets.Length == 0)
        {
            Graphics.Blit(source, destination);
            return;
        }
        for (int i = 0; i < Targets.Length; i++)
        {
            Game.rect = new Rect(0f, 0f, 1f, 1f);
            Game.targetTexture = Targets[i];
            Game.Render();
            Game.targetTexture = null;
            if (AfterRender != null)
                AfterRender(i);
        }
        Graphics.Blit(Targets[Targets.Length - 1], destination);
    }
}
