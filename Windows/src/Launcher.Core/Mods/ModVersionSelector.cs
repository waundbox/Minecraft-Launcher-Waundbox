namespace Launcher.Core.Mods;

public static class ModVersionSelector
{
    /// <summary>Лучшая версия: свежий релиз, а если релизов нет — свежая бета, затем альфа.</summary>
    public static ModVersion? PickBest(IEnumerable<ModVersion> versions) =>
        versions
            .OrderBy(v => v.Channel)
            .ThenByDescending(v => v.Published)
            .FirstOrDefault();
}
