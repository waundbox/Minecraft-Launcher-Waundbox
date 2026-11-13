using Launcher.Core.Profiles;
using Launcher.UI.Mvvm;

namespace Launcher.UI.ViewModels;

/// <summary>
/// Выбор памяти для игры: ползунок доходит только до оперативной памяти компьютера, а в поле можно вписать любое число (в МБ).
/// Если число больше ползунка, ползунок стоит на максимуме; как только его сдвинули, число заменяется положением ползунка.
/// </summary>
internal sealed class MemorySelection : ObservableObject
{
    public const int SliderMin = 1024;
    public const int Step = 512;
    private const long InputCap = 1_000_000_000;

    private long _megabytes;
    private string _text;

    public MemorySelection(int initialMegabytes)
    {
        PhysicalMb = DetectPhysicalMegabytes();
        SliderMax = Math.Max(2048, PhysicalMb / Step * Step);
        _megabytes = initialMegabytes;
        _text = initialMegabytes.ToString();
    }

    /// <summary>Оперативная память компьютера, МБ.</summary>
    public int PhysicalMb { get; }

    public int SliderMax { get; }

    /// <summary>Значение в поле ввода: только цифры.</summary>
    public string Text
    {
        get => _text;
        set
        {
            var digits = new string((value ?? "").Where(char.IsAsciiDigit).ToArray());
            if (!Set(ref _text, digits)) return;
            _megabytes = digits.Length == 0 ? 0 : long.TryParse(digits, out var parsed) ? Math.Min(parsed, InputCap) : InputCap;
            Changed();
        }
    }

    public double SliderValue
    {
        get => Math.Clamp(_megabytes, SliderMin, SliderMax);
        set
        {
            if (Math.Abs(value - SliderValue) < 0.5) return; // слайдер повторил своё же положение — число в поле не трогаем
            _megabytes = (long)Math.Round(value);
            _text = _megabytes.ToString();
            OnPropertyChanged(nameof(Text));
            Changed();
        }
    }

    /// <summary>Выбранная память в МБ (для сохранения в профиль).</summary>
    public int Value => (int)Math.Clamp(_megabytes, 0, int.MaxValue);

    public bool IsValid => _megabytes >= GameProfile.MinAllowedMemoryMb;

    public string Summary => IsValid ? Format.Memory(Value) : "—";

    public string Warning
    {
        get
        {
            if (!IsValid) return $"Минимум — {GameProfile.MinAllowedMemoryMb} МБ.";
            if (_megabytes > PhysicalMb)
                return $"В компьютере {Format.Memory(PhysicalMb)} памяти. С таким объёмом игра, скорее всего, не запустится.";
            if (PhysicalMb > 4096 && _megabytes > PhysicalMb - 1536)
                return "Это почти вся память компьютера: Windows и другие программы начнут тормозить.";
            return "";
        }
    }

    public bool HasWarning => Warning.Length > 0;

    private void Changed()
    {
        OnPropertyChanged(nameof(SliderValue));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(HasWarning));
    }

    internal static int DetectPhysicalMegabytes()
    {
        var mb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        return (int)Math.Clamp(mb, 0, int.MaxValue);
    }
}
