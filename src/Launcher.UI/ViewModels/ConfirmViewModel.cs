using Launcher.UI.Mvvm;
using Launcher.UI.Services;

namespace Launcher.UI.ViewModels;

/// <summary>Диалог подтверждения («Удалить профиль?»).</summary>
internal sealed class ConfirmViewModel : ObservableObject
{
    private readonly DialogService _dialogs;
    private readonly TaskCompletionSource<bool> _result;

    public ConfirmViewModel(DialogService dialogs, string title, string message, string okText, bool isDanger,
        TaskCompletionSource<bool> result)
    {
        _dialogs = dialogs;
        _result = result;
        Title = title;
        Message = message;
        OkText = okText;
        IsDanger = isDanger;
        OkCommand = new RelayCommand(() => Finish(true));
        CancelCommand = new RelayCommand(() => Finish(false));
    }

    public string Title { get; }
    public string Message { get; }
    public string OkText { get; }
    public bool IsDanger { get; }
    public bool IsNotDanger => !IsDanger;

    public RelayCommand OkCommand { get; }
    public RelayCommand CancelCommand { get; }

    private void Finish(bool value)
    {
        _dialogs.Close();
        _result.TrySetResult(value);
    }
}
