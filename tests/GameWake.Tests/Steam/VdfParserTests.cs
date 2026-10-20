using GameWake.Providers.Steam;

namespace GameWake.Tests.Steam;

public sealed class VdfParserTests
{
    [Fact]
    public void Parses_libraryfolders_nested_format()
    {
        const string text = """
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"C:\\Program Files (x86)\\Steam"
            		"label"		""
            		"apps"
            		{
            			"570"		"1"
            		}
            	}
            	"1"
            	{
            		"path"		"D:\\Games"
            	}
            }
            """;

        var root = VdfParser.Parse(text);
        var folders = root["libraryfolders"];
        Assert.NotNull(folders);
        Assert.Equal(@"C:\Program Files (x86)\Steam", folders!["0"]!.GetString("path"));
        Assert.Equal(@"D:\Games", folders["1"]!.GetString("path"));
        Assert.Equal("1", folders["0"]!["apps"]!.GetString("570"));
    }

    [Fact]
    public void Parses_legacy_flat_libraryfolders()
    {
        const string text = """
            "LibraryFolders"
            {
            	"TimeNextStatsReport"		"0"
            	"1"		"D:\\Games"
            }
            """;

        var root = VdfParser.Parse(text);
        Assert.Equal(@"D:\Games", root["LibraryFolders"]!["1"]!.Value);
    }

    [Fact]
    public void Parses_escaped_quotes_and_comments()
    {
        const string text = """
            "AppState"
            {
            	// a comment
            	"name"		"Half-Life \"Alyx\""
            }
            """;

        var name = VdfParser.Parse(text)["AppState"]!.GetString("name");
        Assert.Equal("Half-Life \"Alyx\"", name);
    }

    [Fact]
    public void Throws_on_unterminated_string()
    {
        var ex = Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"AppState\" { \"name\" \"broken"));
        Assert.Contains("Unterminated string", ex.Message);
    }

    [Fact]
    public void Throws_on_unbalanced_brace()
    {
        Assert.Throws<VdfParseException>(() => VdfParser.Parse("\"AppState\" { \"name\" \"x\""));
    }

    [Fact]
    public void Throws_on_unexpected_close_brace()
    {
        Assert.Throws<VdfParseException>(() => VdfParser.Parse("}"));
    }

    [Fact]
    public void TryParse_returns_false_for_corrupt_input()
    {
        Assert.False(VdfParser.TryParse("\"x\" {", out var root, out var error));
        Assert.Null(root);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Throws_when_document_is_too_large()
    {
        Assert.Throws<VdfParseException>(() => VdfParser.Parse(new string('a', VdfParser.MaxLength + 1)));
    }
}
