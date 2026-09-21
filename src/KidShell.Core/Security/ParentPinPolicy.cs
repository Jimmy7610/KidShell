namespace KidShell.Core.Security;

/// <summary>Why a proposed PIN was rejected.</summary>
public enum PinValidation
{
    Ok = 0,

    /// <summary>Nothing entered.</summary>
    Empty = 1,

    /// <summary>Not the required number of digits.</summary>
    WrongLength = 2,

    /// <summary>Contained something other than digits.</summary>
    NotNumeric = 3,

    /// <summary>All digits the same, e.g. 000000.</summary>
    Repeated = 4,

    /// <summary>A straight run, e.g. 123456 or 987654.</summary>
    Sequential = 5,

    /// <summary>
    /// The development fallback. Refused as a chosen PIN in every build,
    /// including developer builds — it is published in the README, so allowing
    /// a parent to pick it would turn a testing convenience into a real
    /// vulnerability.
    /// </summary>
    ReservedDevelopmentPin = 6,

    /// <summary>The confirmation entry did not match the first one.</summary>
    ConfirmationMismatch = 7
}

/// <summary>
/// The rules a parent PIN has to satisfy.
///
/// Deliberately modest: this gate keeps a six-year-old out of Parent Mode, not
/// a motivated adult. Rejecting 000000 and 123456 removes the PINs a child
/// guesses first; demanding more would push parents towards writing it down,
/// which is worse.
/// </summary>
public static class ParentPinPolicy
{
    public const int RequiredLength = 6;

    public static PinValidation Validate(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin))
        {
            return PinValidation.Empty;
        }

        if (!pin.All(char.IsAsciiDigit))
        {
            return PinValidation.NotNumeric;
        }

        if (pin.Length != RequiredLength)
        {
            return PinValidation.WrongLength;
        }

        if (pin.Distinct().Count() == 1)
        {
            return PinValidation.Repeated;
        }

        if (IsSequential(pin))
        {
            return PinValidation.Sequential;
        }

        // Never allow the published fallback to become a real PIN, in any
        // build. It would look configured while being common knowledge.
        if (PinHasher.FixedTimeEquals(pin, DevelopmentPin.Value))
        {
            return PinValidation.ReservedDevelopmentPin;
        }

        return PinValidation.Ok;
    }

    /// <summary>Validates a PIN together with its confirmation entry.</summary>
    public static PinValidation ValidatePair(string? pin, string? confirmation)
    {
        var result = Validate(pin);

        if (result != PinValidation.Ok)
        {
            return result;
        }

        return string.Equals(pin, confirmation, StringComparison.Ordinal)
            ? PinValidation.Ok
            : PinValidation.ConfirmationMismatch;
    }

    /// <summary>Ascending or descending runs of consecutive digits.</summary>
    private static bool IsSequential(string pin)
    {
        var ascending = true;
        var descending = true;

        for (var i = 1; i < pin.Length; i++)
        {
            var delta = pin[i] - pin[i - 1];

            if (delta != 1)
            {
                ascending = false;
            }

            if (delta != -1)
            {
                descending = false;
            }
        }

        return ascending || descending;
    }
}
