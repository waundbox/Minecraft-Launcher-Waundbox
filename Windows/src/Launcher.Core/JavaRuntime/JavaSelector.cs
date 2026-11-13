namespace Launcher.Core.JavaRuntime;

/// <summary>Выбирает из найденных Java ту, что подходит версии Minecraft.</summary>
public static class JavaSelector
{
    /// <summary>
    /// Правила: Java старее требуемой не подходит. Точное совпадение основной версии лучше всего,
    /// иначе берётся ближайшая более новая (с предупреждением). 64-битная предпочтительнее 32-битной,
    /// при равенстве сохраняется порядок обнаружения.
    /// </summary>
    /// <returns>Подходящая Java или null.</returns>
    public static JavaResolution? Select(IEnumerable<JavaInstallation> installations, int requiredMajor)
    {
        var suitable = installations.Where(i => i.MajorVersion >= requiredMajor).ToList();

        var exact = suitable.Where(i => i.MajorVersion == requiredMajor).OrderByDescending(i => i.Is64Bit).FirstOrDefault();
        if (exact is not null) return new JavaResolution(exact, null);

        var newer = suitable.OrderBy(i => i.MajorVersion).ThenByDescending(i => i.Is64Bit).FirstOrDefault();
        return newer is null ? null : new JavaResolution(newer, NewerWarning(requiredMajor, newer.MajorVersion));
    }

    public static string? NewerWarning(int requiredMajor, int actualMajor) =>
        actualMajor == requiredMajor
            ? null
            : $"Рекомендуется Java {requiredMajor}, используется Java {actualMajor}. Игра, скорее всего, запустится, но возможны проблемы.";
}
