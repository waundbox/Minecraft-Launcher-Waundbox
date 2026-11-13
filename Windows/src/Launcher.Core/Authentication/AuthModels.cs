namespace Launcher.Core.Authentication;

/// <summary>Код для входа на другом устройстве/в браузере (device code flow).</summary>
/// <param name="DeviceCode">Секретный код устройства; показывать пользователю нельзя.</param>
/// <param name="UserCode">Короткий код, который пользователь вводит на странице входа.</param>
/// <param name="VerificationUri">Страница Microsoft, где вводится код.</param>
public sealed record DeviceCodeInfo(string DeviceCode, string UserCode, Uri VerificationUri, int ExpiresInSeconds, int IntervalSeconds);

/// <summary>Токены Microsoft. Хранить и логировать только через защищённое хранилище.</summary>
public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

public enum DevicePollStatus
{
    /// <summary>Пользователь ещё не завершил вход.</summary>
    Pending,
    /// <summary>Сервер просит опрашивать реже.</summary>
    SlowDown,
    Success,
    /// <summary>Пользователь отклонил вход.</summary>
    Declined,
    /// <summary>Время действия кода истекло.</summary>
    Expired
}

public sealed record DevicePollResult(DevicePollStatus Status, OAuthTokens? Tokens = null);

/// <summary>Токен Xbox Live или XSTS вместе с «user hash» (uhs).</summary>
public sealed record XboxToken(string Token, string UserHash);

/// <summary>Токен доступа к Minecraft Services.</summary>
public sealed record MinecraftSession(string AccessToken, DateTimeOffset ExpiresAt);

public sealed record MinecraftProfile(string Uuid, string Name);

/// <summary>Текущий скин аккаунта на серверах Mojang: адрес картинки и модель (true — тонкие руки, Alex).</summary>
public sealed record MinecraftSkinInfo(Uri Url, bool Slim);

/// <summary>То, что лаунчер хранит между запусками. Хранится только в защищённом виде (см. <see cref="IAccountStore"/>).</summary>
public sealed record StoredAccount(
    string Username, string Uuid, string RefreshToken, string? MinecraftToken, DateTimeOffset? MinecraftTokenExpiresAt);

/// <summary>Состояние входа без секретов — для показа пользователю.</summary>
public sealed record AccountStatus(bool SignedIn, string? Username, string? Uuid, bool ClientIdConfigured);
