using System.Text.RegularExpressions;

namespace Launcher.Core.GameLaunch;

/// <summary>Сборщик мусора Java, который можно выбрать для профиля.</summary>
/// <param name="Id">Короткий постоянный идентификатор (хранится в профиле).</param>
/// <param name="Flag">Аргумент JVM, включающий этот сборщик.</param>
/// <param name="MinJava">Минимальная версия Java, на которой сборщик поддерживается.</param>
public sealed record GcOption(string Id, string Title, string Description, string Flag, int MinJava);

/// <summary>Список сборщиков мусора и проверки вокруг них. Пустой выбор — «по умолчанию»: аргумент не добавляется.</summary>
public static class GarbageCollectors
{
    public static IReadOnlyList<GcOption> All { get; } =
    [
        new("g1", "G1", "Стандарт современных Java: ровная работа, подходит почти всем.", "-XX:+UseG1GC", 8),
        new("zgc", "ZGC", "Очень короткие паузы, нужна много памяти и современная Java (15+).", "-XX:+UseZGC", 15),
        new("shenandoah", "Shenandoah", "Короткие паузы. Есть не во всех сборках Java (в Oracle JDK нет), нужна Java 17+.", "-XX:+UseShenandoahGC", 17),
        new("parallel", "Parallel", "Упор на общую скорость, паузы могут быть заметнее.", "-XX:+UseParallelGC", 8),
        new("serial", "Serial", "Самый простой и лёгкий; для слабых компьютеров и небольшой памяти.", "-XX:+UseSerialGC", 8)
    ];

    private static readonly Regex GcFlag = new(@"^-XX:[+-]Use\w*GC$", RegexOptions.CultureInvariant);

    public static GcOption? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : All.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Есть ли среди аргументов JVM уже выбранный сборщик: два сборщика сразу Java не принимает.</summary>
    public static bool ContainsGcFlag(IEnumerable<string> jvmArguments) => jvmArguments.Any(a => GcFlag.IsMatch(a));
}
