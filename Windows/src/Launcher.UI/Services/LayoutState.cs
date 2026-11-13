using Launcher.Core.Configuration;

namespace Launcher.UI.Services;

/// <summary>
/// Раскладка главной страницы в памяти и состояние редактора интерфейса (режим правки, выбранный элемент).
/// Сохранением в настройки занимается <see cref="ViewModels.HomeEditorViewModel"/>.
/// </summary>
internal static class LayoutState
{
    private static readonly ElementLayout Default = new();
    private static Dictionary<string, ElementLayout> _map = new(StringComparer.OrdinalIgnoreCase);

    public static bool Editing { get; private set; }
    public static string? Selected { get; private set; }

    /// <summary>Изменился один элемент (id) или вся раскладка (null).</summary>
    public static event Action<string?>? Changed;
    public static event Action? SelectionChanged;
    public static event Action? EditingChanged;

    public static IReadOnlyDictionary<string, ElementLayout> Snapshot => _map;

    /// <summary>Шаг сетки при перетаскивании и стрелках, пикселей; 0 — сетка выключена.</summary>
    public static int SnapStep { get; set; }

    public static event Action? GridChanged;

    /// <summary>Забывает отмену и повтор (при смене «пространства» раскладки старая история не подходит).</summary>
    public static void ClearHistory()
    {
        UndoStack.Clear();
        RedoStack.Clear();
        _lastPush = 0;
    }

    public static void SetSnap(int step)
    {
        SnapStep = step;
        GridChanged?.Invoke();
    }

    // ----- отмена и повтор -----

    private static readonly List<Dictionary<string, ElementLayout>> UndoStack = new();
    private static readonly List<Dictionary<string, ElementLayout>> RedoStack = new();
    private static long _lastPush;

    private static Dictionary<string, ElementLayout> Copy() => new(_map, StringComparer.OrdinalIgnoreCase);

    /// <summary>Запоминает состояние перед правкой. Серия правок подряд (ползунок, стрелки) считается одной.</summary>
    public static void Checkpoint(bool force = false)
    {
        var now = Environment.TickCount64;
        var burst = now - _lastPush < 700;
        _lastPush = now;
        if (burst && !force) return;
        UndoStack.Add(Copy());
        if (UndoStack.Count > 50) UndoStack.RemoveAt(0);
        RedoStack.Clear();
    }

    public static void Undo()
    {
        if (UndoStack.Count == 0) return;
        RedoStack.Add(Copy());
        _map = UndoStack[^1];
        UndoStack.RemoveAt(UndoStack.Count - 1);
        _lastPush = 0;
        Changed?.Invoke(null);
    }

    public static void Redo()
    {
        if (RedoStack.Count == 0) return;
        UndoStack.Add(Copy());
        _map = RedoStack[^1];
        RedoStack.RemoveAt(RedoStack.Count - 1);
        _lastPush = 0;
        Changed?.Invoke(null);
    }

    public static ElementLayout Get(string id) => _map.TryGetValue(id, out var layout) ? layout : Default;

    public static void Set(string id, ElementLayout layout)
    {
        layout = layout.Clamped();
        if (layout == Default) _map.Remove(id);
        else _map[id] = layout;
        Changed?.Invoke(id);
    }

    public static void Load(IReadOnlyDictionary<string, ElementLayout> map)
    {
        _map = map.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);
        Changed?.Invoke(null);
    }

    public static void SetEditing(bool on)
    {
        if (Editing == on) return;
        Editing = on;
        if (!on) Selected = null;
        EditingChanged?.Invoke();
        SelectionChanged?.Invoke();
    }

    public static void Select(string? id)
    {
        if (string.Equals(Selected, id, StringComparison.OrdinalIgnoreCase)) return;
        Selected = id;
        SelectionChanged?.Invoke();
    }
}
