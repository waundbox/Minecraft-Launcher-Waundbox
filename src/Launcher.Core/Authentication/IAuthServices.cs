namespace Launcher.Core.Authentication;

/// <summary>Вход Microsoft по протоколу OAuth 2.0 device code flow (без паролей в лаунчере и без client secret).</summary>
public interface IMicrosoftOAuthClient
{
    /// <exception cref="Errors.AuthException">Сервис недоступен или отклонил запрос.</exception>
    Task<DeviceCodeInfo> RequestDeviceCodeAsync(string clientId, CancellationToken ct);

    /// <summary>Один опрос: завершил ли пользователь вход.</summary>
    Task<DevicePollResult> PollAsync(string clientId, DeviceCodeInfo code, CancellationToken ct);

    /// <exception cref="Errors.AuthException">Токен обновления недействителен — нужен повторный вход.</exception>
    Task<OAuthTokens> RefreshAsync(string clientId, string refreshToken, CancellationToken ct);
}

/// <summary>Xbox Live и XSTS.</summary>
public interface IXboxClient
{
    Task<XboxToken> AuthenticateAsync(string microsoftAccessToken, CancellationToken ct);
    Task<XboxToken> AuthorizeMinecraftAsync(string xboxLiveToken, CancellationToken ct);
}

/// <summary>Minecraft Services (api.minecraftservices.com).</summary>
public interface IMinecraftServicesClient
{
    Task<MinecraftSession> LoginWithXboxAsync(string userHash, string xstsToken, CancellationToken ct);

    /// <summary>Профиль игрока; null — у аккаунта нет Minecraft: Java Edition.</summary>
    Task<MinecraftProfile?> GetProfileAsync(string minecraftAccessToken, CancellationToken ct);

    /// <summary>Активный скин аккаунта; null — своего скина нет (используется стандартный).</summary>
    Task<MinecraftSkinInfo?> GetSkinAsync(string minecraftAccessToken, CancellationToken ct);

    /// <summary>Загружает PNG скина на серверы Mojang (POST /minecraft/profile/skins, multipart: variant + file).</summary>
    /// <exception cref="Errors.AuthException">Токен отклонён, скин не принят или сервис недоступен.</exception>
    Task UploadSkinAsync(string minecraftAccessToken, byte[] png, bool slim, CancellationToken ct);

    /// <summary>Скачивает PNG скина (только https textures.minecraft.net, не больше 1 МБ).</summary>
    Task<byte[]> DownloadSkinAsync(Uri url, CancellationToken ct);
}

/// <summary>Защищённое хранилище входа. Реализация обязана шифровать токены.</summary>
public interface IAccountStore
{
    Task<StoredAccount?> LoadAsync(CancellationToken ct);
    Task SaveAsync(StoredAccount account, CancellationToken ct);
    Task DeleteAsync(CancellationToken ct);
}
