using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Launcher.Core.Authentication;
using Launcher.Core.Errors;
using Launcher.Core.Skins;
using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Вариант выбора аккаунта для запуска (Id = null — автоматически).</summary>
public sealed record PreferredAccountChoice(string? Id, string Title);

/// <summary>Страница «Аккаунт»: вход через Microsoft (device code), вход через Ely.by и ник для локального режима.</summary>
internal sealed class AccountViewModel : PageViewModel
{
    private readonly UiContext _ctx;
    private CancellationTokenSource? _loginCts;
    private bool _isSignedIn;
    private string _username = "";
    private string _uuid = "";
    private bool _clientIdConfigured;
    private bool _isBusy;
    private string _userCode = "";
    private string _verificationUrl = "";
    private string _statusText = "";
    private string _devUsername = DevelopmentAccount.DefaultUsername;

    public AccountViewModel(UiContext ctx) : base("Аккаунт", "")
    {
        _ctx = ctx;
        LoginCommand = new AsyncCommand(LoginAsync, () => !_isBusy && _clientIdConfigured && !_isSignedIn);
        CancelLoginCommand = new RelayCommand(() => _loginCts?.Cancel(), () => _isBusy);
        LogoutCommand = new AsyncCommand(LogoutAsync, () => _isSignedIn && !_isBusy);
        OpenVerificationCommand = new RelayCommand(() => ShellHelper.Open(_verificationUrl), () => _verificationUrl.Length > 0);
        CopyCodeCommand = new RelayCommand(CopyCode, () => _userCode.Length > 0);
        SaveDevNameCommand = new AsyncCommand(SaveDevNameAsync);
        ChooseSkinCommand = new AsyncCommand(ChooseSkinAsync);
        RemoveSkinCommand = new AsyncCommand(RemoveSkinAsync, () => _hasSkin);
        UploadMicrosoftSkinCommand = new AsyncCommand(UploadMicrosoftSkinAsync, () => _isSignedIn && !_skinUploading);
        ShowAccountTabCommand = new RelayCommand(() => ShowSkinsTab = false);
        ShowSkinsTabCommand = new RelayCommand(() => { ShowSkinsTab = true; _ = _ctx.Skins.EnsureLoadedAsync(); });
        ChooseModelCommand = new AsyncCommand(ChooseModelAsync);
        ResetModelCommand = new AsyncCommand(ResetModelAsync, () => _customModel is not null);
        ExportTemplateCommand = new AsyncCommand(ExportTemplateAsync);
        ElyLoginCommand = new AsyncCommand(ElyLoginAsync, _ => !_elyBusy && !_elySignedIn);
        ElyLogoutCommand = new AsyncCommand(ElyLogoutAsync, _ => _elySignedIn && !_elyBusy);
        OpenElyCommand = new RelayCommand(() => ShellHelper.Open("https://ely.by"));
        PreferredChoices = new[]
        {
            new PreferredAccountChoice(null, "Автоматически"),
            new PreferredAccountChoice(AccountChoice.MicrosoftId, "Microsoft"),
            new PreferredAccountChoice(AccountChoice.ElyId, "Ely.by"),
            new PreferredAccountChoice(AccountChoice.DevelopmentId, "Локальный режим")
        };
        _selectedPreferred = PreferredChoices[0];
        _ctx.Settings.Changed += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            SyncDevName();
            // модель на главной перестраивается, только если изменилось то, от чего она зависит (а не цвета или FPS)
            var s = _ctx.Settings.Current;
            OnPropertyChanged(nameof(ModelPoseId));
            OnPropertyChanged(nameof(ModelOverlay));
            OnPropertyChanged(nameof(ShowModel3D));
            OnPropertyChanged(nameof(ModelSpin));
            OnPropertyChanged(nameof(ModelUseSkin));
            OnPropertyChanged(nameof(SelectedPose));
            var key = $"{s.PreferredAccount}|{s.SkinSlim}|{s.CustomModelPath}|{s.ModelUseSkin}";
            if (key == _modelKey) return;
            _modelKey = key;
            _ = RefreshModelAsync();
        });
    }

    // ----- Ely.by -----

    private bool _elySignedIn;
    private string _elyUsername = "";
    private string _elyUuid = "";
    private string _elyLogin = "";
    private string _elyCode = "";
    private bool _elyNeedsCode;
    private bool _elyBusy;
    private string _elyStatus = "";
    private PreferredAccountChoice _selectedPreferred;
    private bool _syncingPreferred;

    public AsyncCommand ElyLoginCommand { get; }
    public AsyncCommand ElyLogoutCommand { get; }
    public RelayCommand OpenElyCommand { get; }
    public IReadOnlyList<PreferredAccountChoice> PreferredChoices { get; }

    public bool ElySignedIn
    {
        get => _elySignedIn;
        private set
        {
            if (!Set(ref _elySignedIn, value)) return;
            OnPropertyChanged(nameof(ElySignedOut));
            OnAccountKindChanged();
        }
    }

    public bool ElySignedOut => !_elySignedIn;
    public string ElyUsername { get => _elyUsername; private set { if (Set(ref _elyUsername, value)) OnAccountKindChanged(); } }
    public string ElyUuid { get => _elyUuid; private set => Set(ref _elyUuid, value); }
    public string ElyLogin { get => _elyLogin; set => Set(ref _elyLogin, value ?? ""); }
    public string ElyCode { get => _elyCode; set => Set(ref _elyCode, value ?? ""); }
    public bool ElyNeedsCode { get => _elyNeedsCode; private set => Set(ref _elyNeedsCode, value); }
    public bool ElyBusy { get => _elyBusy; private set => Set(ref _elyBusy, value); }
    public string ElyStatus { get => _elyStatus; private set => Set(ref _elyStatus, value); }

    /// <summary>Какой аккаунт будет использован для запуска игры (с учётом выбора и того, куда выполнен вход).</summary>
    public AccountKind ActiveKind => AccountChoice.Resolve(_ctx.Settings.Current.PreferredAccount, _isSignedIn, _elySignedIn);

    public PreferredAccountChoice SelectedPreferred
    {
        get => _selectedPreferred;
        set
        {
            if (value is null || !Set(ref _selectedPreferred, value)) return;
            if (_syncingPreferred) return;
            var id = value.Id;
            _ = _ctx.Settings.UpdateAsync(s => s with { PreferredAccount = id });
        }
    }

    private void OnAccountKindChanged()
    {
        OnPropertyChanged(nameof(ActiveKind));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ModeText));
    }

    private async Task ElyLoginAsync(object? parameter)
    {
        var box = parameter as PasswordBox;
        var password = box?.Password ?? "";
        ElyBusy = true;
        ElyStatus = "Выполняю вход…";
        try
        {
            var account = await _ctx.Services.Ely.LoginAsync(_elyLogin, password, _elyCode, CancellationToken.None);
            box?.Clear(); // пароль после входа нигде не остаётся
            ElyCode = "";
            ElyNeedsCode = false;
            ElyStatus = "";
            await RefreshAsync();
            _ctx.Toasts.Show($"Вход через Ely.by выполнен: {account.Username}", ToastKind.Success);
        }
        catch (ElyTwoFactorRequiredException ex)
        {
            ElyNeedsCode = true;
            ElyStatus = ex.Message;
        }
        catch (LauncherException ex)
        {
            ElyStatus = ex.Message;
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            ElyBusy = false;
            CommandManagerRefresh();
        }
    }

    private async Task ElyLogoutAsync(object? _)
    {
        ElyBusy = true;
        try
        {
            await _ctx.Services.Ely.LogoutAsync(CancellationToken.None);
            await RefreshAsync();
            _ctx.Toasts.Show("Вы вышли из Ely.by.", ToastKind.Info);
        }
        finally
        {
            ElyBusy = false;
            CommandManagerRefresh();
        }
    }

    public AsyncCommand LoginCommand { get; }
    public RelayCommand CancelLoginCommand { get; }
    public AsyncCommand LogoutCommand { get; }
    public RelayCommand OpenVerificationCommand { get; }
    public RelayCommand CopyCodeCommand { get; }
    public AsyncCommand SaveDevNameCommand { get; }
    public AsyncCommand ChooseSkinCommand { get; }
    public AsyncCommand RemoveSkinCommand { get; }
    public AsyncCommand UploadMicrosoftSkinCommand { get; }
    public AsyncCommand ChooseModelCommand { get; }

    /// <summary>Сохраняет стандартную модель в файл .bbmodel для редактирования в Blockbench.</summary>
    public AsyncCommand ExportTemplateCommand { get; }

    // ----- Вкладки «Аккаунт» / «Скины» -----

    private bool _showSkinsTab;
    public RelayCommand ShowAccountTabCommand { get; }
    public RelayCommand ShowSkinsTabCommand { get; }
    public bool ShowSkinsTab { get => _showSkinsTab; private set { if (Set(ref _showSkinsTab, value)) OnPropertyChanged(nameof(ShowAccountTab)); } }
    public bool ShowAccountTab => !_showSkinsTab;
    public SkinsViewModel Skins => _ctx.Skins;

    private ImageSource _headImage = SkinImages.Head(null);

    /// <summary>Лицо текущего скина (8×8, увеличено) для значка аккаунта в верхней панели.</summary>
    public ImageSource HeadImage { get => _headImage; private set => Set(ref _headImage, value); }
    public AsyncCommand ResetModelCommand { get; }

    private ImageSource _microsoftHead = SkinImages.Head(null);
    private ImageSource _elyHead = SkinImages.Head(null);
    private ImageSource _devHead = SkinImages.Head(null);

    /// <summary>Лицо скина, который используется при входе через Microsoft (скин с серверов Mojang).</summary>
    public ImageSource MicrosoftHead { get => _microsoftHead; private set => Set(ref _microsoftHead, value); }

    /// <summary>Лицо скина Ely.by (скин с сайта ely.by).</summary>
    public ImageSource ElyHead { get => _elyHead; private set => Set(ref _elyHead, value); }

    /// <summary>Лицо скина в режиме разработки: выбранный в лаунчере файл или стандартный.</summary>
    public ImageSource DevHead { get => _devHead; private set => Set(ref _devHead, value); }

    private async Task RefreshHeadsAsync()
    {
        try
        {
            DevHead = SkinImages.Head(await _ctx.Services.Skins.ReadSkinAsync(CancellationToken.None));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Services.Logger.Warn("Не удалось прочитать скин для значка локального режима.", ex);
        }

        if (_isSignedIn)
        {
            try
            {
                if (await _ctx.Services.Accounts.GetSkinAsync(CancellationToken.None) is { } remote)
                    MicrosoftHead = SkinImages.Head(remote.Png);
            }
            catch (LauncherException ex)
            {
                _ctx.Services.Logger.Warn("Не удалось получить скин Microsoft для значка.", ex);
            }
        }

        if (_elySignedIn && ElyUsername.Length > 0)
        {
            var png = await DownloadElySkinAsync(ElyUsername, ElyUuid);
            ElyHead = SkinImages.Head(png); // null → стандартная голова (скина на ely.by нет или он недоступен)
        }
    }

    /// <summary>
    /// Скин с ely.by. Сначала система скинов по нику (адрес начинается с http: .NET не следует за перенаправлением с https на http),
    /// если не вышло — профиль на сервере авторизации по UUID (поле textures).
    /// </summary>
    private async Task<byte[]?> DownloadElySkinAsync(string username, string uuid)
    {
        try
        {
            return await _ctx.Services.GetBytesAsync($"http://skinsystem.ely.by/skins/{Uri.EscapeDataString(username)}.png", CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _ctx.Services.Logger.Warn("Скин ely.by по нику не получен, пробую через профиль.", ex);
        }

        if (string.IsNullOrWhiteSpace(uuid)) return null;
        try
        {
            var raw = await _ctx.Services.GetBytesAsync(
                $"https://authserver.ely.by/session/profile/{Uri.EscapeDataString(uuid.Replace("-", ""))}", CancellationToken.None);
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            foreach (var property in doc.RootElement.GetProperty("properties").EnumerateArray())
            {
                if (property.GetProperty("name").GetString() != "textures") continue;
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(property.GetProperty("value").GetString() ?? ""));
                using var textures = System.Text.Json.JsonDocument.Parse(json);
                var url = textures.RootElement.GetProperty("textures").GetProperty("SKIN").GetProperty("url").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.Host.EndsWith("ely.by", StringComparison.OrdinalIgnoreCase)) return null;
                var png = await _ctx.Services.GetBytesAsync(uri.ToString(), CancellationToken.None);
                return png.Length <= Launcher.Core.Skins.SkinImage.MaxBytes ? png : null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                                       or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            _ctx.Services.Logger.Warn("Не удалось получить скин ely.by для значка.", ex);
        }
        return null;
    }

    // ----- Модель на главной -----

    private byte[]? _modelSkinPng;
    private bool _modelSlim;
    private SkinModel? _customModel;
    private string _modelNote = "";
    private bool _skinUploading;
    private string _modelKey = "";

    /// <summary>Скин, который показан на модели: с серверов Mojang (вход Microsoft) или выбранный здесь файл; null — стандартная раскраска.</summary>
    public byte[]? ModelSkinPng
    {
        get => _modelSkinPng;
        private set
        {
            if (!Set(ref _modelSkinPng, value)) return;
            HeadImage = SkinImages.Head(value);
        }
    }

    /// <summary>Позы стандартной модели для списка в настройках.</summary>
    public IReadOnlyList<PlayerPose> PoseChoices => PlayerPoses.All;

    public string ModelPoseId => PlayerPoses.Find(_ctx.Settings.Current.ModelPose).Id;

    public PlayerPose SelectedPose
    {
        get => PlayerPoses.Find(_ctx.Settings.Current.ModelPose);
        set
        {
            if (value is null || value.Id == ModelPoseId) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { ModelPose = value.Id });
        }
    }

    /// <summary>Автоматическое вращение 3D-персонажа.</summary>
    public bool ModelSpin
    {
        get => _ctx.Settings.Current.ModelSpin;
        set
        {
            if (_ctx.Settings.Current.ModelSpin == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { ModelSpin = value });
        }
    }

    /// <summary>Показывать 3D-персонажа на главной.</summary>
    public bool ShowModel3D
    {
        get => _ctx.Settings.Current.ShowModel3D;
        set
        {
            if (_ctx.Settings.Current.ShowModel3D == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { ShowModel3D = value });
        }
    }

    /// <summary>Второй слой скина на стандартной модели.</summary>
    public bool ModelOverlay
    {
        get => _ctx.Settings.Current.ModelOverlay;
        set
        {
            if (_ctx.Settings.Current.ModelOverlay == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { ModelOverlay = value });
        }
    }

    /// <summary>Тонкие руки (Alex) у показанной модели.</summary>
    public bool ModelSlim { get => _modelSlim; private set => Set(ref _modelSlim, value); }

    /// <summary>Модель из Blockbench вместо стандартной; null — стандартная.</summary>
    public SkinModel? CustomModel { get => _customModel; private set { if (Set(ref _customModel, value)) OnPropertyChanged(nameof(HasCustomModel)); } }

    public bool HasCustomModel => _customModel is not null;
    public string ModelNote { get => _modelNote; private set => Set(ref _modelNote, value); }
    public bool SkinUploading { get => _skinUploading; private set { if (Set(ref _skinUploading, value)) CommandManagerRefresh(); } }

    /// <summary>Модель скина (Steve/Alex): хранится в настройках, используется для показа и при загрузке скина в Microsoft.</summary>
    public bool SkinSlim
    {
        get => _ctx.Settings.Current.SkinSlim;
        set
        {
            if (_ctx.Settings.Current.SkinSlim == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { SkinSlim = value });
            OnPropertyChanged(nameof(SkinSlim));
        }
    }

    private bool _hasSkin;
    private ImageSource? _skinHead;
    private ImageSource? _skinFull;

    public bool HasSkin { get => _hasSkin; private set { if (Set(ref _hasSkin, value)) OnPropertyChanged(nameof(NoSkin)); } }
    public bool NoSkin => !_hasSkin;

    /// <summary>Лицо персонажа (увеличенный фрагмент 8×8 из скина).</summary>
    public ImageSource? SkinHead { get => _skinHead; private set => Set(ref _skinHead, value); }

    /// <summary>Вся текстура скина.</summary>
    public ImageSource? SkinFull { get => _skinFull; private set => Set(ref _skinFull, value); }

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            if (!Set(ref _isSignedIn, value)) return;
            OnPropertyChanged(nameof(ShowSignedOut));
            OnAccountKindChanged();
        }
    }

    public bool ShowSignedOut => !_isSignedIn;
    public string Username { get => _username; private set { if (Set(ref _username, value)) { OnPropertyChanged(nameof(Initial)); OnPropertyChanged(nameof(DisplayName)); } } }
    public string Uuid { get => _uuid; private set => Set(ref _uuid, value); }

    public bool ClientIdConfigured
    {
        get => _clientIdConfigured;
        private set
        {
            if (Set(ref _clientIdConfigured, value)) OnPropertyChanged(nameof(ClientIdMissing));
        }
    }

    public bool ClientIdMissing => !_clientIdConfigured;

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(HasCode)); }
    }

    public string UserCode
    {
        get => _userCode;
        private set { if (Set(ref _userCode, value)) OnPropertyChanged(nameof(HasCode)); }
    }

    public string VerificationUrl { get => _verificationUrl; private set => Set(ref _verificationUrl, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool HasCode => _isBusy && _userCode.Length > 0;

    public string DevUsername { get => _devUsername; set => Set(ref _devUsername, value); }

    public string Initial => _username.Length > 0 ? _username[..1].ToUpperInvariant() : "?";

    /// <summary>Имя в шапке окна.</summary>
    public string DisplayName => ActiveKind switch
    {
        AccountKind.Microsoft => _username,
        AccountKind.ElyBy => _elyUsername,
        _ => string.IsNullOrWhiteSpace(_devUsername) ? DevelopmentAccount.DefaultUsername : _devUsername
    };

    public string ModeText => ActiveKind switch
    {
        AccountKind.Microsoft => "Microsoft",
        AccountKind.ElyBy => "Ely.by",
        _ => "локальный режим"
    };

    public async Task RefreshAsync()
    {
        var status = await _ctx.Services.Accounts.GetStatusAsync(CancellationToken.None);
        IsSignedIn = status.SignedIn;
        Username = status.Username ?? "";
        Uuid = status.Uuid ?? "";
        ClientIdConfigured = status.ClientIdConfigured;
        var ely = await _ctx.Services.Ely.GetStatusAsync(CancellationToken.None);
        ElyUsername = ely.Username ?? "";
        ElyUuid = ely.Uuid ?? "";
        ElySignedIn = ely.SignedIn;
        SyncDevName();
        await RefreshSkinAsync();
        await RefreshModelAsync();
        await RefreshHeadsAsync();
        CommandManagerRefresh();
    }

    /// <summary>Выбирает, какой скин и какую модель показывать на главной.</summary>
    public async Task RefreshModelAsync()
    {
        try
        {
            ModelSlim = _ctx.Settings.Current.SkinSlim;
            await LoadCustomModelAsync();

            byte[]? png = null;
            if (ActiveKind == AccountKind.Microsoft)
            {
                try
                {
                    if (await _ctx.Services.Accounts.GetSkinAsync(CancellationToken.None) is { } remote)
                    {
                        png = remote.Png;
                        ModelSlim = remote.Slim;
                    }
                }
                catch (LauncherException ex)
                {
                    _ctx.Services.Logger.Warn("Не удалось получить скин аккаунта Microsoft для показа на главной.", ex);
                }
            }
            var shown = png ?? await _ctx.Services.Skins.ReadSkinAsync(CancellationToken.None);
            // Руки (тонкие или обычные) определяются по самому скину: иначе при неверной галочке текстура рук «съезжает».
            if (png is null) ModelSlim = SkinImages.IsSlim(shown, _ctx.Settings.Current.SkinSlim);
            ModelSkinPng = shown;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Services.Logger.Warn("Не удалось прочитать скин для главной страницы.", ex);
        }
    }

    /// <summary>Модель из шаблона лаунчера (или при включённой настройке) показывается с выбранным скином, а не с текстурой из файла.</summary>
    private SkinModel PrepareModel(SkinModel model)
    {
        if (model.Name != BbModelWriter.TemplateName && !_ctx.Settings.Current.ModelUseSkin) return model;
        return model with { EmbeddedTexture = null, Notes = [.. model.Notes.Where(n => !n.Contains("нет встроенной текстуры")), "На модель наложен выбранный скин."] };
    }

    /// <summary>Накладывать выбранный скин на свою модель вместо текстуры из файла.</summary>
    public bool ModelUseSkin
    {
        get => _ctx.Settings.Current.ModelUseSkin;
        set
        {
            if (_ctx.Settings.Current.ModelUseSkin == value) return;
            _ = _ctx.Settings.UpdateAsync(s => s with { ModelUseSkin = value });
        }
    }

    private async Task LoadCustomModelAsync()
    {
        var path = _ctx.Settings.Current.CustomModelPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            CustomModel = null;
            ModelNote = "";
            return;
        }

        try
        {
            var model = PrepareModel(await Task.Run(() => BbModelReader.ReadFile(path)));
            CustomModel = model;
            ModelNote = $"Модель: {Path.GetFileName(path)}. " + string.Join(" ", model.Notes);
        }
        catch (LauncherException ex)
        {
            CustomModel = null;
            ModelNote = "Модель не загружена: " + ex.Message;
            _ctx.Services.Logger.Warn("Не удалось загрузить модель Blockbench.", ex);
        }
    }

    private async Task ExportTemplateAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить стандартную модель для Blockbench",
            Filter = "Модель Blockbench (*.bbmodel)|*.bbmodel",
            FileName = "waundbox-player.bbmodel",
            AddExtension = true,
            DefaultExt = ".bbmodel"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            // в файл кладётся текущий скин (или стандартная раскраска), чтобы модель в Blockbench сразу была с текстурой
            var png = ModelSkinPng ?? EncodePng(SkinImages.DefaultSkin());
            var json = BbModelWriter.Write(ModelSlim, png);
            await File.WriteAllTextAsync(dialog.FileName, json);
            ModelNote = $"Шаблон сохранён: {Path.GetFileName(dialog.FileName)}. Откройте его в Blockbench, измените и подключите кнопкой «Выбрать модель Blockbench…».";
            _ctx.Toasts.Show("Шаблон модели сохранён.", ToastKind.Success);
        }
        catch (Exception ex)
        {
            _ctx.Services.Logger.Warn("Не удалось сохранить шаблон модели.", ex);
            _ctx.Toasts.Show("Не удалось сохранить файл: " + ex.Message, ToastKind.Error);
        }
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private async Task ChooseModelAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите модель Blockbench",
            Filter = "Модель Blockbench (*.bbmodel)|*.bbmodel",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var model = PrepareModel(await Task.Run(() => BbModelReader.ReadFile(dialog.FileName))); // проверка до сохранения пути
            await _ctx.Settings.UpdateAsync(s => s with { CustomModelPath = dialog.FileName });
            CustomModel = model;
            ModelNote = $"Модель: {Path.GetFileName(dialog.FileName)}. " + string.Join(" ", model.Notes);
            _ctx.Toasts.Show("Модель подключена: она показана на главной.", ToastKind.Success);
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Warning);
        }
        CommandManagerRefresh();
    }

    private async Task ResetModelAsync()
    {
        await _ctx.Settings.UpdateAsync(s => s with { CustomModelPath = null });
        CustomModel = null;
        ModelNote = "";
        CommandManagerRefresh();
    }

    /// <summary>Загружает PNG на серверы Mojang: новый скин увидят и другие игроки.</summary>
    private async Task UploadMicrosoftSkinAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите скин для аккаунта Microsoft",
            Filter = "Скин Minecraft (PNG, 64×64)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        SkinUploading = true;
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            await _ctx.Services.Accounts.UploadSkinAsync(bytes, SkinSlim, CancellationToken.None);
            await RefreshModelAsync();
            _ctx.Toasts.Show("Скин аккаунта Microsoft изменён. На серверах он обновится через некоторое время.", ToastKind.Success);
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Warning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Toasts.Show("Не удалось прочитать файл: " + ex.Message, ToastKind.Error);
        }
        finally
        {
            SkinUploading = false;
        }
    }

    private async Task RefreshSkinAsync()
    {
        var bytes = await _ctx.Services.Skins.ReadSkinAsync(CancellationToken.None);
        if (bytes is null)
        {
            HasSkin = false;
            SkinHead = null;
            SkinFull = null;
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            var head = new CroppedBitmap(image, new Int32Rect(8, 8, 8, 8));
            head.Freeze();

            SkinFull = image;
            SkinHead = head;
            HasSkin = true;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException)
        {
            // файл скина повреждён: игра всё равно получит его как есть, но показать картинку не получилось
            _ctx.Services.Logger.Warn("Не удалось показать скин.", ex);
            SkinHead = null;
            SkinFull = null;
            HasSkin = true;
        }
    }

    private async Task ChooseSkinAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите скин Minecraft",
            Filter = "Скин Minecraft (PNG, 64×64)|*.png",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            await _ctx.Services.Skins.SetSkinAsync(bytes, CancellationToken.None);
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Warning);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Toasts.Show("Не удалось прочитать файл: " + ex.Message, ToastKind.Error);
            return;
        }

        await RefreshSkinAsync();
        await RefreshModelAsync();
        _ctx.Toasts.Show("Скин сохранён. Он подключится при следующем запуске игры.", ToastKind.Success);
        CommandManagerRefresh();
    }

    private async Task RemoveSkinAsync()
    {
        try
        {
            _ctx.Services.Skins.RemoveSkin();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ctx.Toasts.Show("Не удалось удалить скин: " + ex.Message, ToastKind.Error);
            return;
        }

        await RefreshSkinAsync();
        await RefreshModelAsync();
        _ctx.Toasts.Show("Скин убран. В игре снова будет стандартный (он отключится при следующем запуске).", ToastKind.Info);
        CommandManagerRefresh();
    }

    private void SyncDevName()
    {
        var saved = _ctx.Settings.Current.DevUsername;
        if (!string.IsNullOrWhiteSpace(saved) && saved != _devUsername) DevUsername = saved;

        _syncingPreferred = true;
        try
        {
            SelectedPreferred = PreferredChoices.FirstOrDefault(c => c.Id == _ctx.Settings.Current.PreferredAccount) ?? PreferredChoices[0];
        }
        finally
        {
            _syncingPreferred = false;
        }
        OnAccountKindChanged();
    }

    private async Task LoginAsync()
    {
        _loginCts = new CancellationTokenSource();
        IsBusy = true;
        UserCode = "";
        VerificationUrl = "";
        StatusText = "Запрашиваю код входа…";
        try
        {
            var account = await _ctx.Services.Accounts.LoginAsync(info =>
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    UserCode = info.UserCode;
                    VerificationUrl = info.VerificationUri.ToString();
                    StatusText = "Введите код на странице Microsoft. Окно ждёт, пока вы закончите вход…";
                    ShellHelper.Open(VerificationUrl);
                }), _loginCts.Token);

            await RefreshAsync();
            _ctx.Toasts.Show($"Вход выполнен: {account.Username}", ToastKind.Success);
            StatusText = "";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Вход отменён.";
        }
        catch (LauncherException ex)
        {
            StatusText = ex.Message;
            _ctx.Toasts.Show(ex.Message, ToastKind.Error);
        }
        finally
        {
            IsBusy = false;
            UserCode = "";
            VerificationUrl = "";
            _loginCts?.Dispose();
            _loginCts = null;
            CommandManagerRefresh();
        }
    }

    private async Task LogoutAsync()
    {
        await _ctx.Services.Accounts.LogoutAsync(CancellationToken.None);
        await RefreshAsync();
        _ctx.Toasts.Show("Вы вышли из аккаунта.", ToastKind.Info);
    }

    private async Task SaveDevNameAsync()
    {
        var name = _devUsername.Trim();
        try
        {
            DevelopmentAccount.Create(name); // только проверка допустимости ника
        }
        catch (LauncherException ex)
        {
            _ctx.Toasts.Show(ex.Message, ToastKind.Warning);
            return;
        }

        await _ctx.Settings.UpdateAsync(s => s with { DevUsername = name });
        DevUsername = name;
        _ctx.Toasts.Show($"Ник для локального режима: {name}", ToastKind.Success);
    }

    private void CopyCode()
    {
        try
        {
            Clipboard.SetText(_userCode);
            _ctx.Toasts.Show("Код скопирован.", ToastKind.Success);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _ctx.Toasts.Show("Буфер обмена занят. Скопируйте код вручную.", ToastKind.Warning);
        }
    }

    private static void CommandManagerRefresh() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
}
