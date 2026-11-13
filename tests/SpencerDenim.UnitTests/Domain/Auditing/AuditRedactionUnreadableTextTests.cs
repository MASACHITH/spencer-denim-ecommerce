using System.Text.Json;
using SpencerDenim.Domain.Auditing;

namespace SpencerDenim.UnitTests.Domain.Auditing;

/// <summary>#50: the fail-closed path for text the parser refuses (cases found by the independent tester).</summary>
public sealed class AuditRedactionUnreadableTextTests
{
    private static void AssertNoSecret(string? input)
    {
        var r = AuditRedaction.Redact(input)!;
        Assert.DoesNotContain("SECRET", r, StringComparison.Ordinal);
        using var _ = JsonDocument.Parse(r, new JsonDocumentOptions { MaxDepth = 512 });
    }

    // ---- escaped names in unreadable (duplicate-key) text, at depth / in arrays / on ProofAssetId
    [Theory]
    [InlineData("""{"a":1,"a":2,"u":{"v":{"pass\u0077ord":"SECRET"}}}""")]
    [InlineData("""{"a":1,"a":2,"rows":[{"x":1},{"Secur\u0069tyStamp":"SECRET"}]}""")]
    [InlineData("""[{"a":1,"a":2},{"\u0050\u0041\u0053\u0053\u0057\u004F\u0052\u0044":"SECRET"}]""")]
    [InlineData("""{"a":1,"a":2,"pro\u006FfAssetId":"SECRET"}""")]
    [InlineData("""{"a":1,"a":2,"ProofAsset\u0049d":"SECRET"}""")]
    [InlineData("""{"a":1,"a":2,"clientSecre\u0074":"SECRET"}""")]
    [InlineData("""{"pass\u0077ord":"SECRET","pass\u0077ord":"SECRET"}""")]
    [InlineData("""{"pass\u0077ord":"SECRET", /* c */ "a":1,"a":2,}""")]
    public void Escaped_names_in_unreadable_text_fail_closed(string json) => AssertNoSecret(json);

    // ---- duplicate key comes AFTER the secret vs BEFORE it
    [Fact]
    public void Duplicate_after_escaped_secret() =>
        AssertNoSecret("""{"pass\u0077ord":"SECRET","z":{"a":1,"a":2}}""");

    // ---- the reader rejects partway: before and after the escaped name
    [Theory]
    [InlineData("""{"a":1 "pass\u0077ord":"SECRET"}""")]                 // missing comma before
    [InlineData("""{"pass\u0077ord":"SECRET" "a":1}""")]                 // missing comma after
    [InlineData("""{"a":1}{"pass\u0077ord":"SECRET"}""")]                // two roots
    [InlineData("""{"a":"\x","pass\u0077ord":"SECRET"}""")]              // invalid escape before
    [InlineData("""{"pass\u0077ord":"SECRET","a":"\uZZZZ"}""")]           // invalid escape after
    [InlineData("""{"a":\uD800,"pass\u0077ord":"SECRET"}""")]
    [InlineData("""{"pass\u0077ord":"SECRET""")]                          // truncated
    public void Reader_rejects_partway_fails_closed(string json) => AssertNoSecret(json);

    // ---- lone surrogate in an escaped NAME (GetString throws)
    [Fact]
    public void Lone_surrogate_in_name_fails_closed() =>
        AssertNoSecret("""{"a":1,"a":2,"pass\u0077ord\uD800":"SECRET"}""");

    // ---- very deep unreadable text
    [Theory]
    [InlineData(100)]
    [InlineData(250)]
    [InlineData(260)]
    [InlineData(1000)]
    public void Deep_unreadable_with_escaped_secret(int depth)
    {
        var json = "{\"a\":1,\"a\":2,\"d\":" + string.Concat(Enumerable.Repeat("{\"b\":", depth))
            + "{\"pass\\u0077ord\":\"SECRET\"}" + new string('}', depth) + "}";
        AssertNoSecret(json);
    }

    // ---- deep READABLE text (parses, but the writer may refuse depth > 64) with an escaped secret
    [Theory]
    [InlineData(70)]
    [InlineData(200)]
    public void Deep_readable_with_escaped_secret(int depth)
    {
        var json = string.Concat(Enumerable.Repeat("{\"b\":", depth)) + "{\"pass\\u0077ord\":\"SECRET\"}" + new string('}', depth);
        AssertNoSecret(json);
    }

    // ---- case folding through escapes
    [Fact]
    public void Escaped_upper_case_name() => AssertNoSecret("""{"a":1,"a":2,"PASS\u0057ORD":"SECRET"}""");

    // ---- things that must NOT become Unreadable
    [Theory]
    [InlineData("""{"a":1,"a":2,"path":"C:\\temp\\x"}""")]                  // dup keys, backslash in a value, no listed name
    [InlineData("""{"a":1,"a":2,"msg":"line\nbreak \u00e9"}""")]
    [InlineData("""{"a":1,"a":2,"pass\u0077ords":"keep"}""")]              // escaped NON-listed name
    [InlineData("""{"a":1,"a":2,"pass\/word":"keep"}""")]                   // unescapes to pass/word
    public void Unreadable_but_clean_text_is_left_as_written(string json)
    {
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Theory]
    [InlineData("""{"path":"C:\\temp","note":"\u00e9"}""")]                 // readable, escapes, clean
    [InlineData("""{"pass\u0077ords":1}""")]
    public void Readable_clean_text_with_escapes_is_same_instance(string json)
    {
        Assert.Same(json, AuditRedaction.Redact(json));
    }

    [Fact]
    public void Bom_plus_unreadable_escaped() => AssertNoSecret("\uFEFF{\"a\":1,\"a\":2,\"pass\\u0077ord\":\"SECRET\"}");

    [Fact]
    public void Multiple_boms() => AssertNoSecret("\uFEFF\uFEFF{\"pass\\u0077ord\":\"SECRET\"}");

    [Fact]
    public void Long_s_folds_conservatively()
    {
        // U+017F folds to S under OrdinalIgnoreCase; over-redaction is the safe side, just recording behaviour.
        var r = AuditRedaction.Redact("{\"pa\u017F\u017Fword\":\"SECRET\",\"k\":1}")!;
        Assert.NotNull(r);
    }

    /// <summary>
    /// Text that never opens an object or array is not JSON at all: it is returned as written, backslashes or not, so the
    /// column's ISJSON check still refuses a caller that wrote non-JSON instead of a placeholder being stored quietly.
    /// </summary>
    [Theory]
    [InlineData(@"C:\temp\file")]
    [InlineData(@"not json \n at all")]
    public void Non_json_text_with_a_backslash_is_returned_as_written(string text) =>
        Assert.Same(text, AuditRedaction.Redact(text));

    [Fact]
    public void Text_given_up_on_inside_an_object_with_an_escape_fails_closed() =>
        Assert.Equal(AuditRedaction.Unreadable, AuditRedaction.Redact(@"{""a"":1,""a"":2,""b"":""\u00e9"" ""c"""));

    [Fact]
    public void Never_throws_on_garbage()
    {
        foreach (var s in new[] { "{", "}", "[", "\"", "\\", "{\"\\", "{\"\\u", "{\"\\uD8", "\uFEFF", "\uFEFF\\", "{\"a\\", new string('[', 5000) + "\\" })
        {
            _ = AuditRedaction.Redact(s);
        }
    }
}
