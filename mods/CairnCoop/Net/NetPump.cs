using System;
using System.Collections.Concurrent;
using System.Threading;

namespace CairnCoop.Net;

/// <summary>
/// Runs the co-op WIRE on a dedicated background thread, decoupled from the game's main thread.
///
/// Why this exists: the relay's socket servicing + timeout/resend used to run inside
/// <c>MelonMod.OnUpdate</c> — i.e. on the game thread. A multi-second main-thread stall (an Edelweiss
/// rewind hitches ~3.7 s; the 1 Hz belay pass ~60 ms) therefore froze the relay too: it stopped
/// receiving the partner's datagrams and stopped ticking, so on resume its wall-clock timeouts saw
/// every peer as "silent for seconds" and tore the whole room down — which is exactly what made the
/// rewind throw "Not in room" and stick (logs: both peers dropped within 200 ms during the host's own
/// rewind). The wire is pure bytes (UDP/Steam datagrams, ack bookkeeping) and touches NO Unity/IL2CPP
/// objects, so it has no business on the game thread.
///
/// This thread keeps the room alive across a main-thread freeze. Anything that DOES touch game objects
/// (the driver state machine, applying a rope-state to the belay, rendezvous bookkeeping) is marshalled
/// back to the main thread via <see cref="Post"/> and run in <see cref="DrainToMain"/> from OnUpdate.
///
/// Ownership contract: the supplied <c>pump</c> delegate and everything it reaches is the ONLY code
/// that runs on this thread. It must never call into Il2CppInterop / Unity. The caller serialises
/// transport construction/teardown against the pump (a lock the pump also takes) so fields can't be
/// swapped mid-iteration.
/// </summary>
internal sealed class NetPump : IDisposable
{
    private const int IntervalMs = 15; // ~66 Hz; the room runs at 20 Hz, so the wire stays comfortably ahead

    private readonly Action _pump;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action> _toMain = new();
    private readonly Thread _thread;
    private volatile bool _running = true;

    internal NetPump(Action pump, Action<string> log)
    {
        _pump = pump;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "CairnCoop-Net" };
        _thread.Start();
    }

    /// <summary>Queue a side-effect to run on the game main thread (drained each OnUpdate). Use for
    /// ANYTHING that touches game objects or main-thread-affine mod state.</summary>
    internal void Post(Action work) => _toMain.Enqueue(work);

    /// <summary>Run all queued main-thread work. Call once per frame from OnUpdate.</summary>
    internal void DrainToMain()
    {
        while (_toMain.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception e) { _log?.Invoke("net: main-thread work threw: " + e.Message); }
        }
    }

    private void Run()
    {
        while (_running)
        {
            try { _pump(); }
            catch (Exception e) { _log?.Invoke("net: pump threw: " + e.Message); }
            Thread.Sleep(IntervalMs);
        }
    }

    public void Dispose()
    {
        _running = false;
        try { if (!_thread.Join(250)) _log?.Invoke("net: pump thread did not stop in time"); }
        catch (Exception) { }
    }
}
