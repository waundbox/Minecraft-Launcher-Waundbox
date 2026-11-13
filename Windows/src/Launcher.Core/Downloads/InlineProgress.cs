namespace Launcher.Core.Downloads;

/// <summary>
/// IProgress, который вызывает обработчик сразу в том же потоке
/// (стандартный Progress&lt;T&gt; переносит вызов в другой поток и может нарушать порядок).
/// </summary>
public sealed class InlineProgress<T> : IProgress<T>
{
    private readonly Action<T> _handler;
    public InlineProgress(Action<T> handler) => _handler = handler;
    public void Report(T value) => _handler(value);
}
