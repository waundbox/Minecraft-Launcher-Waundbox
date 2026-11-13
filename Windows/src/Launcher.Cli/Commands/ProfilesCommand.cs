using Launcher.Core.Errors;
using Launcher.Core.Installation;
using Launcher.Core.Profiles;

namespace Launcher.Cli.Commands;

/// <summary>Команда "profiles": управление игровыми профилями.</summary>
public sealed class ProfilesCommand
{
    private readonly IProfileStore _store;
    private readonly MinecraftLayout _layout;
    private readonly TextWriter _out;

    public ProfilesCommand(IProfileStore store, MinecraftLayout layout, TextWriter output)
    {
        _store = store;
        _layout = layout;
        _out = output;
    }

    public Task<int> RunAsync(ProfilesCliOptions o, CancellationToken ct) => o.Action switch
    {
        ProfilesAction.List => ListAsync(ct),
        ProfilesAction.Show => ShowAsync(o.Name!, ct),
        ProfilesAction.Create => CreateAsync(o, ct),
        ProfilesAction.Edit => EditAsync(o, ct),
        ProfilesAction.Delete => DeleteAsync(o.Name!, ct),
        _ => throw new ArgumentOutOfRangeException(nameof(o))
    };

    private async Task<int> ListAsync(CancellationToken ct)
    {
        var profiles = await _store.ListAsync(ct);
        if (profiles.Count == 0)
        {
            _out.WriteLine("Профилей пока нет. Создайте: profiles create MyProfile --version 1.21.1");
            return 0;
        }

        _out.WriteLine($"{"Профиль",-24} {"Версия",-20} {"Память",-10} Установлена");
        foreach (var p in profiles)
            _out.WriteLine($"{p.Name,-24} {p.VersionId,-20} {p.MaxMemoryMb + " МБ",-10} {(IsInstalled(p.VersionId) ? "да" : "нет")}");
        return 0;
    }

    private async Task<int> ShowAsync(string name, CancellationToken ct)
    {
        var p = await Require(name, ct);
        _out.WriteLine($"Профиль:            {p.Name}");
        _out.WriteLine($"Версия Minecraft:   {p.VersionId} ({(IsInstalled(p.VersionId) ? "установлена" : "НЕ установлена — выполните install " + p.VersionId)})");
        _out.WriteLine($"Память (максимум):  {p.MaxMemoryMb} МБ");
        _out.WriteLine($"Память (начальная): {(p.MinMemoryMb is { } m ? m + " МБ" : "по умолчанию")}");
        _out.WriteLine($"Аргументы JVM:      {p.JvmArguments ?? "—"}");
        _out.WriteLine($"Аргументы игры:     {p.GameArguments ?? "—"}");
        _out.WriteLine($"Java:               {p.JavaPath ?? "автоматически"}");
        _out.WriteLine($"Папка игры:         {_layout.InstanceDirectory(p.Name)}");
        return 0;
    }

    private async Task<int> CreateAsync(ProfilesCliOptions o, CancellationToken ct)
    {
        if (await _store.FindAsync(o.Name!, ct) is not null)
            throw new ProfileException($"Профиль \"{o.Name}\" уже существует. Изменить его: profiles edit {o.Name} ...");

        var profile = new GameProfile(
            o.Name!, o.VersionId!,
            o.MaxMemoryMb ?? GameProfile.DefaultMaxMemoryMb,
            o.MinMemoryMb, o.JvmArguments, o.GameArguments, o.JavaPath);

        await _store.SaveAsync(profile, ct);
        _out.WriteLine($"Профиль \"{profile.Name}\" создан.");
        if (!IsInstalled(profile.VersionId))
            _out.WriteLine($"Версия {profile.VersionId} ещё не установлена. Выполните: install {profile.VersionId}");
        return 0;
    }

    private async Task<int> EditAsync(ProfilesCliOptions o, CancellationToken ct)
    {
        var p = await Require(o.Name!, ct);
        var updated = p with
        {
            VersionId = o.VersionId ?? p.VersionId,
            MaxMemoryMb = o.MaxMemoryMb ?? p.MaxMemoryMb,
            MinMemoryMb = o.MinMemorySet ? o.MinMemoryMb : p.MinMemoryMb,
            JvmArguments = o.JvmArgumentsSet ? o.JvmArguments : p.JvmArguments,
            GameArguments = o.GameArgumentsSet ? o.GameArguments : p.GameArguments,
            JavaPath = o.JavaPathSet ? o.JavaPath : p.JavaPath
        };

        await _store.SaveAsync(updated, ct);
        _out.WriteLine($"Профиль \"{updated.Name}\" обновлён.");
        return 0;
    }

    private async Task<int> DeleteAsync(string name, CancellationToken ct)
    {
        var p = await Require(name, ct);
        await _store.DeleteAsync(p.Name, ct);
        _out.WriteLine($"Профиль \"{p.Name}\" удалён. Файлы игры (сохранения, моды) остались в: {_layout.InstanceDirectory(p.Name)}");
        return 0;
    }

    private async Task<GameProfile> Require(string name, CancellationToken ct) =>
        await _store.FindAsync(name, ct)
        ?? throw new ProfileException($"Профиль \"{name}\" не найден. Список профилей: profiles list");

    private bool IsInstalled(string versionId)
    {
        try { return File.Exists(_layout.VersionJsonPath(versionId)); }
        catch (LauncherException) { return false; }
    }
}
