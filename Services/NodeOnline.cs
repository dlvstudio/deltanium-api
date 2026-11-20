using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Only nodes that answered recently are offered to apps and chosen for new replicas: a registered node that never
    /// answers (gone, or never real) must not make uploads fail. NodeLivenessWorker stamps LastSeen every few minutes.
    /// Window: Nodes:OnlineWindowMinutes (default 30; 0 turns the filter off; off unless Storage:Provider=MySql,
    /// the only store where LastSeen is kept). If no node at all looks online, the full list is returned rather than
    /// none, so a stalled liveness check cannot take the network down.
    /// </summary>
    public static class NodeOnline
    {
        public static TimeSpan? Window(IConfiguration? config)
        {
            if (config == null) return null;
            if (!string.Equals(config["Storage:Provider"], "MySql", StringComparison.OrdinalIgnoreCase)) return null;
            var minutes = double.TryParse(config["Nodes:OnlineWindowMinutes"], out var m) ? m : 30;
            return minutes > 0 ? TimeSpan.FromMinutes(minutes) : null;
        }

        public static List<T> Filter<T>(IEnumerable<T> nodes, Func<T, DateTime> lastSeen, IConfiguration? config, DateTime? nowUtc = null)
        {
            var all = nodes.ToList();
            var window = Window(config);
            if (window == null) return all;
            var since = (nowUtc ?? DateTime.UtcNow) - window.Value;
            var online = all.Where(n => lastSeen(n) >= since).ToList();
            return online.Count > 0 ? online : all;
        }
    }
}
