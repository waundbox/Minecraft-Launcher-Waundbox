using Launcher.Core.Errors;
using Launcher.Core.Installation;

namespace Launcher.Core.Assets;

/// <summary>
/// Старые версии Minecraft ищут ресурсы по именам (например sounds/random/click.ogg), а не по хешам.
/// Для них ресурсы копируются из хранилища assets/objects в assets/virtual/{id} или в папку resources игры.
/// </summary>
public static class LegacyAssetsPreparer
{
    /// <returns>Папка, в которую разложены ресурсы, или null, если индекс современный и раскладка не нужна.</returns>
    public static async Task<string?> PrepareAsync(
        MinecraftLayout layout, string indexId, AssetIndex index, string gameDirectory, CancellationToken ct)
    {
        string target;
        if (index.IsVirtual) target = layout.VirtualAssetsDirectory(indexId);
        else if (index.MapToResources) target = Path.Combine(gameDirectory, "resources");
        else return null;

        foreach (var asset in index.Objects)
        {
            ct.ThrowIfCancellationRequested();
            var destination = MinecraftLayout.SafeCombine(target, asset.Name);
            var source = layout.AssetObjectPath(asset.Hash);

            if (File.Exists(destination) && new FileInfo(destination).Length == asset.Size) continue;
            if (!File.Exists(source))
                throw new LaunchException($"Не хватает файла ресурсов {asset.Name}. Выполните установку версии заново (команда install).");

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var from = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            await using var to = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await from.CopyToAsync(to, ct).ConfigureAwait(false);
        }
        return target;
    }
}
