using KidShell.Core.Configuration;

namespace KidShell.Core.Launching;

/// <summary>How a launch attempt ended.</summary>
public enum LaunchStatus
{
    /// <summary>The program started.</summary>
    Success = 0,

    /// <summary>No program has been pointed at this card yet.</summary>
    NotConfigured = 1,

    /// <summary>A program is configured but could not be found on this machine.</summary>
    NotFound = 2,

    /// <summary>The program exists but starting it failed.</summary>
    Failed = 3,

    /// <summary>The card is switched off in Parent Mode.</summary>
    Blocked = 4
}

/// <summary>
/// Result of a launch attempt.
///
/// <see cref="ChildMessage"/> is what a six-year-old may read.
/// <see cref="TechnicalDetail"/> is for the log and developer diagnostics and
/// must never be put in front of the child.
/// </summary>
public sealed record LaunchResult(
    LaunchStatus Status,
    string AppId,
    string ChildTitle,
    string ChildMessage,
    string? TechnicalDetail = null)
{
    public bool IsSuccess => Status == LaunchStatus.Success;

    public static LaunchResult Success(KidAppDefinition app, string? detail = null) =>
        new(LaunchStatus.Success, app.Id, app.DisplayName, $"{app.DisplayName} öppnas …", detail);

    public static LaunchResult NotConfigured(KidAppDefinition app) =>
        new(LaunchStatus.NotConfigured,
            app.Id,
            app.DisplayName,
            $"{app.DisplayName} är inte konfigurerat ännu.",
            "ExecutablePath is empty.");

    public static LaunchResult NotFound(KidAppDefinition app, string? detail = null) =>
        new(LaunchStatus.NotFound,
            app.Id,
            app.DisplayName,
            $"{app.DisplayName} finns inte på den här datorn ännu.",
            detail);

    public static LaunchResult Failed(KidAppDefinition app, string? detail = null) =>
        new(LaunchStatus.Failed,
            app.Id,
            app.DisplayName,
            $"{app.DisplayName} kunde inte startas just nu.",
            detail);

    public static LaunchResult Blocked(KidAppDefinition app) =>
        new(LaunchStatus.Blocked,
            app.Id,
            app.DisplayName,
            $"{app.DisplayName} är avstängd just nu.",
            "App is disabled in configuration.");
}
