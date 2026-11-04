namespace Launcher.Core.Errors;

/// <summary>Базовое исключение лаунчера. Сообщение пригодно для показа пользователю.</summary>
public class LauncherException : Exception
{
    public LauncherException(string message) : base(message) { }
    public LauncherException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Метаданные пришли, но их формат не соответствует ожидаемому.</summary>
public sealed class ManifestFormatException : LauncherException
{
    public ManifestFormatException(string message) : base(message) { }
    public ManifestFormatException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Манифест версий недоступен: нет сети/сервер недоступен и нет локальной копии.</summary>
public sealed class ManifestUnavailableException : LauncherException
{
    public ManifestUnavailableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Файл не удалось скачать (сервер ответил ошибкой, сеть оборвалась и т.п.).</summary>
public sealed class DownloadException : LauncherException
{
    public DownloadException(string message) : base(message) { }
    public DownloadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Скачанный файл не совпал с контрольной суммой или размером из метаданных.</summary>
public sealed class IntegrityException : LauncherException
{
    public IntegrityException(string message) : base(message) { }
}

/// <summary>Недостаточно места на диске.</summary>
public sealed class InsufficientDiskSpaceException : LauncherException
{
    public InsufficientDiskSpaceException(string message) : base(message) { }
    public InsufficientDiskSpaceException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>JSON версии или индекс ресурсов имеет неожиданный формат.</summary>
public sealed class VersionMetadataException : LauncherException
{
    public VersionMetadataException(string message) : base(message) { }
    public VersionMetadataException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Запрошенной версии нет в официальном списке.</summary>
public sealed class VersionNotFoundException : LauncherException
{
    public VersionNotFoundException(string message) : base(message) { }
}

/// <summary>Подходящая Java не найдена или указанная Java не работает.</summary>
public sealed class JavaNotFoundException : LauncherException
{
    public JavaNotFoundException(string message) : base(message) { }
}

/// <summary>Игру не удалось подготовить или запустить.</summary>
public sealed class LaunchException : LauncherException
{
    public LaunchException(string message) : base(message) { }
    public LaunchException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Ошибка в данных профиля или в хранилище профилей.</summary>
public sealed class ProfileException : LauncherException
{
    public ProfileException(string message) : base(message) { }
    public ProfileException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Сервис Quilt недоступен, вернул неожиданные данные или не поддерживает версию.</summary>
public sealed class QuiltException : LauncherException
{
    public QuiltException(string message) : base(message) { }
    public QuiltException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Сервис Fabric недоступен, вернул неожиданные данные или не поддерживает версию.</summary>
public sealed class FabricException : LauncherException
{
    public FabricException(string message) : base(message) { }
    public FabricException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Установка Forge не удалась: сервис недоступен, формат не поддерживается или установщик завершился с ошибкой.</summary>
public sealed class ForgeException : LauncherException
{
    public ForgeException(string message) : base(message) { }
    public ForgeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Установка NeoForge не удалась: сервис недоступен, версия не поддерживается или установщик завершился с ошибкой.</summary>
public sealed class NeoForgeException : LauncherException
{
    public NeoForgeException(string message) : base(message) { }
    public NeoForgeException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Вход в аккаунт Microsoft/Xbox/Minecraft не удался. Сообщение понятно пользователю и не содержит токенов.</summary>
public class AuthException : LauncherException
{
    public AuthException(string message) : base(message) { }
    public AuthException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Ошибка при работе с модами, ресурспаками и шейдерами (поиск, установка, удаление).</summary>
public sealed class ModException : LauncherException
{
    public ModException(string message) : base(message) { }
    public ModException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Ошибка установки собственного jar-файла.</summary>
public sealed class CustomJarException : LauncherException
{
    public CustomJarException(string message) : base(message) { }
    public CustomJarException(string message, Exception inner) : base(message, inner) { }
}
