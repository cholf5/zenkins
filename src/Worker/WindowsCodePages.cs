using System.Globalization;
using System.Text;

namespace Zenkins.Worker;

/// <summary>
/// The Windows code page a native tool writes when it is not speaking UTF-8 (card B-016).
/// <para>
/// .NET Core's <see cref="Encoding.Default"/> is UTF-8 on every platform, so it cannot answer this
/// question; the provider package supplies the tables and the host's OEM code page is the one
/// <c>cmd.exe</c> and the console subsystem use. Off Windows there is no fallback: the platform's
/// tools speak UTF-8 and a byte string that is not valid UTF-8 is a genuine error, not a locale.
/// </para>
/// <para>
/// <b>Registration is process-wide and idempotent.</b> <see cref="CodePagesEncodingProvider"/>
/// has to be registered before <see cref="Encoding.GetEncoding(int)"/> can resolve 936/1252/…;
/// both <see cref="ProcessRunner"/> and the batch-file writer call <see cref="Oem"/> in their own
/// startup path, so neither depends on the other having run first.
/// </para>
/// </summary>
public static class WindowsCodePages
{
    private static readonly object Gate = new();
    private static bool _registered;

    /// <summary>
    /// The host's OEM code page on Windows, or <see langword="null"/> elsewhere. A lookup that the
    /// platform refuses also answers <see langword="null"/>: the caller then falls back to UTF-8
    /// replacement, which is an honest (if lossy) console line rather than a failed run.
    /// </summary>
    public static Encoding? Oem
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            Register();
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (ArgumentException)
            {
                return null;
            }
            catch (NotSupportedException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Registers the code-pages provider once per process. Safe to call from multiple threads;
    /// the provider itself tolerates repeat registration, the flag only avoids the lock traffic.
    /// </summary>
    public static void Register()
    {
        if (Volatile.Read(ref _registered))
        {
            return;
        }

        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            Volatile.Write(ref _registered, true);
        }
    }
}
