using System.IO.Compression;
using Templing.Layout;

namespace Templing.Tests;

/// <summary>
/// What a backup from someone else (a file, or a paired device) can't do: put markup into the colour style sheet,
/// or unpack to gigabytes.
/// </summary>
public class SecurityTests
{
    [Theory]
    [InlineData("#0361c3", true)]
    [InlineData("#0361C3", true)]
    [InlineData("#0361c3ff", true)]
    [InlineData("#036", false)]
    [InlineData("0361c3", false)]
    [InlineData("#0361c3;", false)]
    [InlineData("red", false)]
    [InlineData("#0361c3}</style><img src=x onerror=alert(1)>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_hex_colours_count_as_colours(string? value, bool ok) => Assert.Equal(ok, CustomColours.IsColour(value));

    [Fact]
    public void Clean_drops_anything_that_isnt_a_colour()
    {
        var c = new CustomColours
        {
            Light = { ["accent"] = "#112233", ["good"] = "red}</style><script>x</script>" },
            Dark = { ["text"] = "expression(alert(1))", ["card"] = "#445566" },
        };
        c.Clean();
        Assert.Equal(new Dictionary<string, string> { ["accent"] = "#112233" }, c.Light);
        Assert.Equal(new Dictionary<string, string> { ["card"] = "#445566" }, c.Dark);
    }

    [Fact]
    public void The_colour_style_sheet_never_contains_what_isnt_a_colour()
    {
        // as if it came straight from an old row, without Clean
        var c = new CustomColours { Light = { ["accent"] = "red}</style><img src=x onerror=alert(1)>", ["good"] = "#00aa00" } };
        var css = ThemeColours.Css(c);
        Assert.DoesNotContain("<", css);
        Assert.DoesNotContain("red", css);
        Assert.Contains("--good:#00aa00", css);
        Assert.Equal(ThemeColours.Get(null, "accent", false), ThemeColours.Get(c, "accent", false));
    }

    static byte[] Gzip(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionLevel.Optimal)) gz.Write(data);
        return output.ToArray();
    }

    [Fact]
    public void A_backup_that_unpacks_past_the_limit_is_refused()
    {
        var bomb = Gzip(new byte[200_000]);   // a couple of hundred bytes that unpack to 200 KB
        Assert.True(bomb.Length < 1000);
        var ex = Assert.Throws<FormatException>(() => Backup.Gunzip(bomb, max: 100_000));
        Assert.Equal("That backup is too large to restore.", ex.Message);
        Assert.Equal(200_000, Backup.Gunzip(bomb, max: 200_000).Length);
    }
}
