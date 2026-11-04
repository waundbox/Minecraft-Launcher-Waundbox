using Launcher.Core.Installation;
using Launcher.Core.Logging;

namespace Launcher.Core.Skins;

public enum SkinApplyResult
{
    /// <summary>Скин подключён к профилю.</summary>
    Applied,
    /// <summary>Скин отключён (его нет или вход выполнен в настоящий аккаунт).</summary>
    Removed,
    /// <summary>Версия игры слишком старая: подмена не поддерживается.</summary>
    Unsupported,
    /// <summary>Не удалось записать файлы профиля; запуск игры это не блокирует.</summary>
    Failed
}

/// <summary>Хранит скин пользователя и подключает его к профилям как ресурспак (для одиночной игры и режима разработки).</summary>
public sealed class SkinService
{
    private readonly MinecraftLayout _layout;
    private readonly string _skinFile;
    private readonly ILauncherLogger _logger;

    public SkinService(MinecraftLayout layout, string skinFile, ILauncherLogger? logger = null)
    {
        _layout = layout;
        _skinFile = skinFile;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public bool HasSkin => File.Exists(_skinFile);

    public async Task<byte[]?> ReadSkinAsync(CancellationToken ct) =>
        HasSkin ? await File.ReadAllBytesAsync(_skinFile, ct).ConfigureAwait(false) : null;

    /// <exception cref="Errors.LauncherException">Файл не подходит как скин.</exception>
    public async Task SetSkinAsync(byte[] png, CancellationToken ct)
    {
        SkinImage.Validate(png);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_skinFile))!);
        var temp = _skinFile + ".tmp";
        await File.WriteAllBytesAsync(temp, png, ct).ConfigureAwait(false);
        File.Move(temp, _skinFile, overwrite: true);
    }

    public void RemoveSkin()
    {
        if (File.Exists(_skinFile)) File.Delete(_skinFile);
    }

    /// <param name="enable">true — подключить скин (нужен для режима разработки); false — убрать из профиля.</param>
    public async Task<SkinApplyResult> ApplyAsync(string profileName, string gameVersion, bool enable, CancellationToken ct)
    {
        try
        {
            var instance = _layout.InstanceDirectory(profileName);
            var packPath = Path.Combine(instance, "resourcepacks", SkinPackBuilder.PackFileName);
            var optionsPath = Path.Combine(instance, "options.txt");
            var entry = OptionsFile.PackEntry(SkinPackBuilder.PackFileName);

            if (!enable || !HasSkin)
            {
                Remove(packPath, optionsPath, entry);
                return SkinApplyResult.Removed;
            }

            if (!ResourcePackFormat.IsSupported(gameVersion)) return SkinApplyResult.Unsupported;

            var png = await File.ReadAllBytesAsync(_skinFile, ct).ConfigureAwait(false);
            var zip = SkinPackBuilder.Build(png, ResourcePackFormat.For(gameVersion));

            Directory.CreateDirectory(Path.GetDirectoryName(packPath)!);
            var temp = packPath + ".tmp";
            await File.WriteAllBytesAsync(temp, zip, ct).ConfigureAwait(false);
            File.Move(temp, packPath, overwrite: true);

            var current = File.Exists(optionsPath) ? await File.ReadAllTextAsync(optionsPath, ct).ConfigureAwait(false) : null;
            if (!OptionsFile.TryEnable(current, entry, out var updated))
            {
                _logger.Warn("Не удалось разобрать строку resourcePacks в options.txt: скин нужно включить в игре вручную.");
                return SkinApplyResult.Failed;
            }
            if (!string.Equals(current, updated, StringComparison.Ordinal))
                await File.WriteAllTextAsync(optionsPath, updated, ct).ConfigureAwait(false);

            return SkinApplyResult.Applied;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Не удалось подключить скин к профилю.", ex);
            return SkinApplyResult.Failed;
        }
    }

    private static void Remove(string packPath, string optionsPath, string entry)
    {
        if (File.Exists(packPath)) File.Delete(packPath);
        if (!File.Exists(optionsPath)) return;

        var text = File.ReadAllText(optionsPath);
        if (OptionsFile.TryDisable(text, entry, out var updated) && !string.Equals(text, updated, StringComparison.Ordinal))
            File.WriteAllText(optionsPath, updated);
    }
}
