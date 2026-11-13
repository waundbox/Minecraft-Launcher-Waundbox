using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Launcher.Core.Errors;
using Launcher.Core.GameLaunch;

namespace Launcher.Infrastructure.GameLaunch;

/// <summary>Запускает Java-процесс игры и перехватывает его stdout/stderr.</summary>
public sealed class SystemGameProcessStarter : IGameProcessStarter
{
    public IGameProcess Start(GameLaunchPlan plan, Action<GameOutputLine>? onOutput)
    {
        var startInfo = new ProcessStartInfo(plan.JavaPath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in plan.Arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(new GameOutputLine(false, e.Data)); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput?.Invoke(new GameOutputLine(true, e.Data)); };

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException)
        {
            process.Dispose();
            throw new LaunchException($"Не удалось запустить Java ({plan.JavaPath}): {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return new RunningGame(process);
    }

    private sealed class RunningGame : IGameProcess
    {
        private readonly Process _process;
        private volatile bool _killRequested;
        private volatile bool _exited;

        public int ProcessId { get; }
        public bool HasExited => _exited;
        public Task<GameExitResult> Completion { get; }

        public RunningGame(Process process)
        {
            _process = process;
            ProcessId = process.Id;
            Completion = Task.Run(async () =>
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                process.WaitForExit(); // дожидаемся, пока будет прочитан весь вывод
                var code = process.ExitCode;
                _exited = true;
                process.Dispose();
                return new GameExitResult(code, _killRequested);
            });
        }

        public void Kill()
        {
            _killRequested = true;
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException or ObjectDisposedException)
            {
                // игра уже закрылась
            }
        }
    }
}
