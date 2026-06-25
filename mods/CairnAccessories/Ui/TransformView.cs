using UnityEngine;

namespace CairnAccessories;

/// <summary>
/// The Transform window: three columns (Move / Rotate / Scale) live at once — no mode switch.
/// Move and Rotate give per-axis X/Y/Z −/+; Scale gives a single global −/+ (uniform). Each column
/// shows its current value and a per-column step −/+. Nudge buttons hold-to-repeat. Everything
/// drives the shared <see cref="Gizmo"/>, so buttons and keyboard stay in sync. Shown with the Editor.
/// </summary>
public sealed class TransformView
{
    private const string Key = "transform";

    private readonly AccessoryManager _mgr;
    private readonly Gizmo _gizmo;
    private readonly ModConfig _config;
    private Rect _rect;

    public TransformView(AccessoryManager mgr, Gizmo gizmo, ModConfig config)
    {
        _mgr = mgr;
        _gizmo = gizmo;
        _config = config;
        _rect = config.Window(Key, new Rect(20, 452, 410, 240));
    }

    public void Draw()
    {
        Gui.RepeatInterval = _config.RepeatInterval;   // apply the persisted repeat speed
        _rect = GUI.Window(0xACD0, _rect, (GUI.WindowFunction)Window, "Transform");
        _config.SetWindow(Key, _rect);
    }

    // Absolute layout: laying rows out by hand (vs GUILayout columns) gives each dimension row a
    // known rect, so it can react to hover-scroll (GUILayoutUtility.GetLastRect is stripped here).
    private const float Pad = 8f;
    private const float HeaderY = 22f;     // repeat-speed slider row
    private const float ColTopY = 50f;     // columns start below the slider
    private const float RowH = 22f;
    private const float LabelH = 18f;

    private void Window(int id)
    {
        var s = _mgr.Selected;

        DrawRepeatSlider();
        DrawColumn(s, 0, Gizmo.Mode.Translate, "Move");
        DrawColumn(s, 1, Gizmo.Mode.Rotate, "Rotate");
        DrawColumn(s, 2, Gizmo.Mode.Scale, "Scale");

        // Align button spans the bottom, above the grip.
        float btnY = ColTopY + LabelH + LabelH + RowH + 3 * RowH + 6f;
        if (GUI.Button(new Rect(Pad, btnY, _rect.width - 2 * Pad, RowH), "Align to bone"))
            _gizmo.AlignToBone(s);

        Gui.ResizeGrip(id, ref _rect);
        Gui.TitleDrag(_rect.width);
    }

    private void DrawRepeatSlider()
    {
        GUI.Label(new Rect(Pad, HeaderY, 90f, LabelH), "repeat speed");
        // slider value = fires/sec (1..20); interval = 1/value. Higher = faster = right.
        float fps = 1f / Mathf.Max(0.0001f, _config.RepeatInterval);
        float w = _rect.width - 2 * Pad - 96f;
        fps = GUI.HorizontalSlider(new Rect(Pad + 96f, HeaderY + 4f, w, LabelH), fps, 1f, 20f);
        _config.RepeatInterval = 1f / Mathf.Clamp(fps, 1f, 20f);   // persisted; applied next frame
    }

    private void DrawColumn(Accessory s, int col, Gizmo.Mode mode, string label)
    {
        float colW = (_rect.width - 2 * Pad - 2 * 4f) / 3f;
        float x = Pad + col * (colW + 4f);
        float y = ColTopY;

        GUI.Box(new Rect(x, y, colW, LabelH * 2 + RowH * 4 + 6f), GUIContent.none);
        GUI.Label(new Rect(x + 4f, y, colW - 8f, LabelH), label);
        y += LabelH;
        GUI.Label(new Rect(x + 4f, y, colW - 8f, LabelH), CurrentValue(s, mode));
        y += LabelH;

        DrawStepRow(new Rect(x + 4f, y, colW - 8f, RowH), mode);
        y += RowH;

        if (mode == Gizmo.Mode.Scale)
            DrawNudgeRow(new Rect(x + 4f, y, colW - 8f, RowH), s, mode, "", -1);  // single uniform row
        else
            for (int axis = 0; axis < 3; axis++)
            {
                DrawNudgeRow(new Rect(x + 4f, y, colW - 8f, RowH), s, mode, "XYZ"[axis].ToString(), axis);
                y += RowH;
            }
    }

    private void DrawStepRow(Rect r, Gizmo.Mode mode)
    {
        GUI.Label(new Rect(r.x, r.y + 2f, r.width - 56f, r.height), "step " + _gizmo.StepTextFor(mode));
        if (GUI.Button(new Rect(r.xMax - 52f, r.y, 24f, r.height - 2f), "−")) _gizmo.ScaleStepFor(mode, 0.5f);
        if (GUI.Button(new Rect(r.xMax - 26f, r.y, 24f, r.height - 2f), "+")) _gizmo.ScaleStepFor(mode, 2f);
    }

    // One dimension row: label + −/+ (hold-repeat) + hover-scroll on the whole row.
    private void DrawNudgeRow(Rect r, Accessory s, Gizmo.Mode mode, string label, int axis)
    {
        bool uniform = mode == Gizmo.Mode.Scale;
        if (!uniform) GUI.Label(new Rect(r.x, r.y + 2f, 14f, r.height), label);

        float bx = uniform ? r.x : r.x + 16f;
        float bw = (r.xMax - bx - 4f) / 2f;
        string idn = $"{mode}{axis}";

        if (Gui.RepeatButtonAt(new Rect(bx, r.y, bw, r.height - 2f), idn + "-", "−")) Apply(s, mode, axis, -1);
        if (Gui.RepeatButtonAt(new Rect(bx + bw + 4f, r.y, bw, r.height - 2f), idn + "+", "+")) Apply(s, mode, axis, +1);

        // hover-scroll the whole row -> ±1 step
        int scroll = Gui.ScrollOver(r);
        if (scroll != 0) Apply(s, mode, axis, scroll);
    }

    private void Apply(Accessory s, Gizmo.Mode mode, int axis, int sign)
    {
        if (mode == Gizmo.Mode.Scale) _gizmo.NudgeUniformScale(s, sign);
        else _gizmo.Nudge(s, mode, axis, sign);
    }

    /// <summary>The accessory's live local value for this column (pos / euler° / scale).</summary>
    private static string CurrentValue(Accessory s, Gizmo.Mode mode)
    {
        var t = s?.Instance != null ? s.Instance.transform : null;
        if (t == null) return "—";
        return mode switch
        {
            Gizmo.Mode.Translate => Fmt(t.localPosition * 1000f, "0", "mm"),     // mm
            Gizmo.Mode.Rotate    => Fmt(t.localEulerAngles, "0.#", "°"),         // degrees
            _                    => $"{t.localScale.x * 100f:0.#}%",            // uniform %
        };
    }

    private static string Fmt(Vector3 v, string n, string unit) =>
        $"{v.x.ToString(n)},{v.y.ToString(n)},{v.z.ToString(n)} {unit}";
}
