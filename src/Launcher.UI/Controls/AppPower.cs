namespace Launcher.UI.Controls;

/// <summary>Состояние «лаунчер свёрнут»: тяжёлые элементы (3D-модель, дым) освобождают память и не рисуют кадры.</summary>
public static class AppPower
{
    public static bool Suspended { get; private set; }

    public static event Action? Changed;

    public static void Set(bool suspended)
    {
        if (Suspended == suspended) return;
        Suspended = suspended;
        Changed?.Invoke();
    }
}
