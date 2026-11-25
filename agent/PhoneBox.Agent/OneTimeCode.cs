using System.Text;
using System.Text.RegularExpressions;

namespace PhoneBox.Agent;

/// <summary>
/// Finds the login code in a message ("Your code is 482913") so it can be put on the clipboard. It answers only when it is
/// sure: exactly one plausible code, with a keyword close by on the same line. Two codes, no keyword, a phone number or a
/// date all give null, because a wrong copy would overwrite what the owner had copied.
/// </summary>
public static partial class OneTimeCode
{
    /// <summary>The most characters allowed between a code and its keyword.</summary>
    private const int MaxGap = 30;

    /// <summary>
    /// The patterns are linear (no nested repetition), so a 64 KiB text takes milliseconds; the timeout is a backstop, and a
    /// search that hits it counts as doubt: nothing is copied.
    /// </summary>
    private const int MatchTimeoutMs = 500;

    // month names as the folded text spells them (lower case, no accents, σ for ς): English, then Greek (nominative and genitive)
    private const string Months =
        "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?"
        + "|ιαν(?:ουαριο[υσ])?|φεβ(?:ρουαριο[υσ])?|μαρ(?:τιο[υσ])?|απρ(?:ιλιο[υσ])?|μαι(?:ο[υσ])?|ιουν(?:ιο[υσ])?|ιουλ(?:ιο[υσ])?"
        + "|αυγ(?:ουστο[υσ])?|σεπ(?:τεμβριο[υσ])?|οκτ(?:ωβριο[υσ])?|νοε(?:μβριο[υσ])?|δεκ(?:εμβριο[υσ])?";

    // A currency sign before the digits counts even with a space between ("$ 1500"), and a text that was cut ("code 1234…") has no
    // code in it: the rest of the digits are missing.
    // 4-8 ASCII digits, or two groups split by one space or dash. Not part of a longer run of digits or letters: not a phone number
    // (210-555-1234), a date (23/11/2026, 2026.11.23), a time or an amount (1500.00, 50,00), and not a number that is
    // labelled as something else: an amount (a currency sign or code before it, "EUR 1500", "Rs. 1500", or a currency after
    // it), the end of a card (ending 4321, ending in 4321, *4321), a reference, order, invoice or phone number, or a year
    // ("24 Nov 2026", "November 24, 2026", "24 Νοεμβρίου 2026", "© 2026"): a security message often names the date without a code.
    [GeneratedRegex(@"(?<![\p{L}\p{N}*#]|[€$£¥₹©]\s*|[0-9][ \-./,:]|\b(?:(?:ending|ends)(?: in| with)?|ref|reference|order|invoice|id|no|tel|phone|call|τηλ)\.?:? |\b(?:eur|usd|gbp|chf|inr|rs|ευρω)\.?\s?|\b(?:" + Months + @")\.?,? |\b(?:" + Months + @")\.? [0-9]{1,2}(?:st|nd|rd|th)?, )(?:[0-9]{4,8}|[0-9]{2,4}[ \-][0-9]{2,4})(?![\p{L}\p{N}…]|[ \-./,:][0-9]|\s?(?:eur|usd|gbp|chf|inr|euro|euros|ευρω|€|\$|£))",
        RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Candidates();

    // run on text folded to lower case with Greek accents removed and ς as σ, so ΚΩΔΙΚΟΣ, κωδικός and κωδικοσ are one word.
    // "zip code", "promo code", "tracking code" and the like are codes of another kind, not a login code; so is a "code postal".
    [GeneratedRegex(@"(?<!\p{L})(?<!(?:zip|postal|post|area|country|promo|coupon|voucher|discount|tracking|error|dial) )(?<!ταχυδρομικοσ )(?<!ταχυδρομικου )(?:(?:code(?! postal)|otp|pin|verification|verify|password)s?(?!\p{L})|κωδικ\p{L}*|επαληθευσ\p{L}*)",
        RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Keywords();

    /// <summary>What a search found: the code, or null with <see cref="Ambiguous"/> telling "two or more candidates" from "none".</summary>
    public readonly record struct CodeSearch(string? Code, bool Ambiguous);

    /// <summary>The code's digits (separators removed) when there is exactly one, else null.</summary>
    public static string? Find(string text) => Search(text).Code;

    public static CodeSearch Search(string text)
    {
        if (string.IsNullOrEmpty(text)) return new(null, false);
        try { return SearchFolded(Fold(text)); }
        catch (RegexMatchTimeoutException) { return new(null, true); } // doubt: no copy, and no second look at the title
    }

    private static CodeSearch SearchFolded(string folded)
    {
        var keywords = Keywords().Matches(folded).Select(m => (Start: m.Index, End: m.Index + m.Length)).ToList();
        if (keywords.Count == 0) return new(null, false);
        // keywords are in text order and never overlap a candidate (letters, not digits): only the nearest keyword on each side
        // of a candidate can be close enough, so one binary search per candidate keeps a long text linear
        var starts = keywords.Select(k => k.Start).ToArray();
        var found = new HashSet<string>();
        foreach (Match c in Candidates().Matches(folded))
        {
            var (start, end) = (c.Index, c.Index + c.Length);
            var after = Array.BinarySearch(starts, end);
            if (after < 0) after = ~after;
            var near = (after < keywords.Count && Near(folded, keywords[after].Start, keywords[after].End, start, end))
                || (after > 0 && Near(folded, keywords[after - 1].Start, keywords[after - 1].End, start, end));
            if (!near) continue;
            var digits = new string(c.Value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length >= 4) found.Add(digits); // never a short or empty copy
            if (found.Count > 1) return new(null, true);
        }
        return found.Count == 1 ? new(found.First(), false) : new(null, false);
    }

    /// <summary>
    /// One output character per input character, so positions in the result are positions in the text. Accents are taken off
    /// by a table, not by Unicode normalization: the macOS and Linux agent runs with InvariantGlobalization, where
    /// string.Normalize leaves "ό" as it is, so a normalizing fold would behave differently in the tests than on the Mac.
    /// </summary>
    internal static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(char.ToLowerInvariant(ch) switch
            {
                'ά' => 'α', 'έ' => 'ε', 'ή' => 'η', 'ί' or 'ϊ' or 'ΐ' => 'ι', 'ό' => 'ο', 'ύ' or 'ϋ' or 'ΰ' => 'υ', 'ώ' => 'ω', 'ς' => 'σ',
                var lower => lower,
            });
        return sb.ToString();
    }

    private static bool Near(string s, int aStart, int aEnd, int bStart, int bEnd)
    {
        var (from, to) = aEnd <= bStart ? (aEnd, bStart) : bEnd <= aStart ? (bEnd, aStart) : (0, 0);
        if (to - from > MaxGap) return false;
        for (var i = from; i < to; i++)
            if (s[i] is '\n' or '\r') return false;
        return true;
    }
}
