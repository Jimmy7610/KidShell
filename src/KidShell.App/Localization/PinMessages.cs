using KidShell.Core.Security;

namespace KidShell.App.Localization;

/// <summary>
/// Parent-facing wording for each way a PIN can be refused.
///
/// One mapping, shared by first-run setup and the change-PIN dialog. It used to
/// live privately in ParentFlows, which meant the setup screen had no way to
/// say anything specific - and a form that answers "must be six digits" to a
/// six-digit sequence teaches the parent it is broken.
///
/// The mapping lives in the app layer rather than Core because Core has no
/// access to the string table, and deliberately no opinion about wording.
/// </summary>
public static class PinMessages
{
    public static string Describe(PinValidation validation) => validation switch
    {
        PinValidation.Empty => Strings.Get("Pin.ErrorEmpty"),
        PinValidation.NotNumeric => Strings.Get("Pin.ErrorNotNumeric"),
        PinValidation.WrongLength => Strings.Get("Dialog.ChangePinInvalid"),
        PinValidation.Repeated => Strings.Get("Pin.ErrorRepeated"),
        PinValidation.Sequential => Strings.Get("Pin.ErrorSequential"),
        PinValidation.ReservedDevelopmentPin => Strings.Get("Pin.ErrorReserved"),
        PinValidation.ConfirmationMismatch => Strings.Get("Dialog.ChangePinMismatch"),
        _ => Strings.Get("Dialog.ChangePinInvalid")
    };
}
