using System;
using CairnAPI;

namespace CairnRoutes;

/// <summary>
/// Route-flavored teleport: warp the climber to a route's start. The warp mechanism itself lives
/// in CairnAPI.Teleport (the streaming-aware FreeRoamManager.WarpToPoint path); this is just the
/// route-shaped entry point the route window calls.
/// </summary>
public static class Teleporter
{
    public static bool Busy => Teleport.Busy;

    public static void TeleportTo(RouteData route, Action<bool> done)
        => Teleport.To(route.Start, done);
}
