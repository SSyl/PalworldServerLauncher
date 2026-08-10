namespace PalServerLauncher.Core;

/// <summary>
/// Which UE4SS install the launcher can see. UE4SS lives in one of two places: the Steam Workshop mod, which
/// the server deploys into <c>Mods\NativeMods\UE4SS</c>, and a hand-installed copy extracted to
/// <c>Pal\Binaries\Win64\ue4ss</c> next to its dwmapi.dll proxy. Only one may load. Two live copies crash the
/// server on launch, so <see cref="Both"/> is a failure state, not a richer version of installed.
/// </summary>
public enum Ue4ssInstall
{
    /// <summary>Neither location has UE4SS.</summary>
    None,

    /// <summary>Only the Workshop mod, deployed by the server. The launcher manages this one.</summary>
    Workshop,

    /// <summary>Only a hand-installed copy under <c>Pal\Binaries\Win64\ue4ss</c>, outside Palworld's mod system.</summary>
    Custom,

    /// <summary>Both are present, which crashes the server on launch.</summary>
    Both,
}
