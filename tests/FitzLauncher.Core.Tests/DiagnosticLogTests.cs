using FitzLauncher.Core.Diagnostics;

namespace FitzLauncher.Core.Tests;

public class DiagnosticLogTests
{
    [Fact]
    public void OffByDefault_StoresNothing()
    {
        var log = new DiagnosticLog();
        log.Write("hello");
        Assert.Empty(log.Snapshot());
    }

    [Fact]
    public void On_KeepsLastLinesOnly()
    {
        var log = new DiagnosticLog(3) { Enabled = true };
        for (var i = 0; i < 5; i++) log.Write($"line {i}");
        var lines = log.Snapshot();
        Assert.Equal(3, lines.Count);
        Assert.EndsWith("line 4", lines[^1]);
    }

    [Fact]
    public void TurningOff_ClearsTrace()
    {
        var log = new DiagnosticLog { Enabled = true };
        log.Write("x");
        log.Enabled = false;
        Assert.Empty(log.Snapshot());
    }

    [Theory]
    [InlineData("apikey=abc123XYZ", "abc123XYZ")]
    [InlineData("SWID: {1234-ABCD}", "{1234-ABCD}")]
    [InlineData("espn_s2=AEBxyz%2Fabc", "AEBxyz")]
    [InlineData("Authorization: Bearer eyJhbGciOi", "eyJhbGciOi")]
    [InlineData("https://x.test/a?token=SECRETVAL&b=1", "SECRETVAL")]
    [InlineData("password: \"hunter two\"", "hunter")]
    public void Sanitize_HidesSecretValues(string input, string secret)
    {
        var clean = DiagnosticLog.Sanitize(input);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[REDACTED]", clean);
    }

    [Fact]
    public void Sanitize_KeepsOrdinaryText_AndHidesUserName()
    {
        Assert.Equal("Timer set to 0.5 ms", DiagnosticLog.Sanitize("Timer set to 0.5 ms"));
        Assert.DoesNotContain("jeff", DiagnosticLog.Sanitize(@"C:\Users\jeff\AppData\x"));
    }

    [Fact]
    public void Package_HasFactsTrace_AndNoSecrets()
    {
        var text = DiagnosticLog.BuildPackage("Fitz 1", [("Timer", "0.5 ms"), ("Note", "token=abc")], ["12:00:00.000  ok"], true);
        Assert.Contains("Timer: 0.5 ms", text);
        Assert.Contains("12:00:00.000  ok", text);
        Assert.DoesNotContain("token=abc", text);
    }

    [Fact]
    public void Package_SaysWhenTraceWasOff()
    {
        Assert.Contains("trace was off", DiagnosticLog.BuildPackage("Fitz 1", [], [], false));
    }
}
