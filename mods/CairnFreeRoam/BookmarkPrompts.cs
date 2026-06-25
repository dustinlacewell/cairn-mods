using CairnAPI;
using Il2Cpp;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace CairnFreeRoam;

/// <summary>
/// The on-screen button prompts shown in the eagle-eye view — "Add bookmark" (always, while in the view),
/// "Rename" and "Delete bookmark" (only when a custom bookmark row is selected). Each is a CairnAPI
/// <see cref="ScreenPrompt"/> parented into our OWN row — a child of the eagle-eye <c>InputPromptsLine</c>
/// carrying its own HorizontalLayoutGroup — one line under the game's prompts. A ScreenPrompt parented into a
/// layout group self-sizes (glyph + label) and the HLG flows them inline natively, no hand-positioning.
/// </summary>
public sealed class BookmarkPrompts
{
    private const float RowY = -44f;     // our row, one prompt-height below the eagle-eye prompt line
    private const float Spacing = 30f;   // the game's inter-prompt spacing on this line

    private ScreenPromptHandle _add;
    private ScreenPromptHandle _rename;
    private ScreenPromptHandle _delete;
    private GameObject _row;              // our HorizontalLayoutGroup row under the eagle-eye prompt line

    /// <summary>Build the row + the three prompts once. Idempotent. Each action is the SAME cross-device
    /// InputAction that triggers the operation, so the glyph adaptive-swaps KBM↔gamepad with the active device.</summary>
    public void Ensure(EagleEyeUI eye, InputAction add, InputAction rename, InputAction delete)
    {
        if (_row != null || eye == null) return;
        var line = PromptLine(eye);
        if (line == null) return;

        _row = BuildRow(line);
        _add = Make("Add bookmark", add);
        _rename = Make("Rename", rename);
        _delete = Make("Delete bookmark", delete);
    }

    /// <summary>Show the add prompt in-view; show rename/delete only when a custom point is selected.</summary>
    public void Refresh(EagleEyeUI eye, bool inView, bool deletable, bool renamable)
    {
        if (_row != null) _row.SetActive(inView);
        ScreenPrompt.SetActive(_add, inView);
        ScreenPrompt.SetActive(_rename, inView && renamable);
        ScreenPrompt.SetActive(_delete, inView && deletable);
    }

    /// <summary>Tear down the prompts and our row.</summary>
    public void Dispose()
    {
        ScreenPrompt.Hide(_add);
        ScreenPrompt.Hide(_rename);
        ScreenPrompt.Hide(_delete);
        if (_row != null) UnityEngine.Object.Destroy(_row);
        _add = _rename = _delete = null;
        _row = null;
    }

    // Build a screen prompt in our row (glyph from the cross-device action + label), hidden. Parented into the
    // HLG row so it self-sizes and lays out inline.
    private ScreenPromptHandle Make(string label, InputAction action)
    {
        var p = ScreenPrompt.Show(label, action, parent: _row.transform);
        if (p == null || !p.Valid) return null;
        p.SetActive(false);
        return p;
    }

    // Our own row under the eagle-eye prompt line: a stretched container with a HorizontalLayoutGroup so the
    // self-sizing prompts flow inline, centered, with the game's spacing.
    private static GameObject BuildRow(Transform line)
    {
        var go = new GameObject("CairnFreeRoam.PromptRow");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(line, false);
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.anchoredPosition = new Vector2(0f, RowY);
        rt.sizeDelta = new Vector2(0f, 40f);

        var hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = Spacing;
        hlg.childAlignment = TextAnchor.MiddleCenter;
        hlg.childControlWidth = true;      // the prompts report a real preferred width — let the HLG size+place
        hlg.childControlHeight = true;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = false;
        return go;
    }

    // The eagle-eye's own prompt line (holding "toggle teleport" / "fast travel" / "back").
    private static Transform PromptLine(EagleEyeUI eye)
    {
        var tmpl = eye.toggleFreeRoamTeleport != null ? eye.toggleFreeRoamTeleport.gameObject
                 : (eye.sharePathInput != null ? eye.sharePathInput.gameObject : null);
        return tmpl != null && tmpl.transform.parent != null ? tmpl.transform.parent.parent : null;
    }
}
