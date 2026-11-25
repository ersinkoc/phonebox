using PhoneBox.Agent;
using Xunit;

/// <summary>
/// Finding a login code in a message so the agent can put it on the clipboard. It must copy only when it is sure: one
/// plausible code next to a keyword. A wrong copy overwrites what the owner had copied, so doubt means no copy.
/// </summary>
public sealed class OneTimeCodeTests
{
    [Theory]
    [InlineData("Your verification code is 482913", "482913")]
    [InlineData("482913 is your Google verification code", "482913")]
    [InlineData("Ο κωδικός σας είναι 482913", "482913")]
    [InlineData("ΚΩΔΙΚΟΣ 482913", "482913")]
    [InlineData("Κωδικός: 482-913", "482913")]
    [InlineData("Your code is 4829 13", "482913")]
    [InlineData("Your OTP: 7391", "7391")]
    [InlineData("PIN 12345678 for your transfer", "12345678")]
    [InlineData("Το PIN σου: 5821", "5821")]
    [InlineData("Code 482913. Again: your code 482913", "482913")]   // the same code twice is one code
    [InlineData("Use 482913 to verify your account.\nIgnore this line", "482913")]
    public void OneCodeNextToAKeywordIsFound(string text, string expected) => Assert.Equal(expected, OneTimeCode.Find(text));

    [Theory]
    [InlineData("Your PIN is 1234 and code 5678")]                       // two codes: do not guess
    [InlineData("Your balance is 123456 EUR")]                           // a number but no keyword
    [InlineData("Call me on 2105551234 code")]                           // ten digits are a phone number
    [InlineData("Call 210-555-1234 about your code")]                    // a phone number written with dashes
    [InlineData("Your code review is on 2026-11-23")]                    // a date
    [InlineData("code A1234B")]                                          // digits glued to letters
    [InlineData("code 123")]                                             // too short
    [InlineData("code 123456789")]                                       // too long
    [InlineData("Your code is ready")]                                   // a keyword but no digits
    [InlineData("pinterest board 4829")]                                 // 'pin' inside another word is not the keyword
    [InlineData("Your code is soon.\n\n482913 other text")]              // the keyword is on another line
    [InlineData("")]
    public void AnythingDoubtfulCopiesNothing(string text) => Assert.Null(OneTimeCode.Find(text));

    [Theory]
    [InlineData("code ١٢٣٤٥٦")]                                           // Arabic-Indic digits
    [InlineData("code １２３４５６")]                                      // full-width digits
    [InlineData("code 12٣٤56")]                                           // mixed scripts
    public void DigitsOfOtherScriptsCopyNothing(string text) => Assert.Null(OneTimeCode.Find(text));

    [Theory]
    [InlineData("Your password was changed on 23/11/2026")]                // a date
    [InlineData("Your code expires 2026.11.23")]
    [InlineData("Your card ending 4321 was charged. Not you? Verify now")] // the last digits of a card
    [InlineData("Your card ends 4321. Verify it")]
    [InlineData("Card *4321 charged, verify")]
    [InlineData("Verification of payment €1500.00 complete")]              // an amount
    [InlineData("Verify: you paid $1500 today")]
    [InlineData("Verify the payment of 1500 EUR")]
    [InlineData("Zip code 10557 is not served")]                           // a postcode
    [InlineData("Your postal code 10557 is not served")]
    [InlineData("Ο ταχυδρομικός κωδικός 10557 δεν εξυπηρετείται")]
    [InlineData("Code valid for 10 minutes. Ref 123456")]                  // a reference number
    [InlineData("Verify order #123456 now")]
    [InlineData("Verify order 123456 now")]
    [InlineData("Verify with tel 12345678")]                              // a phone number
    [InlineData("Verify the time 12:34:56")]
    public void NumbersThatAreNotCodesCopyNothing(string text) => Assert.Null(OneTimeCode.Find(text));

    [Theory]
    [InlineData("Your code is 482913.", "482913")]                         // a code at the end of a sentence
    [InlineData("482913 is your code, valid for 10 minutes", "482913")]
    [InlineData("Order 483920 shipped. Code: 123456", "123456")]            // the order number is excluded, so one code is left
    [InlineData("Code: 482913 (do not share)", "482913")]
    [InlineData("Your verification code is 482913. It expires in 10 minutes.", "482913")]
    [InlineData("Το PIN σας 1234, ποσό 50,00 ευρώ", "1234")]
    public void ARealCodeAmongOrdinaryPunctuationStillCopies(string text, string expected) =>
        Assert.Equal(expected, OneTimeCode.Find(text));

    [Fact]
    public void SearchSaysWhenTheTextHadTwoCandidates()
    {
        var two = OneTimeCode.Search("Your PIN is 1234 and code 5678");
        Assert.Null(two.Code);
        Assert.True(two.Ambiguous);
        var none = OneTimeCode.Search("Your balance is 123456 EUR");
        Assert.Null(none.Code);
        Assert.False(none.Ambiguous);
        var one = OneTimeCode.Search("Your code is 482913");
        Assert.Equal("482913", one.Code);
        Assert.False(one.Ambiguous);
    }

    [Fact]
    public void TheKeywordMustBeCloseToTheCode()
    {
        var far = "Your verification code arrives by " + new string('x', 5) + " mail soon, after a long wait of many minutes, 482913";
        Assert.Null(OneTimeCode.Find(far));
    }

    [Fact]
    public void GreekCapitalsAndFinalSigmaAndAccentsAllMatch()
    {
        Assert.Equal("123456", OneTimeCode.Find("ΚΩΔΙΚΟΣ ΕΠΑΛΗΘΕΥΣΗΣ: 123456"));
        Assert.Equal("123456", OneTimeCode.Find("κωδικοσ 123456"));
        Assert.Equal("123456", OneTimeCode.Find("Ο κωδικός επαλήθευσης είναι 123456"));
        Assert.Equal("123456", OneTimeCode.Find("123456 είναι ο κωδικός σου"));
    }

    [Theory]
    [InlineData("Verify: you paid $ 1500 today")]
    [InlineData("Your code: € 4821")]
    [InlineData("Your verification code is 1234…")]
    public void AnAmountWithASpaceAfterItsSignAndACutOffCodeAreNotCodes(string text) => Assert.Null(OneTimeCode.Find(text));

    // a security message names a date or an amount, often without any code: none of these may overwrite the clipboard
    [Theory]
    [InlineData("Your password was changed on 24 Nov 2026 at 10:31")]
    [InlineData("Your verification code expired on November 24, 2026.")]
    [InlineData("Your verification code expired on Nov 24th, 2026.")]
    [InlineData("Ο κωδικός σας άλλαξε στις 24 Νοεμβρίου 2026")]
    [InlineData("© 2026 Example. Your password was changed")]
    [InlineData("Code USD 4821 charged")]
    [InlineData("Rs 1500 debited. OTP requested for this payment")]
    [InlineData("Rs.1500 debited, OTP requested")]
    [InlineData("INR 1500 OTP requested")]
    [InlineData("OTP requested for 1500 INR")]
    [InlineData("Code used on the card ending in 4321")]
    [InlineData("Votre code postal 75001 n'est pas desservi")]
    public void DatesAmountsAndOtherLabelledNumbersCopyNothing(string text) => Assert.Null(OneTimeCode.Find(text));

    [Theory]
    [InlineData("Your code is 482913, sent on 24 Nov 2026", "482913")]  // the year is not a second candidate
    [InlineData("Rs 1500 debited. OTP 482913", "482913")]
    [InlineData("Your codes: 1234, 5678", null)]                          // two codes in a list are still two
    [InlineData("May 2026: your code is 482913", "482913")]
    public void ACodeNextToADateOrAnAmountIsStillTheOnlyCandidate(string text, string? expected) =>
        Assert.Equal(expected, OneTimeCode.Find(text));

    // The macOS and Linux agent runs with InvariantGlobalization, where string.Normalize leaves accents alone: a fold that relied
    // on it took no accent off on the Mac ("Επαλήθευση" was no keyword there, and a "ταχυδρομικός κωδικός" was copied), while
    // these tests, which run with ICU, passed. Run with DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 to see the difference.
    [Fact]
    public void GreekAccentsAreTakenOffWithoutUnicodeNormalization()
    {
        Assert.Equal("αεηιουω αεηιουω ιυιυ σ κωδικοσ", OneTimeCode.Fold("ΆΈΉΊΌΎΏ άέήίόύώ ϊϋΐΰ ς ΚΩΔΙΚΌΣ"));
        Assert.Equal("482913", OneTimeCode.Find("Επαλήθευση: 482913"));
        Assert.Null(OneTimeCode.Find("Ο ταχυδρομικός κωδικός 10557 δεν εξυπηρετείται"));
    }

    [Fact]
    public void ALongTextWithManyKeywordsIsSearchedQuickly()
    {
        // 64 KiB, the request limit, of keywords and candidates: each candidate looks only at its nearest keywords
        var text = string.Concat(Enumerable.Repeat("code 1234 ", 6500));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal("1234", OneTimeCode.Find(text));
        Assert.True(watch.ElapsedMilliseconds < 2000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void ACodeStillCountsWhenADollarSignIsFarAway() => Assert.Equal("482913", OneTimeCode.Find("Paid $ 5. Your code is 482913"));
}
