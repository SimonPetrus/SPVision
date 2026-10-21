using System.Net;
using SPVision.Core;

namespace SPVision.Core.Tests;

/// <summary>"HTTPS kullan": the port field follows the checkbox (80 ↔ 443), and when a failed HTTPS attempt offers HTTP.</summary>
public class HttpsPortTests
{
    [Theory]
    [InlineData("80", true, "443")]
    [InlineData("443", false, "80")]
    [InlineData(" 80 ", true, "443")]
    [InlineData("443", true, "443")]
    [InlineData("80", false, "80")]
    public void StandardPortFollowsTheCheckbox(string text, bool useHttps, string expected) =>
        Assert.Equal(expected, HttpsPort.Follow(text, useHttps));

    [Theory]
    [InlineData("8080", true)]
    [InlineData("8443", false)]
    [InlineData("8000", true)]
    public void CustomPortIsLeftAlone(string text, bool useHttps) =>
        Assert.Equal(text, HttpsPort.Follow(text, useHttps));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("-80")]
    [InlineData("99999999999")]
    public void EmptyOrInvalidFieldIsLeftAlone(string text)
    {
        Assert.Equal(text, HttpsPort.Follow(text, true));
        Assert.Equal(text, HttpsPort.Follow(text, false));
    }

    [Fact]
    public void ConnectionFailureOverHttpsOffersHttp() =>
        Assert.True(HttpsPort.OfferHttpFallback(true, false, new IsapiException("Cihaza bağlanılamadı", connectionFailed: true)));

    [Fact]
    public void ConnectionFailureOverHttpDoesNotOfferHttp() =>
        Assert.False(HttpsPort.OfferHttpFallback(false, false, new IsapiException("Cihaza bağlanılamadı", connectionFailed: true)));

    [Fact]
    public void ConnectionFailureAfterAnAnswerDoesNotOfferHttp() =>
        Assert.False(HttpsPort.OfferHttpFallback(true, true, new IsapiException("Cihaz zaman aşımına uğradı.", connectionFailed: true)));

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public void DeviceAnswerDoesNotOfferHttp(HttpStatusCode status) =>
        Assert.False(HttpsPort.OfferHttpFallback(true, false, new IsapiException("Kullanıcı adı veya şifre hatalı.", status)));

    [Fact]
    public void OtherErrorsDoNotOfferHttp()
    {
        Assert.False(HttpsPort.OfferHttpFallback(true, false, new IsapiException("Cihazdan geçersiz XML geldi")));
        Assert.False(HttpsPort.OfferHttpFallback(true, false, new InvalidOperationException()));
    }
}
