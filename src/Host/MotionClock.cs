using System.Diagnostics;
using AxisLab.Core;

namespace AxisLab.Host;

public sealed class MotionClock(ProjectStore store, CommunicationService bridge, ProgramRunner programs) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed.TotalSeconds;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var now = clock.Elapsed.TotalSeconds;
                store.AdvanceMotions(now - last); programs.Tick(); bridge.Tick();
                last = clock.Elapsed.TotalSeconds;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { programs.Stop(); store.StopAllRobots(); }
    }
}
