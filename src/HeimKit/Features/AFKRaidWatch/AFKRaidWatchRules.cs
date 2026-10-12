using HeimKit.Shared.Players;
using HeimKit.Shared.Players.Models;
using HeimKit.Features.AFKRaidWatch.Models;
using System;
using System.Collections.Generic;

namespace HeimKit.Features.AFKRaidWatch;

public static class AFKRaidWatchRules
{
    public static bool Suppress(float x, float z, float radius, IEnumerable<PlayerActivityView> players)
        => Suppress(x, z, radius, players, out _);
    public static bool Suppress(float x, float z, float radius, IEnumerable<PlayerActivityView> players, out string reason)
    {
        reason = "invalid_event_geometry";
        if (!PlayerActivityTracker.Finite(x) || !PlayerActivityTracker.Finite(z) || !PlayerActivityTracker.Finite(radius) || radius <= 0) return false;
        bool nearby = false;
        foreach (var player in players)
        {
            if (!player.Known || !PlayerActivityTracker.Finite(player.X) || !PlayerActivityTracker.Finite(player.Y) || !PlayerActivityTracker.Finite(player.Z)) { reason = "unknown_actor_data"; return false; }
            if (player.Y > 3000) continue;
            double dx = player.X - x, dz = player.Z - z;
            if (dx * dx + dz * dz >= (double)radius * radius) continue;
            nearby = true;
            if (!player.Alive || player.InBed || !player.Idle) { reason = !player.Alive ? "nearby_dead_or_loading" : player.InBed ? "nearby_bed_occupant" : "nearby_active_player"; return false; }
        }
        reason = nearby ? "all_nearby_players_inferred_idle" : "no_nearby_actor_evidence";
        return nearby;
    }
}
