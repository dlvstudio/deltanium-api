using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeltaniumApi.Services
{
    /// <summary>
    /// Periodic economics jobs (TOKENOMICS_V2): proof-of-storage challenges (daily), egress report pull,
    /// monthly escrow settlement / auto-renew, bond withdrawals. Disable with Economics:Worker:Enabled=false.
    /// </summary>
    public class EconomicsWorker : BackgroundService
    {
        private readonly IServiceProvider _services;
        private readonly IConfiguration _config;
        private readonly ILogger<EconomicsWorker> _log;
        private DateTime _lastChallenge = DateTime.MinValue;

        public EconomicsWorker(IServiceProvider services, IConfiguration config, ILogger<EconomicsWorker> log)
        {
            _services = services;
            _config = config;
            _log = log;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (string.Equals(_config["Economics:Worker:Enabled"], "false", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("EconomicsWorker disabled");
                return;
            }
            var interval = TimeSpan.FromMinutes(double.TryParse(_config["Economics:Worker:IntervalMinutes"], out var m) ? m : 60);
            var challengeEvery = TimeSpan.FromHours(double.TryParse(_config["Economics:Proof:ChallengeIntervalHours"], out var h) ? h : 24);

            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); } catch (OperationCanceledException) { return; }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _services.CreateScope();
                    var econ = scope.ServiceProvider.GetRequiredService<EconomicsService>();
                    if (DateTime.UtcNow - _lastChallenge >= challengeEvery)
                    {
                        var (c, p) = await econ.RunChallengesAsync();
                        _lastChallenge = DateTime.UtcNow;
                        _log.LogInformation("Proof-of-storage: {Passed}/{Challenged} passed", p, c);
                    }
                    var egress = await econ.PullEgressReportsAsync();
                    var settled = await econ.SettleDueAsync();
                    var bonds = await econ.ProcessBondWithdrawalsAsync();
                    var repaired = await econ.RepairReplicasAsync();
                    if (repaired > 0) _log.LogInformation("Economics: {Count} replicas re-created", repaired);
                    _log.LogInformation("Economics tick: egress items={Egress}, months settled={Settled}, bond withdrawals={Bonds}", egress, settled, bonds);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "EconomicsWorker tick failed");
                }
                try { await Task.Delay(interval, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }
    }
}
