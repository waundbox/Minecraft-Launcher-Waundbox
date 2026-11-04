using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Infrastructure.Authentication;

/// <summary>
/// Хранит вход в файле, зашифрованном DPAPI Windows (ключ привязан к вашей учётной записи Windows).
/// Другой пользователь компьютера или другой компьютер расшифровать файл не сможет. Открытых токенов на диске нет.
/// </summary>
public sealed class DpapiAccountStore : IAccountStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Waundbox.Accounts.v1");

    private readonly string _path;
    private readonly ILauncherLogger _logger;

    public DpapiAccountStore(string path, ILauncherLogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public async Task<StoredAccount?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_path, ct).ConfigureAwait(false);
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<StoredAccount>(plain);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new AuthException("Безопасное хранение входа поддерживается только в Windows.", ex);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            _logger.Warn("Файл входа не удалось расшифровать (другой пользователь Windows или файл повреждён). Нужен повторный вход.", ex);
            return null;
        }
    }

    public async Task SaveAsync(StoredAccount account, CancellationToken ct)
    {
        try
        {
            var plain = JsonSerializer.SerializeToUtf8Bytes(account);
            var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var temp = _path + ".tmp";
            await File.WriteAllBytesAsync(temp, protectedBytes, ct).ConfigureAwait(false);
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
