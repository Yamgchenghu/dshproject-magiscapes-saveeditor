using System.Reflection;

namespace GenerativeSave.Editor;

/// <summary>
/// The single source of truth for the version string shown in the UI.
///
/// It is read from the assembly rather than hard-coded so the number in the
/// corner and the number in the release can never drift apart: bump
/// &lt;Version&gt; in the csproj and both follow.
/// </summary>
internal static class AppVersion
{
    /// <summary>Semantic version, e.g. "1.1.0".</summary>
    public static string Number { get; } = Read();

    /// <summary>What the status bar shows: "v1.1.0".</summary>
    public static string Display => "v" + Number;

    private static string Read()
    {
        var asm = Assembly.GetExecutingAssembly();

        // InformationalVersion carries any "+buildmetadata" suffix; the plain
        // three-part number is what a user wants to read.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        var v = asm.GetName().Version;
        return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
    }
}
