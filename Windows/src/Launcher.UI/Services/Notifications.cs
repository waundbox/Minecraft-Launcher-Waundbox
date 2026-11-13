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

    private object? _locked;

    /// <summary>Окно заблокировано (обязательное обновление): его нельзя закрыть, другие окна не показываются, страницы под ним недоступны.</summary>
    public bool IsLocked => _locked is not null;

    public bool IsUnlocked => _locked is null;

    public void Show(object dialog, bool wide = false)
    {
        if (_locked is not null && !ReferenceEquals(dialog, _locked)) return;
        IsWide = wide;
        Current = dialog;
    }

    /// <summary>Показывает окно, которое нельзя закрыть, пока не вызван <see cref="Unlock"/>.</summary>
    public void ShowLocked(object dialog)
    {
        _locked = dialog;
        IsWide = false;
        Current = dialog;
        NotifyLock();
    }

    /// <summary>Делает уже открытое окно незакрываемым.</summary>
    public void Lock()
    {
        if (_current is null || ReferenceEquals(_locked, _current)) return;
        _locked = _current;
        NotifyLock();
    }

    public void Unlock()
    {
        if (_locked is null) return;
        _locked = null;
        NotifyLock();
    }

    private void NotifyLock()
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsUnlocked));
    }

    public void Close()
    {
        if (_locked is not null) return;
        Current = null;
    }

    public Task<bool> ConfirmAsync(string title, string message, string okText, bool danger = false)
    {
        if (_locked is not null) return Task.FromResult(false); // пока висит обязательное окно, другие не показываются
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Show(new ViewModels.ConfirmViewModel(this, title, message, okText, danger, tcs));
        return tcs.Task;
    }
}
