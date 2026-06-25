using System;
using Il2Cpp;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// Recovers the two known black-screen / frozen-render states the game can get stuck in — game-state
/// recovery, not co-op logic. Lives here (not in the MelonMod root) so the orchestration layer holds
/// no game implementation; <see cref="Core"/> exposes it via F10 + console eval as a thin delegate.
///
/// 1) A leaked MainCamera blur-and-freeze request (cullingMask 0 + timeScale 0): PauseMenu / tutorial
///    HUD / loading screen take a refcounted freeze on show and release on hide; focus juggling between
///    two instances can leak one.
/// 2) A stuck BlackScreen fade overlay (camera renders fine behind it): on load the scene manager shows
///    BlackScreen and the FIRST CUTSCENE is responsible for hiding it
///    (firstCutsceneShouldHandleBlackScreen) — CairnNoCutscenes skipping that cutscene can orphan the hide.
/// </summary>
internal static class ScreenRecovery
{
    public static void ForceUnfreeze(Action<string> log)
    {
        int drained = 0;
        while (MainCamera.IsMainCameraRenderingBlurAndFreeze && drained < 16)
        {
            MainCamera.BlurAndFreezeMainCameraRendering(false, 1f);
            drained++;
        }
        float oldScale = Time.timeScale;
        Time.timeScale = 1f;
        log?.Invoke($"force-unfreeze: drained {drained} freeze request(s), timeScale {oldScale:0.##} -> 1");

        try
        {
            foreach (var blackScreen in UnityEngine.Object.FindObjectsOfType<BlackScreen>())
            {
                float alpha = blackScreen.GetCurrAlpha();
                if (alpha < 0.01f && !blackScreen.IsOpen)
                    continue;
                log?.Invoke($"force-unfreeze: hiding BlackScreen '{blackScreen.gameObject.name}' (alpha {alpha:0.##})");
                blackScreen.Hide(BlackScreen.FadeType.Simple, 0.25f, "CairnCoop", true);
            }
        }
        catch (Exception e)
        {
            log?.Invoke("force-unfreeze: BlackScreen hide failed: " + e.Message);
        }
    }
}
