using System.Text.Json;

namespace KidShell.Core.Configuration;

/// <summary>
/// Value comparison for configuration documents.
///
/// Parent Mode uses this to answer "are there unsaved changes?" without every
/// page having to maintain its own dirty flag - a class of bug that is easy to
/// introduce and hard to notice.
/// </summary>
public static class ConfigurationSnapshot
{
    public static string Take(KidShellConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return JsonSerializer.Serialize(configuration, JsonConfigurationStore.SerializerOptions);
    }

    public static bool AreEquivalent(KidShellConfiguration left, KidShellConfiguration right) =>
        string.Equals(Take(left), Take(right), StringComparison.Ordinal);
}
