namespace Launcher.Core.Profiles;

/// <summary>Хранилище игровых профилей. Имена профилей не зависят от регистра.</summary>
public interface IProfileStore
{
    Task<IReadOnlyList<GameProfile>> ListAsync(CancellationToken cancellationToken);

    Task<GameProfile?> FindAsync(string name, CancellationToken cancellationToken);

    /// <summary>Создаёт профиль или заменяет существующий с тем же именем. Данные проверяются.</summary>
    Task SaveAsync(GameProfile profile, CancellationToken cancellationToken);

    /// <returns>true, если профиль существовал. Файлы игры профиля не удаляются.</returns>
    Task<bool> DeleteAsync(string name, CancellationToken cancellationToken);
}
