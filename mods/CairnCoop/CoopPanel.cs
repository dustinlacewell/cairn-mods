using System;
using System.Collections.Generic;
using CairnCoop.Steam;
using UnityEngine;

namespace CairnCoop;

/// <summary>
/// The F4 IMGUI debug panel: Session / Game / Belay / Log tabs. Pure presentation + session control —
/// it reads the <see cref="GameDriver"/> and <see cref="CoopSession"/> and the log tail, and drives
/// host/join/stop through the session. No transport or game logic lives here.
/// </summary>
public sealed class CoopPanel
{
    private readonly GameDriver _driver;
    private readonly CoopSession _session;
    private readonly IEnumerable<string> _logTail;

    private int _uiTab; // 0 Session, 1 Game, 2 Belay, 3 Log
    private Rect _windowRect = new(40, 40, 440, 460);
    private Vector2 _logScroll;
    private List<string> _gameState = new();
    private DateTime _gameStateAt;

    public CoopPanel(GameDriver driver, CoopSession session, IEnumerable<string> logTail)
    {
        _driver = driver;
        _session = session;
        _logTail = logTail;
    }

    /// <summary>F4 toggles this. Also gates the friend-scan + cursor unlock in Core.OnUpdate.</summary>
    public bool Visible { get; set; }

    public void OnGUI()
    {
        if (!Visible)
            return;
        // GUILayout.Window is broken through the IL2CPP proxy (LayoutedWindow ctor stripped);
        // a plain layout area works fine.
        GUILayout.BeginArea(_windowRect, GUI.skin.box);
        GUILayout.Label("<b>Cairn Co-op</b> (F4)");

        GUILayout.BeginHorizontal();
        DrawTabButton("Session", 0);
        DrawTabButton("Game", 1);
        DrawTabButton("Belay", 2);
        DrawTabButton("Log", 3);
        GUILayout.EndHorizontal();
        GUILayout.Space(4);

        switch (_uiTab)
        {
            case 0: DrawSessionTab(); break;
            case 1: DrawGameTab(); break;
            case 2: DrawBelayTab(); break;
            case 3: DrawLogTab(); break;
        }
        GUILayout.EndArea();
    }

    private void DrawTabButton(string label, int tab)
    {
        if (GUILayout.Button(_uiTab == tab ? $"[ {label} ]" : label))
            _uiTab = tab;
    }

    private void DrawSessionTab()
    {
        if (!SteamApi.Ready && !SteamApi.TryInit())
        {
            GUILayout.Label("Waiting for Steam...");
            return;
        }

        GUILayout.Label($"Steam: {SteamApi.MyName()}" + (SteamApi.LoggedOn() ? "" : "  (OFFLINE)"));
        GUILayout.Label($"Session: {_driver.Describe()}");
        GUILayout.Label("Tip: be on a mountain when you host/join — remote climbers spawn in-level.");
        GUILayout.Space(6);

        if (_session.Active && _driver.State == GameDriver.Stage.InRoom)
        {
            // Connection is formed diegetically (reach a partner's ghost → request/accept). This panel button —
            // like the LT+RT ↑ wedge — is UNROPE-only: detach from the whole chain. Disabled when not roped.
            string ropeState = _driver.RopeConnected ? "CONNECTED" : "disconnected";
            GUI.enabled = _driver.RopeConnected;
            if (GUILayout.Button($"Unrope (whole chain)  [{ropeState}]  (LT+RT ↑)"))
                _driver.UnropeAll();
            GUI.enabled = true;
            GUILayout.Space(6);
        }

        switch (_session.CurrentRole)
        {
            case CoopSession.Role.None:
                if (GUILayout.Button("Host co-op"))
                    _session.StartHost();
                GUILayout.Space(8);
                GUILayout.Label("Friends hosting co-op:");
                var friends = _session.JoinableFriends;
                bool any = false;
                if (friends != null)
                {
                    foreach (var friend in friends)
                    {
                        any = true;
                        if (GUILayout.Button($"Join {friend.Name}"))
                            _session.StartJoin(friend.HostSteamId);
                    }
                }
                if (!any)
                    GUILayout.Label("  (none found — they need the mod + Host running)");
                GUILayout.Space(8);
                if (GUILayout.Button("Join local host (test, this machine)"))
                    _session.StartJoinLocal();
                _session.EnsureIdleRendezvous();
                break;

            case CoopSession.Role.Host:
                GUILayout.Label($"Hosting — Steam peers: {_session.HostPeerCount}");
                // Per-member rows with a Kick button (host only; the owner row has none — you can't kick yourself).
                foreach (var m in _session.RelayMembers())
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label($"  #{m.Id} {m.Name}" + (m.IsOwner ? "  (you, host)" : ""));
                    if (!m.IsOwner && GUILayout.Button("Kick", GUILayout.Width(60)))
                        _session.Kick(m.Id);
                    GUILayout.EndHorizontal();
                }
                if (GUILayout.Button("Stop hosting"))
                    _session.StopSession();
                break;

            case CoopSession.Role.Joiner:
                GUILayout.Label($"Joined {_session.JoinedHostName}");
                if (GUILayout.Button("Leave"))
                    _session.StopSession();
                break;

            case CoopSession.Role.LocalJoiner:
                GUILayout.Label("Local test client (loopback to host instance)");
                if (GUILayout.Button("Leave"))
                    _session.StopSession();
                break;
        }
    }

    private void DrawGameTab()
    {
        if ((DateTime.UtcNow - _gameStateAt).TotalSeconds > 1)
        {
            _gameStateAt = DateTime.UtcNow;
            _gameState = _driver.DescribeGameState();
        }
        foreach (var line in _gameState)
            GUILayout.Label(line);
    }

    private void DrawBelayTab()
    {
        // Live every frame — DescribeBelay just reads the reconciler's last-tick struct (cheap).
        foreach (var line in _driver.DescribeBelay())
            GUILayout.Label(line);
    }

    private void DrawLogTab()
    {
        _logScroll = GUILayout.BeginScrollView(_logScroll, GUILayout.Height(360));
        foreach (var line in _logTail)
            GUILayout.Label(line);
        GUILayout.EndScrollView();
    }
}
