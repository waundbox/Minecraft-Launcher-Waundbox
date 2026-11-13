using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Infrastructure.Authentication;

/// <summary>Токены Ely.by в файле, зашифрованном DPAPI (только ваша учётная запись Windows). Пароль не хранится.</summary>
public sealed class DpapiElyAccountStore : IElyAccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Waundbox.ElyAccount.v1");

    private readonly string _path;
    private readonly ILauncherLogger _logger;

    public DpapiElyAccountStore(string path, ILauncherLogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public async Task<StoredElyAccount?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredElyAccount>(plain);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new AuthException("Безопасное хранение входа поддерживается только в Windows.", ex);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.Warn("Файл входа Ely.by не удалось расшифровать. Нужен повторный вход.", ex);
            return null;
        }
    }

    public async Task SaveAsync(StoredElyAccount account, CancellationToken ct)
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(account);
            var bytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, _path, overwrite: true);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new AuthException("Безопасное хранение входа поддерживается только в Windows.", ex);
        }
    }

    public Task DeleteAsync(CancellationToken ct)
    {
        if (File.Exists(_path)) File.Delete(_path);
        return Task.CompletedTask;
    }
}
