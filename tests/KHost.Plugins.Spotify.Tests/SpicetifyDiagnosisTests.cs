using KHost.Plugins.Spotify.Bridge;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>
/// The extension is the only code inside Spotify's client, so what it says about itself is the
/// only evidence the host ever gets — and the same evidence on every operating system, since it is
/// the same client. These are the readings that turn into what a host is told to go and do.
/// </summary>
public class SpicetifyDiagnosisTests
{
    private const string PlayerThrew = "Cannot read properties of undefined (reading '_volume')";

    /// <summary>
    /// The fault this exists to name, measured on a real machine: Spicetify 2.44.0 patching
    /// Spotify 1.3.0.277. It patches without error, its own API never binds, Spicetify.Platform
    /// stays an empty object, and every extension sits waiting for a player that never comes.
    /// </summary>
    [Fact]
    public void SpicetifyApiNeverStarted_PlatformIsEmptyAndNothingIsReady_IsTrue()
        => Assert.True(Diagnosis(ready: false, platformKeys: 0).SpicetifyApiNeverStarted);

    /// <summary>A slow start is not a fault, and a ready extension is not diagnosing anything.</summary>
    [Fact]
    public void SpicetifyApiNeverStarted_TheExtensionIsReady_IsFalse()
        => Assert.False(Diagnosis(ready: true, platformKeys: 40).SpicetifyApiNeverStarted);

    /// <summary>
    /// A populated Platform and still no player is a different fault, and must not be answered
    /// with "update Spicetify" — that sends a host to do something that will not help.
    /// </summary>
    [Fact]
    public void SpicetifyApiNeverStarted_PlatformIsPopulated_IsFalse()
        => Assert.False(Diagnosis(ready: false, platformKeys: 40).SpicetifyApiNeverStarted);

    [Fact]
    public void Describe_TheApiNeverStarted_NamesTheApiAndWhatItThrew()
    {
        var described = Diagnosis(ready: false, platformKeys: 0).Describe();

        Assert.Contains("Spicetify's own API never started", described);
        Assert.Contains("after 30s", described);
        Assert.Contains(PlayerThrew, described);
    }

    /// <summary>
    /// A populated Platform with no usable player is described as what it is. Saying the API never
    /// started here would be a confident wrong answer, which is worse than a vague right one.
    /// </summary>
    [Fact]
    public void Describe_PlatformIsPopulatedButNothingIsReady_DoesNotBlameTheApi()
    {
        var described = Diagnosis(ready: false, platformKeys: 40).Describe();

        Assert.DoesNotContain("API never started", described);
        Assert.Contains("never became usable", described);
    }

    [Fact]
    public void Parse_AReadyReport_ReadsEveryField()
    {
        var parsed = SpicetifyDiagnosis.Parse(FakeExtension.Diagnosis(ready: true));

        Assert.Equal(new SpicetifyDiagnosis(true, 0, true, true, 40, null), parsed);
    }

    /// <summary>Parsed from the same text the fake extension sends, which is the text the real one sends.</summary>
    [Fact]
    public void Parse_AnUnreadyReport_CarriesTheErrorThroughToTheHost()
    {
        var parsed = SpicetifyDiagnosis.Parse(FakeExtension.Diagnosis(ready: false));

        Assert.Equal(new SpicetifyDiagnosis(false, 30000, true, false, 0, PlayerThrew), parsed);
    }

    /// <summary>The socket carries several kinds of message, and only one of them is this.</summary>
    [Theory]
    [InlineData("""{"type":"state","playing":true,"volume":0.5}""")]
    [InlineData("""{"type":"faded","to":0}""")]
    [InlineData("""["diagnosis"]""")]
    [InlineData("not json at all")]
    public void Parse_AnythingElse_IsNull(string payload)
        => Assert.Null(SpicetifyDiagnosis.Parse(payload));

    /// <summary>
    /// An older extension sends no diagnosis fields at all. Read as not-ready rather than thrown
    /// over: the port is loopback, but a malformed line must not stop the bridge.
    /// </summary>
    [Fact]
    public void Parse_AReportMissingEverythingButItsType_IsNotReady()
    {
        var parsed = SpicetifyDiagnosis.Parse("""{"type":"diagnosis"}""");

        Assert.False(parsed?.Ready);
    }

    private static SpicetifyDiagnosis Diagnosis(bool ready, int platformKeys) => new(
        ready,
        WaitedMilliseconds: ready ? 0 : 30000,
        HasSpicetify: true,
        HasPlayer: ready,
        platformKeys,
        Error: ready ? null : PlayerThrew);
}
