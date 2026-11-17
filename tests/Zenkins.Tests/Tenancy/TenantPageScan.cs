using System.Text;

namespace Zenkins.Tests.Tenancy;

/// <summary>
/// Card Q-019's reader for a rendered page: the tenant scan compares page HTML against the other
/// organization's values, and these pages also carry <b>machine-generated opaque payloads</b> -
/// data-protection blobs (the antiforgery token in its
/// <c>&lt;meta name="request-verification-token"&gt;</c>, the server-side component state), hex
/// prerender ids and the generated element ids of the UI library - whose characters are drawn from
/// the same alphabet a short organization name is spelled with. Over 60,000 page reads (I-005's
/// review) the other organization's name <c>Beta</c> was found fifteen times and every one of them
/// was inside such a payload: eleven in a bare base64 run, four in a JSON-escaped one
/// (<c>\u002B</c> followed by <c>eta</c>). None was rendered content, so the property the scan
/// protects has never actually been violated - the scan was simply too literal to read a blob as a
/// blob.
/// </summary>
/// <remarks>
/// <para>
/// <b>Mechanism.</b> A payload is masked - replaced by <see cref="OpaquePayloadMarker"/> - when it is
/// an unbroken run of at least <see cref="MinimumStandardBase64PayloadSymbols"/> standard base64
/// symbols (alphanumeric, <c>+</c>, <c>/</c>, <c>=</c>) or at least
/// <see cref="MinimumBase64UrlPayloadSymbols"/> base64url symbols (alphanumeric, <c>-</c>, <c>_</c>,
/// <c>=</c>). A JSON unicode escape of an alphabet character counts as that character, so an escaped
/// payload is one run rather than several. Everything else is passed through byte for byte, and every
/// assertion stays a literal <see cref="StringComparison.Ordinal"/> containment test over that text.
/// </para>
/// <para>
/// <b>Two alphabets, not one union.</b> The pages carry both encodings: the escaped payload I-005 saw
/// is standard base64 (it contains <c>+</c> and <c>/</c>), the antiforgery token is base64url (it
/// contains <c>-</c> and <c>_</c>). Scanning for the union would read a URL as a payload - measured: the
/// foreign project id inside <c>/projects/{guid}/settings</c> is one 55-symbol union run, so a
/// union-alphabet mask hides it, while the two alphabets leave it visible because <c>/</c> is not a
/// base64url character. A payload is one encoding's alphabet; a run that mixes <c>/</c> with <c>-</c> is
/// a path.
/// </para>
/// <para>
/// <b>Runs that contain a GUID are never masked.</b> The product renders row ids inside generated DOM
/// ids (<c>rotate-{guid}</c>, <c>schedule-outcomes-{guid}</c>); with <c>-</c> in the base64url alphabet
/// those are 43-54 symbol runs, so without this rule the mask would swallow exactly the values the scan
/// exists to catch. A base64 payload never contains an <c>8-4-4-4-12</c> hex GUID - it is not in the
/// encoding's structure and 40 random symbols cannot spell one - so the rule costs nothing on the
/// payload side. Measured on the ten pages: 0 masked run contains a GUID.
/// </para>
/// <para>
/// <b>Why the twelve scanned values survive, stated truthfully.</b> Not because "markup is never a long
/// run" - it is: each page carries 32- and 64-symbol hex prerender ids and
/// <c>bb-scrollarea-viewport-{32 hex}</c> element ids that this mask does remove (measured: 8.8% of the
/// characters, about four data-protection payloads and about eleven generated id runs per page). What
/// protects the values is narrower and checkable: every fixture value is shorter than both minimums (the
/// ciphertext is 29 symbols, a GUID 36, the rest shorter), and a run that embeds a GUID is exempt, so a
/// leaked row id stays visible wherever the page puts it. <c>TenantPageScanTests</c> proves all twelve
/// values survive in rendered markup, and the ten-page scan keeps a positive control (the active
/// organization's own name and owner must still read as page text).
/// </para>
/// <para>
/// <b>Known limitations, pinned by facts rather than left as prose.</b> A value that sits <i>inside</i>
/// a masked run is masked with it - both when it touches a payload (<c>data-x="{80 symbols}AALBeta"</c>)
/// and in principle for a rendered value that is itself 40+ symbols of hyphenated base64url or 32+
/// symbols of base64 with no GUID in it. Neither shape occurs in this fixture's pages, and the first is
/// the price of not reading payloads as page text at all.
/// </para>
/// <para>
/// Whole-word matching was the alternative. It was not chosen because it changes the meaning of all
/// twelve assertions at once (each needle would need its own argument about word boundaries in HTML),
/// while this keeps them literal and addresses the one thing that was ever imprecise: reading a machine
/// payload as if it were text.
/// </para>
/// </remarks>
public static class TenantPageScan
{
    /// <summary>
    /// Shortest unbroken standard base64 run (alphanumeric plus <c>+</c>, <c>/</c>, <c>=</c>) treated as
    /// a machine-generated payload.
    /// </summary>
    public const int MinimumStandardBase64PayloadSymbols = 32;

    /// <summary>
    /// Shortest unbroken base64url run (alphanumeric plus <c>-</c>, <c>_</c>, <c>=</c>) treated as a
    /// machine-generated payload. Above 36 so a hyphenated GUID - and a GUID inside a generated DOM id -
    /// is never a payload on its own.
    /// </summary>
    public const int MinimumBase64UrlPayloadSymbols = 40;

    /// <summary>What a machine-generated payload reads as once it is ignored by the scan.</summary>
    public const string OpaquePayloadMarker = "<opaque-payload/>";

    private const string StandardAlphabetExtra = "+/=";
    private const string Base64UrlAlphabetExtra = "-_=";
    private const int JsonUnicodeEscapeLength = 6; // \u002B
    private const int GuidLength = 36;

    /// <summary>
    /// Returns <paramref name="body"/> with every machine-generated opaque payload replaced by
    /// <see cref="OpaquePayloadMarker"/>. Everything else - all rendered markup and text - is
    /// returned unchanged, so the caller's <see cref="StringComparison.Ordinal"/> assertions still
    /// compare the page, not a normalised approximation of it.
    /// </summary>
    public static string IgnoringMachineGeneratedPayloads(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var scanned = new StringBuilder(body.Length);
        var index = 0;
        while (index < body.Length)
        {
            // The base64url run is tested first: a base64url payload has no '+', '/', '-', so the
            // standard alphabet would cut it at its first '_' - and mask only the prefix, leaving a
            // short tail that is still page text (measured: a 198-symbol token with one '_' left 4
            // symbols of itself in front of the assertions). A path is unaffected either way, because
            // '/' is not a base64url symbol.
            var base64Url = ReadRun(body, index, Base64UrlAlphabetExtra);
            if (base64Url.Symbols >= MinimumBase64UrlPayloadSymbols
                && !ContainsGuid(body, index, base64Url.Characters))
            {
                scanned.Append(OpaquePayloadMarker);
                index += base64Url.Characters;
                continue;
            }

            var standard = ReadRun(body, index, StandardAlphabetExtra);
            if (standard.Symbols >= MinimumStandardBase64PayloadSymbols
                && !ContainsGuid(body, index, standard.Characters))
            {
                scanned.Append(OpaquePayloadMarker);
                index += standard.Characters;
                continue;
            }

            // Short runs (an ordinary word, a path segment, a GUID) are page text, copied verbatim.
            scanned.Append(body[index]);
            index++;
        }

        return scanned.ToString();
    }

    /// <summary>
    /// Reads the maximal run of symbols of one alphabet starting at <paramref name="index"/>: how many
    /// symbols it holds, and how many characters of <paramref name="body"/> it spans (a JSON-escaped
    /// symbol spans six).
    /// </summary>
    private static (int Symbols, int Characters) ReadRun(string body, int index, string extra)
    {
        var symbols = 0;
        var characters = 0;
        while (index + characters < body.Length
               && TryReadSymbol(body, index + characters, extra, out var span))
        {
            symbols++;
            characters += span;
        }

        return (symbols, characters);
    }

    /// <summary>Reads one symbol of the alphabet - a literal character or its JSON unicode escape.</summary>
    private static bool TryReadSymbol(string body, int index, string extra, out int span)
    {
        span = 1;
        if (IsAlphabetCharacter(body[index], extra))
        {
            return true;
        }

        span = 0;
        if (body[index] == '\\'
            && index + JsonUnicodeEscapeLength <= body.Length
            && body[index + 1] == 'u'
            && Uri.IsHexDigit(body[index + 2])
            && Uri.IsHexDigit(body[index + 3])
            && Uri.IsHexDigit(body[index + 4])
            && Uri.IsHexDigit(body[index + 5]))
        {
            var decoded = (char)((HexValue(body[index + 2]) << 12)
                                 | (HexValue(body[index + 3]) << 8)
                                 | (HexValue(body[index + 4]) << 4)
                                 | HexValue(body[index + 5]));
            if (IsAlphabetCharacter(decoded, extra))
            {
                span = JsonUnicodeEscapeLength;
                return true;
            }
        }

        return false;
    }

    private static bool IsAlphabetCharacter(char value, string extra) =>
        char.IsAsciiLetterOrDigit(value) || extra.Contains(value, StringComparison.Ordinal);

    /// <summary>True when the span holds an <c>8-4-4-4-12</c> hex GUID - a value never masked.</summary>
    private static bool ContainsGuid(string body, int start, int length)
    {
        for (var offset = 0; offset + GuidLength <= length; offset++)
        {
            if (IsGuid(body.AsSpan(start + offset, GuidLength)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGuid(ReadOnlySpan<char> text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (index is 8 or 13 or 18 or 23)
            {
                if (text[index] != '-')
                {
                    return false;
                }
            }
            else if (!Uri.IsHexDigit(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        _ => value - 'A' + 10,
    };
}
