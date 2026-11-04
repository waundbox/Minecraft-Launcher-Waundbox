using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Core.Authentication;

/// <summary>
/// Вход через Ely.by. Пароль передаётся серверу один раз при входе и нигде не сохраняется;
/// на диске (в зашифрованном виде) остаются только токены. Токены не попадают в журнал.
/// </summary>
public sealed class ElyAccountService
{
    public const string UserType = "mojang";
    /// <summary>Значение для authlib-injector: короткое имя сервера Ely.by.</summary>
    public const string AuthlibServer = "ely.by";

    private readonly IElyAuthClient _client;
    private readonly IElyAccountStore _store;
    private readonly ILauncherLogger _logger;

    public ElyAccountService(IElyAuthClient client, IElyAccountStore store, ILauncherLogger? logger = null)
    {
        _client = client;
        _store = store;
        _logger = logger ?? NullLauncherLogger.Instance;
    }

    public async Task<ElyStatus> GetStatusAsync(CancellationToken ct)
    {
        var stored = await _store.LoadAsync(ct).ConfigureAwait(false);
        return stored is null ? new ElyStatus(false, null, null) : new ElyStatus(true, stored.Username, stored.Uuid);
    }

    /// <param name="twoFactorCode">Код из приложения-аутентификатора, если аккаунт им защищён (иначе пусто).</param>
    /// <exception cref="ElyTwoFactorRequiredException">Нужен код.</exception>
    /// <exception cref="AuthException">Неверные данные или сервис недоступен.</exception>
    public async Task<GameAccount> LoginAsync(string usernameOrEmail, string password, string? twoFactorCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(usernameOrEmail)) throw new AuthException("Введите ник или e-mail от Ely.by.");
        if (string.IsNullOrEmpty(password)) throw new AuthException("Введите пароль от Ely.by.");

        var previous = await _store.LoadAsync(ct).ConfigureAwait(false);
        var clientToken = previous?.ClientToken ?? Guid.NewGuid().ToString("N");
        var secret = string.IsNullOrWhiteSpace(twoFactorCode) ? password : $"{password}:{twoFactorCode.Trim()}";

        var session = await _client.AuthenticateAsync(usernameOrEmail.Trim(), secret, clientToken, ct).ConfigureAwait(false);
        await SaveAsync(session, ct).ConfigureAwait(false);
        _logger.Info($"Вход через Ely.by выполнен: {session.Profile.Name}");
        return ToGameAccount(session.Profile, session.AccessToken);
    }

    /// <summary>
    /// Аккаунт для запуска игры или null, если вход не выполнен. Токен проверяется и при необходимости обновляется.
    /// Если сервер недоступен, используется сохранённый токен (игра покажет свою ошибку, если он устарел).
    /// </summary>
    /// <exception cref="AuthException">Сессия отозвана — нужен повторный вход.</exception>
    public async Task<GameAccount?> GetGameAccountAsync(CancellationToken ct)
    {
        var stored = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (stored is null) return null;

        try
        {
            if (await _client.ValidateAsync(stored.AccessToken, ct).ConfigureAwait(false))
                return ToGameAccount(stored);

            var session = await _client.RefreshAsync(stored.AccessToken, stored.ClientToken, ct).ConfigureAwait(false);
            await SaveAsync(session, ct).ConfigureAwait(false);
            return ToGameAccount(session.Profile, session.AccessToken);
        }
        catch (ElyTokenRejectedException)
        {
            await _store.DeleteAsync(ct).ConfigureAwait(false);
            throw;
        }
        catch (AuthException ex)
        {
            _logger.Warn("Ely.by недоступен, использую сохранённый токен.", ex);
            return ToGameAccount(stored);
        }
    }

    /// <summary>Выход: токен отзывается на сервере (если получится), локальные данные удаляются в любом случае.</summary>
    public async Task LogoutAsync(CancellationToken ct)
    {
        var stored = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (stored is not null)
        {
            try { await _client.InvalidateAsync(stored.AccessToken, stored.ClientToken, ct).ConfigureAwait(false); }
            catch (AuthException ex) { _logger.Warn("Не удалось отозвать токен Ely.by на сервере.", ex); }
        }
        await _store.DeleteAsync(ct).ConfigureAwait(false);
    }

    private Task SaveAsync(ElySession s, CancellationToken ct) =>
        _store.SaveAsync(new StoredElyAccount(s.Profile.Name, s.Profile.Uuid, s.AccessToken, s.ClientToken), ct);

    private static GameAccount ToGameAccount(StoredElyAccount a) => ToGameAccount(new ElyProfile(a.Uuid, a.Username), a.AccessToken);

    private static GameAccount ToGameAccount(ElyProfile profile, string accessToken) =>
        new(profile.Name, profile.Uuid, accessToken, UserType, IsDevelopment: false, AuthlibServer);
}
