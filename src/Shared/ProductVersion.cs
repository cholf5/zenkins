using System.Reflection;

namespace Zenkins.Shared;

/// <summary>
/// The product version, stamped at build time from the single <c>Version</c> property in
/// <c>Directory.Build.props</c> (card P-001). The .NET SDK writes that property into every
/// assembly's <see cref="AssemblyInformationalVersionAttribute"/> - as <c>1.0.0+&lt;source revision&gt;</c>,
/// because the SDK appends the git commit - so there is no generated code and no second place to
/// bump; this reader is the one seam that strips the revision back off for display.
/// <para>
/// Reading an attribute off an assembly is metadata-only reflection, which NativeAOT keeps - that
/// is why the Worker's <c>--version</c> flag can print the stamp from the AOT binary (proven by the
/// PublishAot fixture's probe). <see cref="Current"/> resolves the entry assembly, so a Server
/// process reports the Server's stamp and a Worker process reports the Worker's stamp - one
/// property, one value, whichever binary prints it.
/// </para>
/// </summary>
public static class ProductVersion
{
    /// <summary>
    /// The version of the running process's own binary (the entry assembly), or of the Shared
    /// assembly when no entry assembly exists (a library host). "unknown" would mean the stamp is
    /// missing entirely, which the version tests treat as a failure, not a display quirk.
    /// </summary>
    public static string Current => From(Assembly.GetEntryAssembly() ?? typeof(ProductVersion).Assembly);

    /// <summary>The informational version of <paramref name="assembly"/>, without the source-revision suffix.</summary>
    public static string From(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return "unknown";
        }

        // The SDK's InformationalVersion is "<Version>+<SourceRevisionId>" when a git revision is
        // available; the release stamp is the part before the '+'.
        var revisionSeparator = informational.IndexOf('+');
        return revisionSeparator < 0 ? informational : informational[..revisionSeparator];
    }
}
