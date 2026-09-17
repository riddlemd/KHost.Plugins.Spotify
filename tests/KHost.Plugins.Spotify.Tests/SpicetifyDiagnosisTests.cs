using KHost.Plugins.Spotify.Bridge;

namespace KHost.Plugins.Spotify.Tests;

/// <summary>The extension is the only code inside Spotify's client, so what it says about itself
/// is the only evidence the host ever gets, identical on every operating system.</summary>
public class SpicetifyDiagnosisTests
{
    private const string PlayerThrew = "Cannot read properties of undefined (reading '_volume')";

    /// <summary>The fault this exists to name, measured on a real machine: Spicetify patching
    /// Spotify without error, its own API never binding, every extension waiting forever.</summary>
    [Fact]
    public void SpicetifyApiNeverStarted_PlatformIsEmptyAndNothingIsReady_IsTrue()
        => Assert.True(Diagnosis(ready: false, platformKeys: 0).SpicetifyApiNeverStarted);

    /// <summary>A slow start is not a fault, and a ready extension is not diagnosing anything.</summary>
    [Fact]
    public void SpicetifyApiNeverStarted_TheExtensionIsReady_IsFalse()
        => Assert.False(Diagnosis(ready: true, platformKeys: 40).SpicetifyApiNeverStarted);

    /// <summary>A populated Platform and still no player is a different fault, and must not be
    /// answered with "update Spicetify": that sends a host to do something that will not help.</summary>
    [Fact]
    public void SpicetifyApiNeverStarted_PlatformIsPopulated_IsFalse()
        => Assert.False(Diagnosis(ready: false, platformKeys: 40).SpicetifyApiNeverStarted);

    [Fact]
    public void Describe_TheApiNeverStarted_NamesTheApiAndWhatItThrew()
    {
        var described = Diagnosis(ready: false, platformKeys: 0).Describe();

        Assert.Contains("Spicetify's own API never started", described);
        Assert.Contains("after 15s", described);
        Assert.Contains(PlayerThrew, described);
    }

    /// <summary>A populated Platform with no usable player is described as what it is: saying the
    /// API never started would be a confident wrong answer, worse than a vague right one.</summary>
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

    /// <summary>Parsed from the same text the fake extension sends, the text the real one sends.</summary>
    [Fact]
    public void Parse_AnUnreadyReport_CarriesTheErrorThroughToTheHost()
    {
        var parsed = SpicetifyDiagnosis.Parse(FakeExtension.Diagnosis(ready: false));

        Assert.Equal(new SpicetifyDiagnosis(false, 15000, true, false, 0, PlayerThrew), parsed);
    }

    /// <summary>The socket carries several kinds of message, and only one of them is this.</summary>
    [Theory]
    [InlineData("""{"type":"state","playing":true,"volume":0.5}""")]
    [InlineData("""{"type":"faded","to":0}""")]
    [InlineData("""["diagnosis"]""")]
    [InlineData("not json at all")]
    public void Parse_AnythingElse_IsNull(string payload)
        => Assert.Null(SpicetifyDiagnosis.Parse(payload));

    /// <summary>An older extension sends no diagnosis fields at all. Read as not-ready rather
    /// than thrown: a malformed line from a client anyone can reach must not stop the bridge.</summary>
    [Fact]
    public void Parse_AReportMissingEverythingButItsType_IsNotReady()
    {
        var parsed = SpicetifyDiagnosis.Parse("""{"type":"diagnosis"}""");

        Assert.False(parsed?.Ready);
    }

    /// <summary>The extension opens its socket before it knows anything, so its first report says
    /// "not ready" while nothing has gone wrong: that is not the same fault as never binding.</summary>
    [Fact]
    public void SpicetifyApiNeverStarted_TheExtensionHasOnlyJustConnected_IsFalse()
        => Assert.False(
            new SpicetifyDiagnosis(false, 0, true, false, 0, "not ready yet").SpicetifyApiNeverStarted);

    [Fact]
    public void Describe_TheExtensionHasOnlyJustConnected_SaysItIsWaitingRatherThanBroken()
    {
        var described = new SpicetifyDiagnosis(false, 0, true, false, 0, "x").Describe();

        Assert.Contains("still waiting for the player", described);
        Assert.DoesNotContain("never started", described);
    }

    /// <summary>The host asks for a verdict when its grace runs out, so the extension has to have
    /// reached one by then. Held together here since the two numbers live in different languages.</summary>
    [Fact]
    public void TheHostsGraceOutlastsTheExtensionsWait()
        => Assert.True(SpicetifyBridgeSetup.GracePeriodOutlastsTheExtensionsWait);

    private static SpicetifyDiagnosis Diagnosis(bool ready, int platformKeys) => new(
        ready,
        WaitedMilliseconds: ready ? 0 : SpicetifyDiagnosis.VerdictAfterMilliseconds,
        HasSpicetify: true,
        HasPlayer: ready,
        platformKeys,
        Error: ready ? null : PlayerThrew);
}
