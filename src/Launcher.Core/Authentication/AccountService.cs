using Launcher.Core.Configuration;
using Launcher.Core.Errors;
using Launcher.Core.Logging;

namespace Launcher.Core.Authentication;

/// <summary>
/// Официальный вход: Microsoft (device code) → Xbox Live → XSTS → Minecraft Services → профиль.
/// Пароль пользователя лаунчер не видит: он вводится на странице Microsoft. Токены не попадают в журнал.
/// </summary>
public sealed class AccountService
{
    public const string UserType = "msa";

    /// <summary>Токен Minecraft обновляется заранее, если до конца его действия осталось меньше этого времени.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly IMicrosoftOAuthClient _oauth;
    private readonly IXboxClient _xbox;
    private readonly IMinecraftServicesClient _minecraft;
    private readonly IAccountStore _store;
    private readonly ISettingsStore _settings;
    private readonly ILauncherLogger _logger;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _embeddedClientId;

    public AccountService(
        IMicrosoftOAuthClient oauth, IXboxClient xbox, IMinecraftServicesClient minecraft,
        IAccountStore store, ISettingsStore settings, ILauncherLogger? logger = null,
        TimeProvider? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        string? embeddedClientId = null)
    {
        _embeddedClientId = embeddedClientId ?? LauncherAppRegistration.ClientId;
        _oauth = oauth;
        _xbox = xbox;
        _minecraft = minecraft;
        _store = store;
        _settings = settings;
        _logger = logger ?? NullLauncherLogger.Instance;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
    }

    /// <summary>Входит в аккаунт. <paramref name="showCode"/> получает адрес и код, которые нужно показать пользователю.</summary>
    /// <exception cref="AuthException">Вход не удался (причина в сообщении).</exception>
    public async Task<GameAccount> LoginAsync(Action<DeviceCodeInfo> showCode, CancellationToken ct)
    {
        var clientId = await RequireClientIdAsync(ct).ConfigureAwait(false);
        var code = await _oauth.RequestDeviceCodeAsync(clientId, ct).ConfigureAwait(false);
        showCode(code);

        var deadline = _clock.GetUtcNow().AddSeconds(code.ExpiresInSeconds);
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.IntervalSeconds));

        while (true)
        {
            await _delay(interval, ct).ConfigureAwait(false);
            if (_clock.GetUtcNow() >= deadline)
                throw new AuthException("Время действия кода истекло. Выполните вход заново: auth login");

            var poll = await _oauth.PollAsync(clientId, code, ct).ConfigureAwait(false);
            switch (poll.Status)
            {
                case DevicePollStatus.Pending:
                    continue;
                case DevicePollStatus.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    continue;
                case DevicePollStatus.Declined:
                    throw new AuthException("Вход отклонён на странице Microsoft.");
                case DevicePollStatus.Expired:
                    throw new AuthException("Время действия кода истекло. Выполните вход заново: auth login");
                case DevicePollStatus.Success:
                    return await CompleteAsync(poll.Tokens ?? throw new AuthException("Microsoft не вернула токены."), ct)
                        .ConfigureAwait(false);
            }
        }
    }

    /// <summary>Аккаунт для запуска игры: из хранилища, при необходимости с обновлением токенов. null — вход не выполнен.</summary>
    /// <exception cref="AuthException">Нужен повторный вход или сервисы недоступны.</exception>
    public async Task<GameAccount?> GetAccountAsync(CancellationToken ct)
    {
        var stored = await _store.LoadAsync(ct).ConfigureAwait(false);
        if (stored is null) return null;

        if (stored.MinecraftToken is not null && stored.MinecraftTokenExpiresAt is { } expires &&
            expires - _clock.GetUtcNow() > RefreshMargin)
            return ToGameAccount(stored.Username, stored.Uuid, stored.MinecraftToken);

        var clientId = await RequireClientIdAsync(ct).ConfigureAwait(false);
        _logger.Info("Токен Minecraft устарел, выполняю обновление.");
        var tokens = await _oauth.RefreshAsync(clientId, stored.RefreshToken, ct).ConfigureAwait(false);
        return await CompleteAsync(tokens, ct).ConfigureAwait(false);
    }

    public async Task<AccountStatus> GetStatusAsync(CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var stored = await _store.LoadAsync(ct).ConfigureAwait(false);
        return new AccountStatus(stored is not null, stored?.Username, stored?.Uuid,
            !string.IsNullOrWhiteSpace(settings.MicrosoftClientId) || !string.IsNullOrWhiteSpace(_embeddedClientId));
    }

    public Task LogoutAsync(CancellationToken ct) => _store.DeleteAsync(ct);

    /// <summary>Меняет скин аккаунта на серверах Mojang (его увидят и другие игроки).</summary>
    /// <exception cref="AuthException">Вход не выполнен, токен отклонён или скин не принят.</exception>
    public async Task UploadSkinAsync(byte[] png, bool slim, CancellationToken ct)
    {
        Skins.SkinImage.Validate(png);
        var account = await GetAccountAsync(ct).ConfigureAwait(false)
            ?? throw new AuthException("Сначала войдите через Microsoft.");
        await _minecraft.UploadSkinAsync(account.AccessToken, png, slim, ct).ConfigureAwait(false);
        _logger.Info("Скин аккаунта Microsoft обновлён.");
    }

    /// <summary>Текущий скин аккаунта Microsoft: картинка и модель. null — вход не выполнен или скина нет.</summary>
    public async Task<(byte[] Png, bool Slim)?> GetSkinAsync(CancellationToken ct)
    {
        var account = await GetAccountAsync(ct).ConfigureAwait(false);
        if (account is null) return null;
        var info = await _minecraft.GetSkinAsync(account.AccessToken, ct).ConfigureAwait(false);
        if (info is null) return null;
        return (await _minecraft.DownloadSkinAsync(info.Url, ct).ConfigureAwait(false), info.Slim);
    }

    private async Task<GameAccount> CompleteAsync(OAuthTokens tokens, CancellationToken ct)
    {
        var xbl = await _xbox.AuthenticateAsync(tokens.AccessToken, ct).ConfigureAwait(false);
        var xsts = await _xbox.AuthorizeMinecraftAsync(xbl.Token, ct).ConfigureAwait(false);
        var session = await _minecraft.LoginWithXboxAsync(xsts.UserHash, xsts.Token, ct).ConfigureAwait(false);

        // Владение игрой проверяется по наличию профиля Minecraft: Java Edition.
        var profile = await _minecraft.GetProfileAsync(session.AccessToken, ct).ConfigureAwait(false)
            ?? throw new AuthException(
                "У этой учётной записи Microsoft нет Minecraft: Java Edition (профиль не найден). " +
                "Если игра куплена, войдите тем аккаунтом, на котором она куплена. Игра по подписке Game Pass требует хотя бы одного входа в официальный лаунчер.");

        await _store.SaveAsync(new StoredAccount(
            profile.Name, profile.Uuid, tokens.RefreshToken, session.AccessToken, session.ExpiresAt), ct).ConfigureAwait(false);

        _logger.Info($"Выполнен вход: {profile.Name}.");
        return ToGameAccount(profile.Name, profile.Uuid, session.AccessToken);
    }

    /// <summary>Client id из настроек (если задан командой auth setup) важнее вшитого в лаунчер.</summary>
    private async Task<string> RequireClientIdAsync(CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct).ConfigureAwait(false);
        var clientId = !string.IsNullOrWhiteSpace(settings.MicrosoftClientId)
            ? settings.MicrosoftClientId
            : _embeddedClientId;

        if (string.IsNullOrWhiteSpace(clientId))
            throw new AuthException(
                "В этой сборке лаунчера не указан идентификатор приложения Microsoft (client id). " +
                "Его один раз прописывает автор лаунчера в LauncherAppRegistration.cs (см. README, «Авторизация»); " +
                "для проверки можно задать свой командой: auth setup <client-id>.");
        return clientId;
    }

    private static GameAccount ToGameAccount(string username, string uuid, string token) =>
        new(username, uuid.Replace("-", "", StringComparison.Ordinal), token, UserType, IsDevelopment: false);
}
