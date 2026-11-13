using System.Text;
using Launcher.Core.Errors;

namespace Launcher.Core.GameLaunch;

/// <summary>Делит строку аргументов на отдельные аргументы; значения с пробелами берутся в двойные кавычки.</summary>
public static class CommandLineSplitter
{
    /// <exception cref="LauncherException">В строке не закрыта кавычка.</exception>
    public static IReadOnlyList<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;

        foreach (var c in text)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (inQuotes) throw new LauncherException($"В строке аргументов не закрыта кавычка: {text}");
        if (hasToken) result.Add(current.ToString());
        return result;
    }
}
