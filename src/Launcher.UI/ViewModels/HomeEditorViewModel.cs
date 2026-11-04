using System.Windows;
using System.Windows.Threading;
using Launcher.Core.Configuration;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Элемент главной страницы, который можно править в редакторе.</summary>
public sealed record EditorElement(string Id, string Title);

/// <summary>
/// Редактор главной страницы: выбор элемента, его положение, масштаб, прозрачность, скругление, видимость.
/// Менять можно только внешний вид — что делает кнопка, не настраивается.
/// </summary>
internal sealed class HomeEditorViewModel : ObservableObject
{
    private readonly UiContext _ctx;
    private readonly DispatcherTimer _saveTimer;
    private string? _savedJson;

    public HomeEditorViewModel(UiContext ctx)
    {
        _ctx = ctx;
        Elements = TabCatalog.HomeElements.Select(e => new EditorElement(e.Id, e.Title)).ToArray();

        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveNow();

        LayoutState.Changed += _ =>
        {
            RaiseValues();
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        LayoutState.SelectionChanged += () =>
        {
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));
            RaiseValues();
        };
        LayoutState.EditingChanged += () =>
        {
            OnPropertyChanged(nameof(IsEditing));
            if (!LayoutState.Editing) SaveNow();
        };
        ctx.Settings.Changed += () => Application.Current.Dispatcher.BeginInvoke(Reload);

        StartCommand = new RelayCommand(() =>
        {
            OpenRequested?.Invoke();
            LayoutState.SetEditing(true);
        });
        DoneCommand = new RelayCommand(() => LayoutState.SetEditing(false));
        ResetElementCommand = new RelayCommand(() =>
        {
            if (LayoutState.Selected is not { } id) return;
            LayoutState.Checkpoint(force: true);
            LayoutState.Set(id, new ElementLayout());
        });
        ResetAllCommand = new RelayCommand(ResetAll);
        UndoCommand = new RelayCommand(LayoutState.Undo);
        RedoCommand = new RelayCommand(LayoutState.Redo);
        LayoutState.GridChanged += () => OnPropertyChanged(nameof(SnapToGrid));
    }

    /// <summary>Редактору нужна главная страница: окно переключает на неё.</summary>
    public event Action? OpenRequested;

    public IReadOnlyList<EditorElement> Elements { get; }
    public bool IsEditing => LayoutState.Editing;
    public bool HasSelection => LayoutState.Selected is not null;

    public RelayCommand StartCommand { get; }
    public RelayCommand DoneCommand { get; }
    public RelayCommand ResetElementCommand { get; }
    public RelayCommand ResetAllCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }

    /// <summary>Привязка к сетке 10 px при перетаскивании и стрелках.</summary>
    public bool SnapToGrid
    {
        get => LayoutState.SnapStep > 0;
        set => LayoutState.SetSnap(value ? 10 : 0);
    }

    /// <summary>Сдвиг выбранного элемента стрелками (шаг сетки или 1 px).</summary>
    public bool Nudge(double dx, double dy)
    {
        if (LayoutState.Selected is not { } id) return false;
        LayoutState.Checkpoint();
        var l = LayoutState.Get(id);
        LayoutState.Set(id, l with { X = l.X + dx, Y = l.Y + dy });
        return true;
    }

    public EditorElement? Selected
    {
        get => Elements.FirstOrDefault(e => string.Equals(e.Id, LayoutState.Selected, StringComparison.OrdinalIgnoreCase));
        set => LayoutState.Select(value?.Id);
    }

    private ElementLayout Current => LayoutState.Selected is { } id ? LayoutState.Get(id) : new ElementLayout();

    private void Edit(Func<ElementLayout, ElementLayout> change)
    {
        if (LayoutState.Selected is not { } id) return;
        LayoutState.Checkpoint();
        LayoutState.Set(id, change(LayoutState.Get(id)));
    }

    public int Z { get => Current.Z; set => Edit(l => l with { Z = value }); }
    public double X { get => Current.X; set => Edit(l => l with { X = value }); }
    public double Y { get => Current.Y; set => Edit(l => l with { Y = value }); }
    public double Scale { get => Current.Scale; set => Edit(l => l with { Scale = value }); }
    public double Opacity { get => Current.Opacity; set => Edit(l => l with { Opacity = value }); }
    public double Corner { get => Current.Corner ?? 14; set => Edit(l => l with { Corner = value }); }
    public bool ElementVisible { get => Current.Visible; set => Edit(l => l with { Visible = value }); }

    private void RaiseValues()
    {
        OnPropertyChanged(nameof(X));
        OnPropertyChanged(nameof(Z));
        OnPropertyChanged(nameof(Y));
        OnPropertyChanged(nameof(Scale));
        OnPropertyChanged(nameof(Opacity));
        OnPropertyChanged(nameof(Corner));
        OnPropertyChanged(nameof(ElementVisible));
    }

    public void ResetAll()
    {
        LayoutState.Checkpoint(force: true);
        LayoutState.Load(new Dictionary<string, ElementLayout>());
    }

    private void SaveNow()
    {
        _saveTimer.Stop();
        var json = HomeLayout.Serialize(LayoutState.Snapshot);
        if (json == _savedJson) return;
        _savedJson = json;
        _ = _ctx.Settings.UpdateAsync(s => s with { HomeLayout = json });
    }

    private void Reload()
    {
        var json = _ctx.Settings.Current.HomeLayout;
        if (json == _savedJson) return; // это наше же сохранение
        _savedJson = json;
        LayoutState.Load(HomeLayout.Parse(json));
    }
}
