using System.Collections.Generic;
using Il2Cpp;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(CairnRoutes.Core), "CairnRoutes", "0.1.0", "ldlework")]
[assembly: MelonGame("TheGameBakers", "Cairn")]
[assembly: MelonAdditionalDependencies("CairnAPI")]

namespace CairnRoutes;

/// <summary>
/// Personal route designer: record the line you actually climb, keep it on a
/// named route list, and draw/teleport-to your routes. F8 toggles the window.
/// </summary>
public class Core : MelonMod
{
    private const float SampleDistance = 0.35f;
    private const int MaxPoints = 50_000;

    private readonly RouteStore _store = new();
    private readonly RouteLines _lines = new();

    // Recording state
    private bool _recording;
    private readonly List<Vector3> _capture = new();
    private float _capturedMeters;

    // UI state
    private bool _showWindow;
    private Rect _windowRect = new(20f, 80f, 400f, 480f);
    private Vector2 _scroll;
    private string _pendingName = "";
    private string _status = "";
    private RouteData _armedDelete;
    private float _armedDeleteAt;

    public override void OnInitializeMelon()
    {
        _store.Load();
        LoggerInstance.Msg($"Loaded {_store.Routes.Count} route(s). F8 opens the window.");
    }

    public override void OnUpdate()
    {
        if (Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame && !EditField.IsEditing)
            ToggleWindow();

        if (_showWindow) EditField.Update();
        if (_recording) CaptureSample();
        EnsureLines();
        _store.SaveIfDue();
    }

    private void ToggleWindow()
    {
        _showWindow = !_showWindow;
        if (!_showWindow)
        {
            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
    }

    // ---------------- recording ----------------

    private void CaptureSample()
    {
        if (!TryGetPlayerPosition(out var pos)) return;
        if (_capture.Count > 0)
        {
            float step = Vector3.Distance(_capture[^1], pos);
            if (step < SampleDistance) return;
            _capturedMeters += step;
        }
        _capture.Add(pos);
        if (_capture.Count >= MaxPoints) StopRecording(save: true);
    }

    private void StartRecording()
    {
        if (!TryGetPlayerPosition(out _))
        {
            _status = "No climber in the world yet.";
            return;
        }
        _capture.Clear();
        _capturedMeters = 0f;
        _recording = true;
        if (string.IsNullOrWhiteSpace(_pendingName))
            _pendingName = $"Route {_store.Routes.Count + 1}";
        _status = "Recording…";
    }

    private void StopRecording(bool save)
    {
        _recording = false;
        if (!save || _capture.Count < 2)
        {
            _status = save ? "Too short to save." : "Recording discarded.";
            _capture.Clear();
            return;
        }
        var route = _store.Add(_pendingName.Trim(), _capture);
        _lines.SetVisible(route, true);
        _status = $"Saved \"{route.Name}\" ({_capturedMeters:F0} m, {_capture.Count} points).";
        _capture.Clear();
        _pendingName = "";
    }

    private static bool TryGetPlayerPosition(out Vector3 pos)
    {
        pos = default;
        if (!PawnManager.MCSpawned) return false;
        var mc = MoSingleton<PawnManager>.Instance?.MCGameObject;
        if (mc == null) return false;
        pos = mc.transform.position;
        return true;
    }

    // ---------------- line lifecycle ----------------

    /// <summary>Scene unloads destroy our line objects; respawn them with the pawn.</summary>
    private void EnsureLines()
    {
        if (!_lines.IsAlive && PawnManager.MCSpawned)
            _lines.Rebuild(_store.Routes);
    }

    // ---------------- UI ----------------

    private bool _guiFaulted;

    public override void OnGUI()
    {
        if (!_showWindow) return;
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;

        try
        {
            GUILayout.BeginArea(_windowRect, GUI.skin.box);
            GUILayout.Label("<b>Cairn Routes</b> (F8 to close)", RichLabel());

            DrawRecorder();
            GUILayout.Space(6f);
            DrawRouteList();

            if (!string.IsNullOrEmpty(_status))
                GUILayout.Label(_status);
            GUILayout.EndArea();
        }
        catch (System.Exception e)
        {
            if (!_guiFaulted)
            {
                _guiFaulted = true;
                LoggerInstance.Error($"OnGUI failed (logged once): {e}");
            }
        }
    }

    private void DrawRecorder()
    {
        if (_recording)
        {
            GUILayout.Label($"Recording: {_capturedMeters:F0} m, {_capture.Count} points");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stop && Save")) StopRecording(save: true);
            if (GUILayout.Button("Discard", GUILayout.Width(80f))) StopRecording(save: false);
            GUILayout.EndHorizontal();
            return;
        }

        GUILayout.BeginHorizontal();
        GUILayout.Label("Name:", GUILayout.Width(45f));
        _pendingName = EditField.Draw(_pendingNameId, _pendingName);
        if (GUILayout.Button("Record", GUILayout.Width(80f))) StartRecording();
        GUILayout.EndHorizontal();
    }

    private static readonly object _pendingNameId = new();

    private void DrawRouteList()
    {
        if (_store.Routes.Count == 0)
        {
            GUILayout.Label("No routes yet — climb something and record it.");
            return;
        }

        _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(300f));
        RouteData doomed = null;
        foreach (var route in _store.Routes)
        {
            GUILayout.BeginHorizontal();

            var renamed = EditField.Draw(route, route.Name, GUILayout.MinWidth(120f));
            if (renamed != route.Name) { route.Name = renamed; _store.MarkDirty(); }

            GUI.enabled = !Teleporter.Busy;
            if (GUILayout.Button("TP", GUILayout.Width(36f)))
            {
                var name = route.Name;
                _status = $"Warping to \"{name}\"…";
                Teleporter.TeleportTo(route,
                    ok => _status = ok ? $"Arrived at \"{name}\"." : $"Warp to \"{name}\" failed.");
            }
            GUI.enabled = true;

            if (GUILayout.Button($"D{route.Difficulty}", GUILayout.Width(36f)))
            {
                route.Difficulty = (route.Difficulty + 1) % 6;
                _lines.Refresh(route);
                _store.MarkDirty();
            }

            if (GUILayout.Button(route.Visible ? "Hide" : "Show", GUILayout.Width(50f)))
            {
                _lines.SetVisible(route, !route.Visible);
                _store.MarkDirty();
            }

            bool armed = _armedDelete == route && Time.unscaledTime - _armedDeleteAt < 3f;
            if (GUILayout.Button(armed ? "Sure?" : "Del", GUILayout.Width(50f)))
            {
                if (armed) doomed = route;
                else { _armedDelete = route; _armedDeleteAt = Time.unscaledTime; }
            }

            GUILayout.EndHorizontal();
            GUILayout.Label($"  {route.LengthMeters():F0} m · {route.CreatedAt}");
        }
        GUILayout.EndScrollView();

        if (doomed != null)
        {
            _lines.Remove(doomed);
            _store.Remove(doomed);
            _armedDelete = null;
            _status = $"Deleted \"{doomed.Name}\".";
        }
    }

    private static GUIStyle _richLabel;
    private static GUIStyle RichLabel() => _richLabel ??= new GUIStyle(GUI.skin.label) { richText = true };
}
