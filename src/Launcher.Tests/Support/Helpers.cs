using System.Net;
using System.Security.Cryptography;
using Launcher.Core.Downloads;
using Launcher.Core.JavaRuntime;

namespace Launcher.Tests.Support;

/// <summary>Базовый класс тестов с временной папкой, которая удаляется после теста.</summary>
public abstract class TempDirectoryTest : IDisposable
{
    protected string Dir { get; } = Path.Combine(Path.GetTempPath(), "waundbox-tests-" + Guid.NewGuid().ToString("N"));

    protected TempDirectoryTest() => Directory.CreateDirectory(Dir);

    public void Dispose()
    {
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>HTTP-обработчик, отдающий заготовленные ответы по очереди (последний повторяется).</summary>
internal sealed class SequenceHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage>[] _responses;
    public int Calls { get; private set; }

    public SequenceHandler(params Func<HttpResponseMessage>[] responses) => _responses = responses;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var factory = _responses[Math.Min(Calls, _responses.Length - 1)];
        Calls++;
        return Task.FromResult(factory());
    }
}

internal static class Responses
{
    public static Func<HttpResponseMessage> Ok(byte[] body) =>
        () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static Func<HttpResponseMessage> Status(HttpStatusCode code) => () => new HttpResponseMessage(code);

    public static string Sha1Of(byte[] data) => Convert.ToHexStringLower(SHA1.HashData(data));
}

/// <summary>Поддельный загрузчик: «скачивает» заранее заданное содержимое по URL, ничего не обращаясь к сети.</summary>
internal sealed class FakeDownloader : IFileDownloader
{
    private readonly Dictionary<string, byte[]> _content = new();
    private readonly object _lock = new();

    public List<DownloadRequest> Requests { get; } = new();

    public FakeDownloader Serve(string url, byte[] content)
    {
        _content[url] = content;
        return this;
    }

    public FakeDownloader Serve(string url, string content) => Serve(url, System.Text.Encoding.UTF8.GetBytes(content));

    public Task<DownloadOutcome> DownloadAsync(DownloadRequest request, IProgress<long>? bytesProgress, CancellationToken ct)
    {
        lock (_lock) Requests.Add(request);

        if (File.Exists(request.DestinationPath))
            return Task.FromResult(DownloadOutcome.AlreadyValid);

        Directory.CreateDirectory(Path.GetDirectoryName(request.DestinationPath)!);
        var bytes = _content.TryGetValue(request.Url.ToString(), out var c) ? c : "data"u8.ToArray();
        File.WriteAllBytes(request.DestinationPath, bytes);
        bytesProgress?.Report(bytes.Length);
        return Task.FromResult(DownloadOutcome.Downloaded);
    }
}

internal sealed class FakeProcessRunner(Func<string, ProcessResult> respond) : Launcher.Core.JavaRuntime.IProcessRunner
{
    public List<(string File, IReadOnlyList<string> Args)> Calls { get; } = new();
    private readonly object _lock = new();

    public Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        lock (_lock) Calls.Add((fileName, arguments));
        return Task.FromResult(respond(fileName));
    }
}

internal sealed class FakeJavaDetector(params Launcher.Core.JavaRuntime.JavaInstallation[] found) : Launcher.Core.JavaRuntime.IJavaDetector
{
    public Task<IReadOnlyList<Launcher.Core.JavaRuntime.JavaInstallation>> DetectAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Launcher.Core.JavaRuntime.JavaInstallation>>(found);
}

internal static class JavaSamples
{
    /// <summary>Типичный вывод "java -XshowSettings:properties -version" (идёт в stderr).</summary>
    public static string Output(string version, string vendor = "Eclipse Adoptium", string bits = "64") => $$"""
        Property settings:
            java.class.path = 
            java.home = /opt/java
            java.library.path = /usr/lib
                /lib
            java.vendor = {{vendor}}
            java.version = {{version}}
            os.arch = amd64
            sun.arch.data.model = {{bits}}

        openjdk version "{{version}}" 2024-07-16 LTS
        """;

    public static ProcessResult Ok(string version, string bits = "64") => new(0, "", Output(version, bits: bits), false);

    public static Launcher.Core.JavaRuntime.JavaInstallation Make(
        int major, bool is64 = true, string path = "", Launcher.Core.JavaRuntime.JavaSource source = Launcher.Core.JavaRuntime.JavaSource.Installed) =>
        new(string.IsNullOrEmpty(path) ? $"/java{major}{(is64 ? "" : "-32")}/bin/java" : path,
            major, major == 8 ? "1.8.0_392" : $"{major}.0.1", "Test", is64, source);
}

internal sealed class MemorySettings(Launcher.Core.Configuration.LauncherSettings? initial = null) : Launcher.Core.Configuration.ISettingsStore
{
    public Launcher.Core.Configuration.LauncherSettings Current { get; private set; } = initial ?? new Launcher.Core.Configuration.LauncherSettings();

    public Task<Launcher.Core.Configuration.LauncherSettings> LoadAsync(CancellationToken ct) => Task.FromResult(Current);

    public Task SaveAsync(Launcher.Core.Configuration.LauncherSettings settings, CancellationToken ct)
    {
        Current = settings;
        return Task.CompletedTask;
    }
}

internal sealed class FakeGameProcess : Launcher.Core.GameLaunch.IGameProcess
{
    public int ProcessId => 4242;
    public bool HasExited => true;
    public Task<Launcher.Core.GameLaunch.GameExitResult> Completion { get; } =
        Task.FromResult(new Launcher.Core.GameLaunch.GameExitResult(0, false));
    public void Kill() { }
}

internal sealed class FakeStarter : Launcher.Core.GameLaunch.IGameProcessStarter
{
    public Launcher.Core.GameLaunch.GameLaunchPlan? Plan { get; private set; }

    public Launcher.Core.GameLaunch.IGameProcess Start(
        Launcher.Core.GameLaunch.GameLaunchPlan plan, Action<Launcher.Core.GameLaunch.GameOutputLine>? onOutput)
    {
        Plan = plan;
        return new FakeGameProcess();
    }
}
