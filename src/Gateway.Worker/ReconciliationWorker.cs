using Gateway.Core;

namespace Gateway.Worker;

/// <summary>
/// Every few seconds, settles payments whose callback never arrived. Safe to run as
/// several replicas: each batch is claimed with FOR UPDATE SKIP LOCKED.
/// </summary>
public class ReconciliationWorker(IServiceScopeFactory scopes, IConfiguration config, ILogger<ReconciliationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Reconciliation:IntervalSeconds", 5));
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var summary = await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunOnceAsync(stoppingToken);
                // A full batch means there is a backlog; go again straight away.
                if (summary.Checked == ReconciliationService.BatchSize) continue;
            }
            // An HTTP timeout is also an OperationCanceledException, so only a shutdown may end
            // the loop. Anything else (provider down, database blip) is logged and retried.
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(e, "Reconciliation run failed; retrying next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
