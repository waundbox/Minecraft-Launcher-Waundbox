using Launcher.Core.Errors;

namespace Launcher.Core.Authentication;

/// <summary>Игрок Ely.by: идентификатор без дефисов и ник.</summary>
public sealed record ElyProfile(string Uuid, string Name);

/// <summary>Ответ сервера авторизации Ely.by на вход или обновление токена.</summary>
public sealed record ElySession(string AccessToken, string ClientToken, ElyProfile Profile);

/// <summary>Что лаунчер хранит между запусками (только в зашифрованном виде). Пароль не хранится никогда.</summary>
public sealed record StoredElyAccount(string Username, string Uuid, string AccessToken, string ClientToken);

/// <summary>Состояние входа без секретов — для показа пользователю.</summary>
public sealed record ElyStatus(bool SignedIn, string? Username, string? Uuid);

/// <summary>Аккаунт защищён двухфакторной аутентификацией: нужен код из приложения-аутентификатора.</summary>
public sealed class ElyTwoFactorRequiredException : AuthException
{
    public ElyTwoFactorRequiredException()
        : base("Аккаунт Ely.by защищён двухфакторной аутентификацией. Введите код из приложения-аутентификатора и повторите вход.") { }
}

/// <summary>Сервер Ely.by отклонил токен (он устарел или отозван): нужен вход заново.</summary>
public sealed class ElyTokenRejectedException : AuthException
{
    public ElyTokenRejectedException() : base("Сессия Ely.by больше не действует. Войдите в аккаунт заново.") { }
}

/// <summary>Сервер авторизации Ely.by (Yggdrasil-совместимый): https://docs.ely.by/en/minecraft-auth.html</summary>
public interface IElyAuthClient
{
    /// <param name="usernameOrEmail">Ник или e-mail.</param>
    /// <param name="passwordWithCode">Пароль; при двухфакторной защите — «пароль:код».</param>
    /// <exception cref="AuthException">Неверные данные, нужен код (<see cref="ElyTwoFactorRequiredException"/>) или сервис недоступен.</exception>
    Task<ElySession> AuthenticateAsync(string usernameOrEmail, string passwordWithCode, string clientToken, CancellationToken ct);

    /// <exception cref="ElyTokenRejectedException">Токен не принят.</exception>
    Task<ElySession> RefreshAsync(string accessToken, string clientToken, CancellationToken ct);

    /// <summary>true — токен действителен. Сетевые сбои выбрасываются как исключение.</summary>
    Task<bool> ValidateAsync(string accessToken, CancellationToken ct);

    /// <summary>Отзывает токен на сервере. Если токена уже нет, ошибкой это не считается.</summary>
    Task InvalidateAsync(string accessToken, string clientToken, CancellationToken ct);
}

/// <summary>Защищённое хранилище входа Ely.by. Реализация обязана шифровать токены.</summary>
public interface IElyAccountStore
{
    Task<StoredElyAccount?> LoadAsync(CancellationToken ct);
    Task SaveAsync(StoredElyAccount account, CancellationToken ct);
    Task DeleteAsync(CancellationToken ct);
}

/// <summary>Где лежит authlib-injector: библиотека, которая подключает игру к стороннему серверу авторизации и скинов.</summary>
public interface IAuthlibInjectorProvider
{
    /// <summary>Путь к jar-файлу (скачивает и проверяет контрольную сумму при необходимости).</summary>
    /// <exception cref="AuthException">Файла нет и скачать его не удалось.</exception>
    Task<string> GetJarPathAsync(CancellationToken ct);
}
