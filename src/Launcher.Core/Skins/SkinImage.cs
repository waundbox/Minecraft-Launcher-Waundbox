using Launcher.Core.Errors;

namespace Launcher.Core.Skins;

/// <summary>Проверка файла скина без декодирования картинки: подпись PNG и размеры из заголовка.</summary>
public static class SkinImage
{
    /// <summary>Скины Minecraft — крошечные файлы; больший размер почти наверняка ошибка.</summary>
    public const int MaxBytes = 1_000_000;

    private static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <exception cref="LauncherException">Файл не похож на скин Minecraft.</exception>
    public static void Validate(byte[] data)
    {
        if (data.Length > MaxBytes)
            throw new LauncherException("Файл слишком большой для скина (больше 1 МБ). Нужен PNG 64×64 пикселя.");

        if (data.Length < 24 || !data.AsSpan(0, 8).SequenceEqual(PngSignature) ||
            data[12] != (byte)'I' || data[13] != (byte)'H' || data[14] != (byte)'D' || data[15] != (byte)'R')
            throw new LauncherException("Это не PNG-файл. Скин должен быть картинкой PNG 64×64 пикселя.");

        var width = ReadInt32BigEndian(data, 16);
        var height = ReadInt32BigEndian(data, 20);
        var ok = width == 64 && (height == 64 || height == 32);
        if (!ok)
            throw new LauncherException(
                $"Размер картинки {width}×{height}, а нужен 64×64 (или старый формат 64×32). Скины с другим размером Minecraft не принимает.");
    }

    private static int ReadInt32BigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
