using System.Text;

namespace Zenkins.Worker.Pipelines;

/// <summary>
/// How a Windows batch script reaches <c>cmd.exe</c> (card B-016). <c>cmd.exe</c> reads the file with
/// the console's current code page, so a UTF-8 file with Chinese in it is misread before the first
/// command runs. The normal case is lossless: the script fits the host's OEM page and is written in
/// that page, with no prologue. Only a script the page cannot represent (an emoji, a rare CJK
/// extension character) switches the page to UTF-8 first.
/// </summary>
public static class WindowsBatchScript
{
    /// <summary>The first line of a batch file that had to be written as UTF-8.</summary>
    public const string ChcpPrologue = "@chcp 65001 >nul\r\n";

    /// <summary>
    /// Encodes one script for <c>cmd.exe</c>. Returns the bytes and whether they start with
    /// <see cref="ChcpPrologue"/>; the caller writes them as-is.
    /// </summary>
    /// <param name="script">The resolved script text, exactly as the user wrote it.</param>
    /// <param name="oem">The host's OEM code page encoding (see <see cref="WindowsCodePages.Oem"/>).</param>
    public static (byte[] Bytes, bool HasChcpPrologue) Encode(string script, Encoding oem)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(oem);

        // The caller's encoding may have a replacement fallback; this decision must be lossless or
        // loud, so ask the same code page with an exception fallback.
        var strict = Encoding.GetEncoding(
            oem.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            return (strict.GetBytes(script), false);
        }
        catch (EncoderFallbackException)
        {
            return (Encoding.UTF8.GetBytes(ChcpPrologue + script), true);
        }
    }
}
