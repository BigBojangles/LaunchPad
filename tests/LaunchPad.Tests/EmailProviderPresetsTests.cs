using LaunchPad.Services;
using Xunit;

namespace LaunchPad.Tests;

public sealed class EmailProviderPresetsTests
{
    [Theory]
    [InlineData(" User+pager@GMAIL.COM ", "Gmail")]
    [InlineData("user@yahoo.com", "Yahoo")]
    [InlineData("user@icloud.com", "iCloud")]
    [InlineData("user@fastmail.com", "Fastmail")]
    [InlineData("user@hotmail.com", "Outlook / Hotmail")]
    public void RecognizesExactProviderDomainsWithoutChangingAccount(string email, string provider)
    {
        Assert.Equal(provider, EmailProviderPresets.Match(email)?.Name);
        Assert.Equal(email.Trim(), EmailProviderPresets.Address(email));
    }

    [Theory]
    [InlineData("user@gmail.com.attacker.invalid")]
    [InlineData("user@notgmail.com")]
    [InlineData("user@custom.invalid")]
    [InlineData("Display Name <user@gmail.com>")]
    [InlineData("user@gmail.com\r\nBcc: other@invalid.example")]
    [InlineData("not-an-address")]
    public void UnknownOrMalformedAddressCannotChooseAProvider(string email)
        => Assert.Null(EmailProviderPresets.Match(email));

    [Fact]
    public void OutlookCannotSuggestPasswordDeliveryAndHelpLinksAreFixedHttps()
    {
        Assert.True(EmailProviderPresets.Match("user@outlook.com")!.RequiresOAuth);
        Assert.False(EmailProviderPresets.Match("user@gmail.com")!.RequiresOAuth);
        Assert.All(EmailProviderPresets.All, preset =>
        {
            Assert.Equal("https", new Uri(preset.HelpUrl).Scheme);
            Assert.InRange(preset.Port, 1, 65535);
        });
    }
}
