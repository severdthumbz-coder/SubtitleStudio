using SubtitleStudio.Infrastructure;
using SubtitleStudio.Services;

namespace SubtitleStudio.ViewModels;

/// <summary>
/// One API-key card in Settings. The plain key is only held while the edit box is open;
/// the view shows a mask once it is saved. Reused for every provider added later.
/// </summary>
public sealed class ApiKeyEntryViewModel : ObservableObject
{
    private readonly ApiKeyStore _store;
    private readonly IDialogService _dialogs;
    private readonly Action<string, StatusKind> _status;

    private ApiKeyState _state;
    private bool _isEditing;
    private string _pendingKey = string.Empty;
    private string _maskedValue = string.Empty;
    private string _message = string.Empty;

    public ApiKeyEntryViewModel(
        string providerId,
        string displayName,
        string description,
        ApiKeyStore store,
        IDialogService dialogs,
        Action<string, StatusKind> status)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        Description = description;
        _store = store;
        _dialogs = dialogs;
        _status = status;

        EditCommand = new RelayCommand(BeginEdit);
        SaveCommand = new RelayCommand(Save, () => !string.IsNullOrWhiteSpace(PendingKey));
        CancelCommand = new RelayCommand(CancelEdit);
        RemoveCommand = new RelayCommand(Remove, () => HasKey);

        Refresh();
    }

    public string ProviderId { get; }
    public string DisplayName { get; }
    public string Description { get; }

    public ApiKeyState State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(HasKey));
            OnPropertyChanged(nameof(IsSaved));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(EditButtonText));
        }
    }

    public bool HasKey => State != ApiKeyState.NotSet;
    public bool IsSaved => State == ApiKeyState.Saved;

    public string StateText => State switch
    {
        ApiKeyState.Saved => "Saved · encrypted",
        ApiKeyState.Unreadable => "Can't decrypt",
        _ => "Not set",
    };

    public string EditButtonText => State == ApiKeyState.Saved ? "Edit" : State == ApiKeyState.Unreadable ? "Re-enter" : "Add key";

    public bool IsEditing
    {
        get => _isEditing;
        private set => SetProperty(ref _isEditing, value);
    }

    /// <summary>Bound to the PasswordBox via PasswordBoxHelper. Cleared on save/cancel.</summary>
    public string PendingKey
    {
        get => _pendingKey;
        set => SetProperty(ref _pendingKey, value ?? string.Empty);
    }

    public string MaskedValue
    {
        get => _maskedValue;
        private set => SetProperty(ref _maskedValue, value);
    }

    public string Message
    {
        get => _message;
        private set => SetProperty(ref _message, value);
    }

    public RelayCommand EditCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public void Refresh()
    {
        State = _store.GetState(ProviderId, out var key);
        MaskedValue = State switch
        {
            ApiKeyState.Saved => SecureKeyService.Mask(key ?? string.Empty),
            ApiKeyState.Unreadable => "(stored key can't be read on this PC / Windows account)",
            _ => "No key saved",
        };
        Message = State == ApiKeyState.Unreadable
            ? "This key was encrypted for a different PC or Windows account (DPAPI). Enter it again to use it here."
            : string.Empty;
    }

    private void BeginEdit()
    {
        // "Unlock": pre-fill the (masked) box with the current key so it can be corrected in place.
        PendingKey = _store.GetState(ProviderId, out var key) == ApiKeyState.Saved ? key ?? string.Empty : string.Empty;
        IsEditing = true;
    }

    private void Save()
    {
        var key = PendingKey.Trim();
        if (key.Length == 0) return;

        _store.Set(ProviderId, key);
        PendingKey = string.Empty;
        IsEditing = false;
        Refresh();
        _status($"{DisplayName} key saved (encrypted with Windows DPAPI).", StatusKind.Success);
    }

    private void CancelEdit()
    {
        PendingKey = string.Empty;
        IsEditing = false;
    }

    private void Remove()
    {
        if (!_dialogs.Confirm("Remove API key", $"Remove the saved {DisplayName} key from subt_settings.json?")) return;
        _store.Remove(ProviderId);
        PendingKey = string.Empty;
        IsEditing = false;
        Refresh();
        _status($"{DisplayName} key removed.", StatusKind.Info);
    }
}
