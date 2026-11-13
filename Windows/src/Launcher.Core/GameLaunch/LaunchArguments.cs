namespace Launcher.Core.GameLaunch;

/// <summary>Условие применимости аргумента (поле "rules" в метаданных версии).</summary>
/// <param name="Features">Требуемые значения "features", например is_demo_user = true.</param>
public sealed record ArgumentRule(
    bool Allow, string? OsName, string? OsArch, string? OsVersion, IReadOnlyDictionary<string, bool> Features);

/// <summary>Один или несколько аргументов с условиями.</summary>
public sealed record ArgumentItem(IReadOnlyList<string> Values, IReadOnlyList<ArgumentRule> Rules);

/// <summary>Аргументы запуска версии: JVM и игры.</summary>
/// <param name="IsLegacy">true — старый формат (строка minecraftArguments), JVM-аргументы заданы лаунчером по умолчанию.</param>
public sealed record LaunchArguments(bool IsLegacy, IReadOnlyList<ArgumentItem> Jvm, IReadOnlyList<ArgumentItem> Game);
