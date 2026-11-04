namespace Launcher.Cli.Commands;

/// <summary>Разбор объёма памяти из аргументов командной строки: "4096", "4096M", "4G".</summary>
public static class MemorySize
{
    public static bool TryParseMegabytes(string text, out int megabytes)
    {
        megabytes = 0;
        text = text.Trim();
        if (text.Length == 0) return false;

        var multiplier = 1;
        var last = char.ToUpperInvariant(text[^1]);
        if (last == 'G') { multiplier = 1024; text = text[..^1]; }
        else if (last == 'M') { text = text[..^1]; }

        if (!int.TryParse(text, out var number) || number < 0 || number > int.MaxValue / 1024) return false;
        megabytes = number * multiplier;
        return true;
    }
}
