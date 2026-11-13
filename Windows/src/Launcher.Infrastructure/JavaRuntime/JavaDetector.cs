using Launcher.Core.JavaRuntime;
using Launcher.Core.Logging;

namespace Launcher.Infrastructure.JavaRuntime;

/// <summary>Находит кандидатов на диске и проверяет каждого запуском (по нескольку одновременно).</summary>
public sealed class JavaDetector : IJavaDetector
{
    private const int MaxParallelProbes = 4;

    private readonly Func<JavaSearchLocations> _locations;
    private readonly JavaProbe _probe;
    private readonly ILauncherLogger _logger;

    public JavaDetector(Func<JavaSearchLocations> locations, JavaProbe probe, ILauncherLogger? logger = null)
    {
        _locations = locations;
        _probe = probe;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public async Task<IReadOnlyList<JavaInstallation>> DetectAsync(CancellationToken ct)
    {
        var candidates = JavaCandidateFinder.Find(_locations());
        _logger.Info($"Кандидатов на Java: {candidates.Count}.");

        using var limiter = new SemaphoreSlim(MaxParallelProbes);
        var tasks = candidates.Select(async candidate =>
        {
            await limiter.WaitAsync(ct).ConfigureAwait(false);
            try { return await _probe.ProbeAsync(candidate.Path, candidate.Source, ct).ConfigureAwait(false); }
            finally { limiter.Release(); }
        }).ToList();

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.Where(r => r is not null).Select(r => r!).ToList(); // порядок обнаружения сохраняется
    }
}
