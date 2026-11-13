using Launcher.Core.Authentication;
using Launcher.Core.Configuration;

namespace Launcher.Cli.Commands;

/// <summary>Команда "auth": настройка и вход в аккаунт Microsoft.</summary>
public sealed class AuthCommand
{
    private readonly AccountService _accounts;
    private readonly ISettingsStore _settings;
    private readonly TextWriter _out;

    public AuthCommand(AccountService accounts, ISettingsStore settings, TextWriter output)
    {
        _accounts = accounts;
        _settings = settings;
        _out = output;
    }

    public Task<int> RunAsync(AuthCliOptions o, CancellationToken ct) => o.Action switch
    {
        AuthAction.Setup => SetupAsync(o.ClientId!, ct),
        AuthAction.Login => LoginAsync(ct),
        AuthAction.Status => StatusAsync(ct),
        AuthAction.Logout => LogoutAsync(ct),
        _ => throw new ArgumentOutOfRangeException(nameof(o))
    };

    private async Task<int> SetupAsync(string clientId, CancellationToken ct)
    {
        var settings = await _settings.LoadAsync(ct);
        await _settings.SaveAsync(settings with { MicrosoftClientId = clientId }, ct);
        _out.WriteLine("Client id сохранён (это не секрет). Теперь выполните: auth login");
        return 0;
    }

    private async Task<int> LoginAsync(CancellationToken ct)
    {
        var account = await _accounts.LoginAsync(code =>
        {
            _out.WriteLine("Для входа:");
            _out.WriteLine($"  1. Откройте в браузере: {code.VerificationUri}");
            _out.WriteLine($"  2. Введите код:         {code.UserCode}");
            _out.WriteLine("  3. Войдите в свой аккаунт Microsoft на странице Microsoft (пароль лаунчеру не нужен).");
            _out.WriteLine();
            _out.WriteLine($"Жду подтверждения (код действует {code.ExpiresInSeconds / 60} мин). Отмена: Ctrl+C.");
        }, ct);

        _out.WriteLine();
        _out.WriteLine($"Вход выполнен: {account.Username}. Теперь «launch» использует этот аккаунт.");
        return 0;
    }

    private async Task<int> StatusAsync(CancellationToken ct)
    {
        var status = await _accounts.GetStatusAsync(ct);
        _out.WriteLine($"Client id приложения Azure: {(status.ClientIdConfigured ? "указан" : "не указан (auth setup <client-id>)")}");
        _out.WriteLine(status.SignedIn
            ? $"Вход выполнен: {status.Username} ({status.Uuid})"
            : "Вход не выполнен (auth login). Без входа игра запускается только в development-режиме.");
        return 0;
    }

    private async Task<int> LogoutAsync(CancellationToken ct)
    {
        await _accounts.LogoutAsync(ct);
        _out.WriteLine("Вы вышли: сохранённые токены удалены с этого компьютера.");
        return 0;
    }
}
