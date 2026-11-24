namespace Larva.Polyglot.Tests;

/// <summary>
/// Names into words and back. The words must stay those of System.Bindings' <c>Names.Words</c> (Library/System.Bindings/Names.ant),
/// which splits C names the other way: the cases its documentation gives are here.
/// </summary>
public sealed class NamingTests
{
    [Theory]
    [InlineData("GPUDevice", "GPU|Device")]
    [InlineData("SDL_INIT_VIDEO", "SDL|INIT|VIDEO")]
    [InlineData("windowID", "window|ID")]
    [InlineData("parseJSONValue", "parse|JSON|Value")]
    [InlineData("HTTPClient", "HTTP|Client")]
    [InlineData("IOStream", "IO|Stream")]
    [InlineData("MaxDepth", "Max|Depth")]
    [InlineData("__init__", "init")]
    [InlineData("x", "x")]
    public void Words_split_at_underscores_case_changes_and_acronym_ends(string name, string words) =>
        Assert.Equal(words, string.Join("|", Naming.Words(name)));

    [Theory]
    // The cases System.Bindings documents for its NameCase.
    [InlineData("windowID", NameStyle.Pascal, "WindowID")]
    [InlineData("WINDOW_FLAGS", NameStyle.Pascal, "WindowFlags")]
    [InlineData("WindowFlags", NameStyle.Camel, "windowFlags")]
    [InlineData("WindowFlags", NameStyle.UpperSnake, "WINDOW_FLAGS")]
    [InlineData("WindowFlags", NameStyle.Snake, "window_flags")]
    // Acronyms: capitals kept where the style keeps capitals, lower case where it has none.
    [InlineData("GetHTTPResponse", NameStyle.Snake, "get_http_response")]
    [InlineData("HTTPClient", NameStyle.Camel, "httpClient")]
    [InlineData("GPUDevice", NameStyle.Pascal, "GPUDevice")]
    [InlineData("JsonParser", NameStyle.Lower, "jsonparser")]
    [InlineData("JsonParser", NameStyle.Kebab, "json-parser")]
    [InlineData("Anything", NameStyle.Keep, "Anything")]
    // One word in capitals is an acronym (a namespace `IO`), not a screaming name.
    [InlineData("IO", NameStyle.Pascal, "IO")]
    [InlineData("IO", NameStyle.Snake, "io")]
    public void Shape_joins_the_words_in_a_style(string name, NameStyle style, string shaped) =>
        Assert.Equal(shaped, Naming.Shape(name, style));

    [Theory]
    [InlineData("GPUDevice", AcronymPolicy.Keep, "GPUDevice")]
    [InlineData("GPUDevice", AcronymPolicy.ShortUpper, "GpuDevice")]
    [InlineData("IOStream", AcronymPolicy.ShortUpper, "IOStream")]
    [InlineData("IOStream", AcronymPolicy.Words, "IoStream")]
    [InlineData("HTTPClient", AcronymPolicy.Words, "HttpClient")]
    public void Acronym_policies(string name, AcronymPolicy policy, string shaped) =>
        Assert.Equal(shaped, Naming.Shape(name, NameStyle.Pascal, policy));

    [Fact]
    public void A_name_that_starts_with_a_digit_is_made_writable() => Assert.Equal("_3D", Naming.Writable("3D"));

    [Theory]
    [InlineData("python", TargetLanguage.Python)]
    [InlineData("PY", TargetLanguage.Python)]
    [InlineData("cs", TargetLanguage.CSharp)]
    [InlineData("ts", TargetLanguage.TypeScript)]
    [InlineData("h", TargetLanguage.C)]
    [InlineData("java", TargetLanguage.Java)]
    public void Languages_are_found_by_id_or_alias(string name, TargetLanguage language) => Assert.Equal(language, TargetRules.Parse(name));

    [Fact]
    public void An_unknown_language_is_none() => Assert.Null(TargetRules.Parse("cobol"));
}
