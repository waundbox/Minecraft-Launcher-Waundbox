using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Launcher.Core.GameLaunch;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

public sealed record ConsoleLine(string Text, bool IsError, bool IsLauncher);

/// <summary>
/// Консоль одного запуска (одного профиля). Строки приходят из потоков процесса в очередь сеанса;
/// интерфейс забирает их пачками. Хранится не больше <see cref="MaxLines"/> строк — остальное в файле журнала.
/// </summary>
public sealed class ConsoleSession : ObservableObject
{
    internal const int MaxLines = 4000;

    private bool _isLive = true;

    internal ConsoleSession(string name, string logPath)
    {
        Name = name;
        LogPath = logPath;
    }

    public string Name { get; }
    public string LogPath { get; internal set; }
    public BulkObservableCollection<ConsoleLine> Lines { get; } = new();

    /// <summary>Игра ещё работает (или готовится к запуску).</summary>
    public bool IsLive
    {
        get => _isLive;
        internal set => Set(ref _isLive, value);
    }

    internal ConcurrentQueue<ConsoleLine> Pending { get; } = new();
    internal object LogGate { get; } = new();
    internal StreamWriter? Log { get; set; }
    internal int Scheduled;

    /// <summary>Лог очистили вручную: после завершения игры сеанс убирается из списка.</summary>
    internal bool Cleared { get; set; }
}

/// <summary>
/// Консоль игры: у каждого запущенного профиля свой сеанс (вкладка). Можно играть в несколько профилей сразу,
/// вывод игр не смешивается. Завершённые сеансы остаются для просмотра (последние несколько).
/// </summary>
internal sealed class ConsoleViewModel : PageViewModel
{
    private const int TrimBatch = 500;
    private const int FlushBatch = 2000;
    private const int KeepFinished = 5;

    private static readonly BulkObservableCollection<ConsoleLine> NoLines = new();

    private readonly DispatcherTimer _timer;
    private readonly Dispatcher _dispatcher;
    private readonly UiContext _ctx;
    private readonly List<ConsoleSession> _all = new(); // доступ только из потока интерфейса, кроме Pending
    private ConsoleSession? _current;
    private bool _autoScroll = true;

    /// <summary>Значок вкладки «Логи»: листок с текстом.</summary>
    public override string? IconData => "F1 M34 227 c-2 -7 -3 -60 -2 -118 l3 -104 85 0 85 0 3 89 c3 85 2 90 -24 118 -24 24 -35 28 -86 28 -39 0 -60 -4 -64 -13z m108 -24 c2 -22 9 -29 31 -31 l28 -3 -3 -77 -3 -77 -75 0 -75 0 -3 94 c-1 52 -1 100 2 108 3 8 22 13 50 13 41 0 45 -2 48 -27z m38 -14 c0 -5 -4 -9 -10 -9 -5 0 -10 7 -10 16 0 8 5 12 10 9 6 -3 10 -10 10 -16z M93 133 c15 -2 39 -2 55 0 15 2 2 4 -28 4 -30 0 -43 -2 -27 -4z M93 103 c15 -2 39 -2 55 0 15 2 2 4 -28 4 -30 0 -43 -2 -27 -4z M93 73 c15 -2 39 -2 55 0 15 2 2 4 -28 4 -30 0 -43 -2 -27 -4z M75 50 c-4 -6 8 -10 29 -10 20 0 36 5 36 10 0 6 -13 10 -29 10 -17 0 -33 -4 -36 -10z";

    public ConsoleViewModel(UiContext ctx) : base("Логи", "")
    {
        _ctx = ctx;
        _dispatcher = Application.Current.Dispatcher;
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Flush();

        ClearCommand = new RelayCommand(Clear, () => _current is not null);
        CopyCommand = new RelayCommand(Copy, () => Lines.Count > 0);
        OpenLogCommand = new RelayCommand(OpenLog, () => _current is { LogPath.Length: > 0 } c && File.Exists(c.LogPath));
    }

    public ObservableCollection<ConsoleSession> Sessions { get; } = new();

    public BulkObservableCollection<ConsoleLine> Lines => _current?.Lines ?? NoLines;

    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    public bool AutoScroll
    {
        get => _autoScroll;
        set => Set(ref _autoScroll, value);
    }

    /// <summary>Показываемый сеанс.</summary>
    public ConsoleSession? Current
    {
        get => _current;
        set
        {
            if (!Set(ref _current, value)) return;
            OnPropertyChanged(nameof(Lines));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(SessionTitle));
            OnPropertyChanged(nameof(HasSeveralSessions));
            Flushed?.Invoke();
        }
    }

    public string SessionTitle => _current is null ? "Игра не запускалась" : $"Профиль «{_current.Name}»";

    /// <summary>Вкладки показываются, когда сеансов больше одного.</summary>
    public bool HasSeveralSessions => Sessions.Count > 1;

    /// <summary>Вызывается после каждой пачки строк — страница прокручивает список к концу.</summary>
    public event Action? Flushed;

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Открывает консоль профиля (например, когда игра упала).</summary>
    public void Select(string profileName)
    {
        var session = _all.FirstOrDefault(s => s.Name == profileName);
        if (session is not null) Current = session;
    }

    /// <summary>Начинает сеанс профиля (из потока интерфейса). Старый сеанс того же профиля заменяется.</summary>
    public ConsoleSession BeginSession(string profileName, string logPath)
    {
        var session = _all.FirstOrDefault(s => s.Name == profileName);
        if (session is null)
        {
            session = new ConsoleSession(profileName, logPath);
            _all.Add(session);
            Sessions.Add(session);
        }
        else
        {
            CloseLog(session);
            while (session.Pending.TryDequeue(out _)) { }
            session.Lines.ReplaceAll(Array.Empty<ConsoleLine>());
            session.LogPath = logPath;
            session.Cleared = false;
            session.IsLive = true;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            lock (session.LogGate) session.Log = new StreamWriter(logPath, append: false) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Services.Logger.Warn("Не удалось открыть журнал игры.", ex);
            session.LogPath = "";
        }

        TrimFinished(keep: session);
        Current = session;
        OnPropertyChanged(nameof(HasSeveralSessions));
        OnPropertyChanged(nameof(IsEmpty));
        return session;
    }

    public void EndSession(ConsoleSession session)
    {
        CloseLog(session);
        session.IsLive = false;
        if (session.Cleared) RemoveSession(session); // лог очищен и игра закрыта — профиль пропадает из логов
    }

    private void RemoveSession(ConsoleSession session)
    {
        var wasCurrent = ReferenceEquals(_current, session);
        _all.Remove(session);
        Sessions.Remove(session);
        if (wasCurrent) Current = Sessions.LastOrDefault();
        OnPropertyChanged(nameof(HasSeveralSessions));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SessionTitle));
    }

    /// <summary>Вывод игры (вызывается из потоков процесса).</summary>
    public void Write(ConsoleSession session, GameOutputLine line)
    {
        var text = line.IsError ? "[ERR] " + line.Text : line.Text;
        lock (session.LogGate) session.Log?.WriteLine(text);
        Enqueue(session, new ConsoleLine(line.Text, line.IsError, false));
    }

    public void WriteLauncher(ConsoleSession session, string text, bool isError = false)
    {
        lock (session.LogGate) session.Log?.WriteLine("[Waundbox] " + text);
        Enqueue(session, new ConsoleLine(text, isError, true));
    }

    private void Enqueue(ConsoleSession session, ConsoleLine line)
    {
        session.Pending.Enqueue(line);
        if (Interlocked.Exchange(ref session.Scheduled, 1) == 0)
            _dispatcher.BeginInvoke(() => _timer.Start());
    }

    private void Flush()
    {
        var more = false;
        foreach (var session in _all.ToArray())
        {
            if (session.Pending.IsEmpty)
            {
                Interlocked.Exchange(ref session.Scheduled, 0);
                // строка могла прийти между проверкой и сбросом флага
                if (!session.Pending.IsEmpty && Interlocked.Exchange(ref session.Scheduled, 1) == 0) more = true;
                continue;
            }

            var batch = new List<ConsoleLine>(256);
            while (batch.Count < FlushBatch && session.Pending.TryDequeue(out var line)) batch.Add(line);

            var wasEmpty = session.Lines.Count == 0;
            session.Lines.AddRange(batch);
            if (session.Lines.Count > ConsoleSession.MaxLines)
                session.Lines.RemoveFirst(session.Lines.Count - ConsoleSession.MaxLines + TrimBatch);

            more = true;
            if (ReferenceEquals(session, _current))
            {
                if (wasEmpty) OnPropertyChanged(nameof(IsEmpty));
                Flushed?.Invoke();
            }
        }

        if (!more) _timer.Stop();
    }

    private void TrimFinished(ConsoleSession keep)
    {
        var finished = _all.Where(s => !s.IsLive && !ReferenceEquals(s, keep)).ToList();
        foreach (var old in finished.Take(Math.Max(0, finished.Count - KeepFinished)))
        {
            _all.Remove(old);
            Sessions.Remove(old);
        }
    }

    private void Clear()
    {
        if (_current is null) return;
        while (_current.Pending.TryDequeue(out _)) { }
        _current.Lines.ReplaceAll(Array.Empty<ConsoleLine>());
        _current.Cleared = true;
        OnPropertyChanged(nameof(IsEmpty));
        // игра уже закрыта — профиль исчезает из логов сразу, иначе исчезнет, когда игра завершится
        if (!_current.IsLive) RemoveSession(_current);
    }

    private void Copy() => CopyLines(Lines);

    /// <summary>Копирует строки консоли в буфер обмена (все или выделенные).</summary>
    public void CopyLines(IEnumerable<ConsoleLine> lines)
    {
        try
        {
            var list = lines.ToList();
            if (list.Count == 0) return;
            Clipboard.SetText(string.Join(Environment.NewLine, list.Select(l => l.Text)));
            _ctx.Toasts.Show(list.Count == Lines.Count ? "Логи скопированы в буфер обмена." : $"Скопировано строк: {list.Count}.", ToastKind.Success);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _ctx.Toasts.Show("Буфер обмена сейчас занят другой программой. Попробуйте ещё раз.", ToastKind.Warning);
        }
    }

    private void OpenLog()
    {
        if (_current is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(_current.LogPath) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _ctx.Toasts.Show("Не удалось открыть файл журнала.", ToastKind.Error);
        }
    }

    private static void CloseLog(ConsoleSession session)
    {
        lock (session.LogGate)
        {
            session.Log?.Dispose();
            session.Log = null;
        }
    }
}
