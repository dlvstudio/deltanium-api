using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Probes every registered Blocker and Store (see <see cref="INodeHealthService"/>) and stamps
    /// LastSeen on the ones that answer. Nothing else updates LastSeen after registration, yet blocker
    /// scheduling weights uptime by it (LastSeen within 10 minutes) and the explorer shows it.
    /// Interval: Nodes:LivenessIntervalMinutes (default 5).
    /// </summary>
    public class NodeLivenessWorker : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IConfiguration _config;
        private readonly ILogger<NodeLivenessWorker> _log;

        public NodeLivenessWorker(IServiceProvider services, IConfiguration config, ILogger<NodeLivenessWorker> log)
        {
            _services = services;
            _config = config;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var interval = TimeSpan.FromMinutes(double.TryParse(_config["Nodes:LivenessIntervalMinutes"], out var m) && m > 0 ? m : 5);
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var health = scope.ServiceProvider.GetRequiredService<INodeHealthService>();
                    var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();
                    var report = await health.GetReportAsync(stoppingToken);
                    var online = report.Blockers.Concat(report.Stores).Where(n => n.Online).ToList();
                    foreach (var n in online)
                        await storage.TouchNodeLastSeenAsync(n.Role, n.PublicKey, report.CheckedAt);
                    _log.LogInformation("Node liveness: {Online}/{Total} online",
                        online.Count, report.Blockers.Count + report.Stores.Count);

                    // Registrations whose fee the Super Admin approved: activate the nodes that now answer.
                    var regs = scope.ServiceProvider.GetService<NodeRegistrationService>();
                    if (regs != null)
                    {
                        var expired = await regs.ExpireUnpaidAsync();
                        if (expired > 0) _log.LogInformation("Node registrations: {Count} unpaid registration(s) expired", expired);
                        var activated = await regs.ActivateApprovedAsync();
                        if (activated > 0) _log.LogInformation("Node registrations: {Count} node(s) activated", activated);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogWarning(ex, "Node liveness tick failed");
                }
                try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
