using SwitchPresence.Nintendo;
using SwitchPresence.Presence;
using SwitchPresence.Storage;
using Xunit;

namespace SwitchPresence.Tests;

public class PresenceMapperTests
{
    const long Start = 1_795_302_022_872;

    static AppConfig Config() => new()
    {
        DiscordClientIdSwitch = "switch1-app",
        DiscordClientIdSwitch2 = "switch2-app",
    };

    static CoralPresence Playing(string state = "ONLINE", int? platform = 2,
        string? game = "Nintendo Switch Sports Resort", string? sysDescription = "") => new()
        {
            State = state,
            Platform = platform,
            Game = game is null ? new PresenceGame() : new PresenceGame
            {
                Name = game,
                ImageUri = "https://atum-img-lp1.cdn.nintendo.net/i/c/abc_1024",
                ShopUri = "https://ec.nintendo.com/apps/04008e001f098000/CH?acdIndex=0",
                SysDescription = sysDescription,
            },
        };

    [Theory]
    [InlineData("OFFLINE")]
    [InlineData("INACTIVE")]
    public void NothingIsShownWhenTheConsoleIsNotInAGame(string state) =>
        Assert.Null(PresenceMapper.Map(Playing(state), Config(), Start));

    [Fact]
    public void NothingIsShownWithoutAPresence() =>
        Assert.Null(PresenceMapper.Map(null, Config(), Start));

    [Fact]
    public void NothingIsShownWhenGameIsTheEmptyObjectCoralSendsWhileIdle() =>
        Assert.Null(PresenceMapper.Map(Playing(game: null), Config(), Start));

    [Theory]
    [InlineData(2, "switch2-app", "Playing on Nintendo Switch 2")]
    [InlineData(1, "switch1-app", "Playing on Nintendo Switch")]
    [InlineData(null, "switch1-app", "Playing on Nintendo Switch")]
    public void ThePlatformPicksTheDiscordApp(int? platform, string expectedApp, string expectedText)
    {
        var mapped = PresenceMapper.Map(Playing(platform: platform), Config(), Start);

        Assert.NotNull(mapped);
        Assert.Equal(expectedApp, mapped.AppId);
        Assert.Equal(expectedText, mapped.Activity.Assets?.LargeText);
    }

    [Fact]
    public void TheGameNameAndArtworkAreCarriedThrough()
    {
        var mapped = PresenceMapper.Map(Playing(), Config(), Start);

        Assert.Equal("Nintendo Switch Sports Resort", mapped!.Activity.Details);
        Assert.Equal("https://atum-img-lp1.cdn.nintendo.net/i/c/abc_1024", mapped.Activity.Assets?.LargeImage);
        Assert.Equal(Start, mapped.Activity.Timestamps?.Start);
        Assert.Equal(0, mapped.Activity.Type);
    }

    [Fact]
    public void DetailsAreTruncatedToWhatDiscordAccepts()
    {
        var mapped = PresenceMapper.Map(Playing(game: new string('x', 200)), Config(), Start);

        Assert.Equal(128, mapped!.Activity.Details!.Length);
    }

    // sysDescription arrives as "" rather than absent, which is the subtle one.
    [Fact]
    public void AnEmptySysDescriptionIsTreatedAsAbsent()
    {
        var mapped = PresenceMapper.Map(Playing("PLAYING", sysDescription: ""), Config(), Start);

        Assert.Equal("Playing online", mapped!.Activity.State);
    }

    [Fact]
    public void SysDescriptionWinsOverPlayingOnline()
    {
        var mapped = PresenceMapper.Map(Playing("PLAYING", sysDescription: "Round 3"), Config(), Start);

        Assert.Equal("Round 3", mapped!.Activity.State);
    }

    // ONLINE means in a game; only PLAYING means in a game *online*.
    [Fact]
    public void OnlineWithoutASysDescriptionHasNoSecondLine() =>
        Assert.Null(PresenceMapper.Map(Playing("ONLINE"), Config(), Start)!.Activity.State);

    [Fact]
    public void PlayingOnlineCanBeTurnedOff()
    {
        var config = Config();
        config.ShowPlayingOnline = false;

        Assert.Null(PresenceMapper.Map(Playing("PLAYING"), config, Start)!.Activity.State);
    }

    [Fact]
    public void TheEshopButtonUsesTheShopUri()
    {
        var button = Assert.Single(PresenceMapper.Map(Playing(), Config(), Start)!.Activity.Buttons!);

        Assert.Equal("Nintendo eShop", button.Label);
        Assert.Equal("https://ec.nintendo.com/apps/04008e001f098000/CH?acdIndex=0", button.Url);
    }

    [Fact]
    public void TheEshopButtonCanBeTurnedOff()
    {
        var config = Config();
        config.ShowEshopButton = false;

        Assert.Null(PresenceMapper.Map(Playing(), config, Start)!.Activity.Buttons);
    }

    // Discord rejects non-https button URLs, and Coral sends "" for some titles.
    [Theory]
    [InlineData("")]
    [InlineData("http://ec.nintendo.com/apps/04008e001f098000/CH")]
    public void AButtonIsOmittedWhenTheShopUriIsNotUsable(string shopUri)
    {
        var presence = Playing();
        presence.Game!.ShopUri = shopUri;

        Assert.Null(PresenceMapper.Map(presence, Config(), Start)!.Activity.Buttons);
    }

    [Fact]
    public void ArtworkIsOmittedRatherThanSentEmpty()
    {
        var presence = Playing();
        presence.Game!.ImageUri = "";

        Assert.Null(PresenceMapper.Map(presence, Config(), Start)!.Activity.Assets);
    }
}
