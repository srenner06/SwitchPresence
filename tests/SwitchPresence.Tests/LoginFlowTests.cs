using SwitchPresence.Nintendo;
using Xunit;

namespace SwitchPresence.Tests;

public class LoginFlowTests
{
    private const string RealLink =
        "npf71b963c1b7b6d119://auth#session_token_code=eyJhbGciOiJIUzI1NiJ9.payload.signature"
        + "&state=Lc7lnYMSuyl-BI1VOOAOcHZ0aNqQkg_R8UkQgzhGZwDLdSZi"
        + "&session_state=32b6815c24a1a6b8ef04756484a5bcf5924635021baa557f11ff6ab2e873bb90";

    [Fact]
    public void TheCodeAndStateComeOutOfTheFragment()
    {
        var fragment = PendingLogin.ParseFragment(RealLink);

        Assert.Equal("eyJhbGciOiJIUzI1NiJ9.payload.signature", fragment["session_token_code"]);
        Assert.Equal("Lc7lnYMSuyl-BI1VOOAOcHZ0aNqQkg_R8UkQgzhGZwDLdSZi", fragment["state"]);
    }

    [Fact]
    public void PercentEncodingIsUndone()
    {
        var fragment = PendingLogin.ParseFragment("npf://auth#error_description=Something%20went%20wrong");

        Assert.Equal("Something went wrong", fragment["error_description"]);
    }

    // Clicking the button instead of copying its link address is the likely mistake, and the
    // message has to say so rather than just failing.
    [Fact]
    public void ALinkWithoutAFragmentIsRejectedHelpfully()
    {
        var ex = Assert.Throws<LoginException>(() =>
            PendingLogin.ParseFragment("https://accounts.nintendo.com/connect/1.0.0/authorize?state=x"));

        Assert.Contains("link address", ex.Message);
    }

    [Fact]
    public void ValuelessParametersDoNotThrow()
    {
        var fragment = PendingLogin.ParseFragment("npf://auth#state=&other");

        Assert.Equal("", fragment["state"]);
        Assert.Equal("", fragment["other"]);
    }

    [Fact]
    public void EachAttemptGetsItsOwnStateAndChallenge()
    {
        var first = LoginFlow.Start().AuthorizeUrl;
        var second = LoginFlow.Start().AuthorizeUrl;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheAuthorizeUrlAsksForWhatCoralNeeds()
    {
        var url = LoginFlow.Start().AuthorizeUrl;

        Assert.Contains("client_id=71b963c1b7b6d119", url);
        Assert.Contains("response_type=session_token_code", url);
        Assert.Contains("session_token_code_challenge_method=S256", url);
    }
}