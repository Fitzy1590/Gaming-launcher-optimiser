using FitzLauncher.Core.Config;

namespace FitzLauncher.Core.Tests;

public class IniDocumentTests
{
    private const string Sample =
        "; user comment\r\n" +
        "[SystemSettings]\r\n" +
        "r.MotionBlurQuality=4\r\n" +
        "\r\n" +
        "[/Script/Engine.GameUserSettings]\r\n" +
        "+Array=one\r\n" +
        "bUseVSync=False\r\n";

    [Fact]
    public void Set_existing_key_changes_only_that_line_and_keeps_crlf()
    {
        var doc = IniDocument.Parse(Sample);
        var previous = doc.Set("SystemSettings", "r.MotionBlurQuality", "0");

        Assert.Equal("4", previous);
        Assert.Equal(Sample.Replace("r.MotionBlurQuality=4", "r.MotionBlurQuality=0"), doc.ToString());
    }

    [Fact]
    public void Set_new_key_inserts_before_trailing_blank_line_of_section()
    {
        var doc = IniDocument.Parse(Sample);
        Assert.Null(doc.Set("systemsettings", "t.MaxFPS", "117"));
        var text = doc.ToString();
        Assert.Contains("r.MotionBlurQuality=4\r\nt.MaxFPS=117\r\n\r\n[/Script", text);
        Assert.Equal("117", doc.Get("SystemSettings", "t.maxfps"));
    }

    [Fact]
    public void Set_creates_missing_section_at_end()
    {
        var doc = IniDocument.Parse(Sample);
        doc.Set("ScalabilityGroups", "sg.ShadowQuality", "2");
        Assert.EndsWith("[ScalabilityGroups]\r\nsg.ShadowQuality=2\r\n", doc.ToString());
    }

    [Fact]
    public void Empty_document_gets_section_and_key()
    {
        var doc = IniDocument.Parse("");
        doc.Set("Framerate", "Limit", "117");
        Assert.Equal("[Framerate]\nLimit=117\n", doc.ToString());
    }

    [Fact]
    public void Unreal_array_entries_are_never_treated_as_keys()
    {
        var doc = IniDocument.Parse(Sample);
        Assert.Null(doc.Get("/Script/Engine.GameUserSettings", "+Array"));
        Assert.Equal("False", doc.GetSection("/Script/Engine.GameUserSettings")["bUseVSync"]);
    }

    [Fact]
    public void Remove_then_round_trip_restores_original()
    {
        var doc = IniDocument.Parse(Sample);
        doc.Set("SystemSettings", "t.MaxFPS", "117");
        Assert.True(doc.Remove("SystemSettings", "t.MaxFPS"));
        Assert.Equal(Sample, doc.ToString());
    }
}

public class KeyValueFileTests
{
    [Fact]
    public void Set_updates_in_place_and_appends_new_keys()
    {
        var kv = KeyValueFile.Parse("gfx-enable-gfx-jobs=0\nwait-for-native-debugger=0\n");
        Assert.Equal("0", kv.Set("gfx-enable-gfx-jobs", "1"));
        Assert.Null(kv.Set("gfx-enable-native-gfx-jobs", "1"));
        Assert.Equal("gfx-enable-gfx-jobs=1\nwait-for-native-debugger=0\ngfx-enable-native-gfx-jobs=1\n", kv.ToString());
    }
}
