using KidShell.App.Localization;
using KidShell.Core.Configuration;
using KidShell.Core.Mvvm;
using KidShell.Core.Security;

namespace KidShell.App.ViewModels;

/// <summary>
/// The PIN gate in front of Parent Mode.
///
/// The view model never learns the expected PIN: it hands whatever was typed
/// to <see cref="IParentPinService"/> and only receives correct/incorrect
/// back.
/// </summary>
public sealed class PinOverlayViewModel : ObservableObject
{
    private readonly IParentPinService _pinService;
    private readonly IDeveloperOptions _developerOptions;

    private string _entry = string.Empty;
    private bool _hasError;

    public PinOverlayViewModel(IParentPinService pinService, IDeveloperOptions developerOptions)
    {
        _pinService = pinService;
        _developerOptions = developerOptions;

        AppendCommand = new RelayCommand(parameter => Append(parameter as string));
        BackspaceCommand = new RelayCommand(Backspace);
        ClearCommand = new RelayCommand(Clear);
        CancelCommand = new RelayCommand(() => Cancelled?.Invoke(this, EventArgs.Empty));
    }

    public RelayCommand AppendCommand { get; }

    public RelayCommand BackspaceCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand CancelCommand { get; }

    /// <summary>Raised when the correct PIN has been entered.</summary>
    public event EventHandler? Accepted;

    /// <summary>Raised when the parent backs out of the PIN screen.</summary>
    public event EventHandler? Cancelled;

    public int PinLength => _pinService.PinLength;

    public int EnteredCount => _entry.Length;

    /// <summary>Masked representation, one bullet per entered digit.</summary>
    public string MaskedEntry => new('•', _entry.Length);

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorMessage => Strings.Get("Pin.Wrong");

    /// <summary>
    /// True while the built-in development PIN is still what unlocks Parent
    /// Mode. The UI says so out loud rather than hiding it.
    /// </summary>
    public bool ShowsDeveloperHint => _developerOptions.DeveloperMode && !_pinService.IsCustomPinConfigured;

    public string AutomationStatus => Strings.Format("Pin.EntryAutomation", _entry.Length, PinLength);

    public void Reset()
    {
        _entry = string.Empty;
        HasError = false;
        NotifyEntryChanged();
        OnPropertyChanged(nameof(ShowsDeveloperHint));
    }

    public void Append(string? digit)
    {
        if (string.IsNullOrEmpty(digit) || !char.IsAsciiDigit(digit[0]))
        {
            return;
        }

        if (_entry.Length >= PinLength)
        {
            return;
        }

        HasError = false;
        _entry += digit[0];
        NotifyEntryChanged();

        if (_entry.Length == PinLength)
        {
            Submit();
        }
    }

    public void Backspace()
    {
        if (_entry.Length == 0)
        {
            return;
        }

        _entry = _entry[..^1];
        HasError = false;
        NotifyEntryChanged();
    }

    public void Clear()
    {
        _entry = string.Empty;
        NotifyEntryChanged();
    }

    private void Submit()
    {
        var result = _pinService.Verify(_entry);
        _entry = string.Empty;
        NotifyEntryChanged();

        if (result == PinVerificationResult.Correct)
        {
            HasError = false;
            Accepted?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Same message for "wrong" and "malformed": nothing about the expected
        // PIN is revealed.
        HasError = true;
    }

    private void NotifyEntryChanged()
    {
        OnPropertyChanged(nameof(MaskedEntry));
        OnPropertyChanged(nameof(EnteredCount));
        OnPropertyChanged(nameof(AutomationStatus));
    }
}
