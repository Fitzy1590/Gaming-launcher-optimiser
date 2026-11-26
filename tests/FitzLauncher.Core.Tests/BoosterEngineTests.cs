using FitzLauncher.Core.Audio;
using FitzLauncher.Core.Diagnostics;
using FitzLauncher.Core.Models;
using FitzLauncher.Core.Telemetry;
using FitzLauncher.Core.Upscalers;
using Xunit;

namespace FitzLauncher.Core.Tests;

public class LogRedactorTests
{
    [Theory]
    [InlineData("api_key=abc123", "api_key=[REDACTED]")]
    [InlineData("Authorization: Bearer abc.def-1", "Authorization: [REDACTED]")]
    [InlineData("""{"token":"xyz789","a":1}""", """{"token":"[REDACTED]","a":1}""")]
    [InlineData("GET /x?id=1&token=s3cr3t&b=2", "GET /x?id=1&token=[REDACTED]&b=2")]
    [InlineData("Cookie: SWID=guid-1; ESPN_S2=longvalue", "Cookie: [REDACTED]; ESPN_S2=[REDACTED]")]
    [InlineData("password: hunter2", "password: [REDACTED]")]
    public void Secrets_are_masked(string input, string expected) => Assert.Equal(expected, LogRedactor.Redact(input));

    [Fact]
    public void Plain_text_and_user_path_handling()
    {
        Assert.Equal("Game started at 60 FPS", LogRedactor.Redact("Game started at 60 FPS"));
        Assert.Equal(@"C:\Users\<user>\x.log", LogRedactor.Redact(@"C:\Users\Jeff\x.log"));
    }

    [Fact]
    public void Package_prefixes_every_line_and_redacts()
    {
        var text = DiagnosticPackage.Build(new DateTimeOffset(2026, 11, 26, 1, 2, 3, TimeSpan.Zero),
            [("system", ["OS: Windows 11"]), ("log", ["a\nsecret=zzz"])]);
        var lines = text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("[header]", lines[0]);
        Assert.Contains("[system] OS: Windows 11", lines);
        Assert.Contains("[log] secret=[REDACTED]", lines);
        Assert.DoesNotContain("zzz", text);
    }
}

public class EqualizerApoTests
{
    [Fact]
    public void Include_is_added_once_and_removed_leaving_user_lines()
    {
        const string user = "Preamp: -2 dB\r\nInclude: mine.txt\r\n";
        var on = EqualizerApo.AddInclude(user);
        Assert.Equal(on, EqualizerApo.AddInclude(on));
        Assert.True(EqualizerApo.HasInclude(on));
        Assert.Equal(user, EqualizerApo.RemoveInclude(on));
    }

    [Fact]
    public void Empty_config_round_trips_and_missing_newline_is_handled()
    {
        Assert.Equal("", EqualizerApo.RemoveInclude(EqualizerApo.AddInclude("")));
        Assert.Equal("Preamp: 0 dB", EqualizerApo.RemoveInclude(EqualizerApo.AddInclude("Preamp: 0 dB")).TrimEnd('\n'));
    }

    [Fact]
    public void Profiles() { Assert.Empty(EqualizerApo.ProfileLines(AudioProfile.Off)); Assert.All(new[] { AudioProfile.Clarity, AudioProfile.Footsteps }, p => Assert.StartsWith("Preamp:", EqualizerApo.ProfileLines(p)[0])); }
}

public class FramePacingAndFloorTests
{
    [Fact]
    public void Pacing_wording()
    {
        Assert.Equal("–", FramePacing.Describe(0, 0));
        Assert.Equal("90% · smooth", FramePacing.Describe(60, 54));
        Assert.Equal("70% · some hitches", FramePacing.Describe(60, 42));
        Assert.Equal("40% · uneven", FramePacing.Describe(60, 24));
    }

    [Theory]
    [InlineData(UpscalerDllKind.DlssSuperResolution, "3.5.10.0", true)]
    [InlineData(UpscalerDllKind.DlssSuperResolution, "3.7.0.0", false)]
    [InlineData(UpscalerDllKind.XeSS, "1.2.0.0", true)]
    [InlineData(UpscalerDllKind.FsrDx12, "0.1", false)]
    [InlineData(UpscalerDllKind.XeSS, null, false)]
    public void Floor(UpscalerDllKind k, string? v, bool below) => Assert.Equal(below, UpscalerLibrary.BelowFloor(k, v));
}
