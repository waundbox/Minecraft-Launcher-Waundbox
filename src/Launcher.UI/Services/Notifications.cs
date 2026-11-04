using System.Collections.ObjectModel;
using System.Windows;
using Launcher.UI.Mvvm;

namespace Launcher.UI.Services;

public enum ToastKind { Info, Success, Warning, Error }

public sealed class ToastItem
{
    public ToastItem(string text, ToastKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }
    public ToastKind Kind { get; }

    public string Glyph => Kind switch
    {
        ToastKind.Success => "",
        ToastKind.Warning => "",
        ToastKind.Error => "",
        _ => ""
    };
}

/// <summary>Короткие всплывающие сообщения внизу окна. Исчезают сами.</summary>
public sealed class ToastService
{
    private const int MaxVisible = 4;

    public ObservableCollection<ToastItem> Items { get; } = new();

    public void Show(string text, ToastKind kind = ToastKind.Info)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Show(text, kind));
            return;
        }

        var item = new ToastItem(text, kind);
        Items.Add(item);
        while (Items.Count > MaxVisible) Items.RemoveAt(0);

        var seconds = kind == ToastKind.Error ? 8 : 4.5;
        _ = Task.Delay(TimeSpan.FromSeconds(seconds)).ContinueWith(
            _ => dispatcher.BeginInvoke(() => Items.Remove(item)), TaskScheduler.Default);
    }
}

/// <summary>Окно-диалог поверх страниц. Одновременно показывается один диалог.</summary>
public sealed class DialogService : ObservableObject
{
    private object? _current;

    public object? Current
    {
        get => _current;
        private set
        {
            if (Set(ref _current, value)) OnPropertyChanged(nameof(IsOpen));
        }
    }

    public bool IsOpen => _current is not null;

    private bool _isWide;

    /// <summary>Широкое окно (для списков вроде каталога модов).</summary>
    public bool IsWide
    {
        get => _isWide;
        private set => Set(ref _isWide, value);
    }

    public void Show(object dialog, bool wide = false)
    {
        IsWide = wide;
        Current = dialog;
    }

    public void Close() => Current = null;

    public Task<bool> ConfirmAsync(string title, string message, string okText, bool danger = false)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Show(new ViewModels.ConfirmViewModel(this, title, message, okText, danger, tcs));
        return tcs.Task;
    }
}
