using System.Reflection;

namespace PalServerLauncher.Core;

/// <summary>App metadata read from the assembly, shared by the log banner and the settings dialog's footer.</summary>
public static class AppInfo
{
    /// <summary>The app version, baked in from the csproj &lt;Version&gt; at build time so it never drifts.</summary>
    public static string Version { get; } = Resolve(
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3));

    /// <summary>Pick the display version, dropping the SDK's <c>+&lt;commit&gt;</c> build-metadata suffix so a
    /// source-built run reads "1.1.0" rather than "1.1.0+9a3f1c2". Pure so it can be tested.</summary>
    public static string Resolve(string? informationalVersion, string? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var plus = informationalVersion.IndexOf('+');
            return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
        }
        return string.IsNullOrWhiteSpace(assemblyVersion) ? "?" : assemblyVersion;
    }
}
