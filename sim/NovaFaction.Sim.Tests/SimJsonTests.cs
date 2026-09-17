using NovaFaction.Sim.Content;
using NovaFaction.Sim.Numerics;

namespace NovaFaction.Sim.Tests;

public class SimJsonTests
{
    [Fact]
    public void ParsesAllValueKinds()
    {
        JsonValue root = SimJson.Parse(
            "{\"n\": null, \"t\": true, \"f\": false, \"i\": -42, \"x\": 1.25, \"s\": \"hi\", " +
            "\"a\": [1, [2], {}], \"o\": {\"k\": []}}");

        Assert.Equal(JsonKind.Object, root.Kind);
        Assert.True(root.Get("n").IsNull);
        Assert.True(root.Get("t").AsBool());
        Assert.False(root.Get("f").AsBool());
        Assert.Equal(-42, root.Get("i").AsInt());
        Assert.Equal(Fix.Parse("1.25"), root.Get("x").AsFix());
        Assert.Equal("hi", root.Get("s").AsString());

        IReadOnlyList<JsonValue> a = root.Get("a").AsArray();
        Assert.Equal(3, a.Count);
        Assert.Equal(1, a[0].AsInt());
        Assert.Equal(2, a[1].AsArray()[0].AsInt());
        Assert.Empty(a[2].Members);
        Assert.Empty(root.Get("o").Get("k").AsArray());
    }

    [Fact]
    public void ObjectMembersKeepFileOrder()
    {
        JsonValue root = SimJson.Parse("{\"zebra\": 1, \"apple\": 2, \"mango\": 3}");
        Assert.Equal(new[] { "zebra", "apple", "mango" }, root.Members.Select(m => m.Key));
    }

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("-0", 0L)]
    [InlineData("123", 123L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void AsLong_ReadsWholeNumbers(string json, long expected)
    {
        Assert.Equal(expected, SimJson.Parse(json).AsLong());
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    [InlineData("18446744073709551616")]
    [InlineData("1.5")]
    [InlineData("1e3")]
    public void AsLong_RejectsOutOfRangeOrFractional(string json)
    {
        Assert.Throws<SimJsonException>(() => SimJson.Parse(json).AsLong());
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    public void AsInt_RejectsOutOfRange(string json)
    {
        var ex = Assert.Throws<SimJsonException>(() => SimJson.Parse(json).AsInt());
        Assert.Contains("int range", ex.Message);
    }

    [Theory]
    [InlineData("0.35", 22938L)] // 0.35 * 65536 = 22937.6, rounded to nearest
    [InlineData("-1.5", -98304L)]
    [InlineData("10", 655360L)]
    [InlineData("0", 0L)]
    public void AsFix_ParsesDecimalsExactly(string json, long expectedRaw)
    {
        Assert.Equal(expectedRaw, SimJson.Parse(json).AsFix().Raw);
    }

    [Theory]
    [InlineData("1e2")]
    [InlineData("1.5E-3")]
    [InlineData("999999999999999999999")]
    public void AsFix_RejectsExponentsAndOverflow(string json)
    {
        var ex = Assert.Throws<SimJsonException>(() => SimJson.Parse(json).AsFix());
        Assert.Contains("fixed-point", ex.Message);
    }

    [Fact]
    public void WrongTypeAccess_ReportsKindAndPosition()
    {
        JsonValue root = SimJson.Parse("{\n  \"a\": \"text\"\n}", "test.json");
        var ex = Assert.Throws<SimJsonException>(() => root.Get("a").AsInt());
        Assert.Equal(2, ex.Line);
        Assert.Equal(8, ex.Column);
        Assert.Equal("test.json (line 2, column 8): expected a number but found a string.", ex.Message);
    }

    [Fact]
    public void Get_MissingKey_Throws()
    {
        var ex = Assert.Throws<SimJsonException>(() => SimJson.Parse("{}").Get("nope"));
        Assert.Contains("\"nope\"", ex.Message);
    }

    [Fact]
    public void DecodesEscapes()
    {
        string s = SimJson.Parse("\"q\\\" b\\\\ s\\/ \\b\\f\\n\\r\\t \\u0041\\u00e9 \\ud83d\\ude00\"").AsString();
        Assert.Equal("q\" b\\ s/ \b\f\n\r\t A\u00e9 \U0001F600", s);
    }

    [Fact]
    public void AcceptsRawUnicodeAndLeadingBom()
    {
        Assert.Equal("héllo 😀", SimJson.Parse("\uFEFF \"héllo 😀\" ").AsString());
    }

    [Fact]
    public void AcceptsAllJsonWhitespace()
    {
        Assert.Equal(7, SimJson.Parse(" \t\r\n[ \r\n 7 \t]\n").AsArray()[0].AsInt());
    }

    [Fact]
    public void AcceptsNestingUpToMaxDepth()
    {
        string json = new string('[', SimJson.MaxDepth) + new string(']', SimJson.MaxDepth);
        Assert.Equal(JsonKind.Array, SimJson.Parse(json).Kind);
    }

    [Theory]
    // input, expected line, expected column, message fragment
    [InlineData("", 1, 1, "empty")]
    [InlineData("   \n  ", 2, 3, "empty")]
    [InlineData("{\"a\": 1,}", 1, 9, "trailing comma")]
    [InlineData("[1, 2,]", 1, 7, "trailing comma")]
    [InlineData("{\"a\" 1}", 1, 6, "expected ':'")]
    [InlineData("{a: 1}", 1, 2, "double-quoted key")]
    [InlineData("{'a': 1}", 1, 2, "double-quoted key")]
    [InlineData("['a']", 1, 2, "double quotes")]
    [InlineData("{\"a\": 1 \"b\": 2}", 1, 9, "expected ',' or '}'")]
    [InlineData("[1 2]", 1, 4, "expected ',' or ']'")]
    [InlineData("{\"a\": 1", 1, 8, "end of document")]
    [InlineData("[1,", 1, 4, "end of document")]
    [InlineData("\"abc", 1, 1, "unterminated string")]
    [InlineData("\"a\nb\"", 1, 3, "control character")]
    [InlineData("\"\\x\"", 1, 3, "invalid escape")]
    [InlineData("\"\\u12G4\"", 1, 6, "4 hex digits")]
    [InlineData("\"\\ud83d\"", 1, 2, "unpaired high surrogate")]
    [InlineData("\"\\ud83d\\u0041\"", 1, 2, "unpaired high surrogate")]
    [InlineData("\"\\ude00\"", 1, 2, "unpaired low surrogate")]
    [InlineData("01", 1, 1, "leading zeros")]
    [InlineData("-", 1, 2, "expected a digit")]
    [InlineData("1.", 1, 3, "after the decimal point")]
    [InlineData(".5", 1, 1, "unexpected '.'")]
    [InlineData("+1", 1, 1, "unexpected '+'")]
    [InlineData("1e", 1, 3, "exponent")]
    [InlineData("1.2.3", 1, 4, "in number")]
    [InlineData("12abc", 1, 3, "in number")]
    [InlineData("NaN", 1, 1, "unexpected 'N'")]
    [InlineData("Infinity", 1, 1, "unexpected 'I'")]
    [InlineData("tru", 1, 1, "expected true")]
    [InlineData("truex", 1, 1, "expected true")]
    [InlineData("nul", 1, 1, "expected null")]
    [InlineData("True", 1, 1, "unexpected 'T'")]
    [InlineData("// c\n1", 1, 1, "comments")]
    [InlineData("1 /* c */", 1, 3, "after the end")]
    [InlineData("{} {}", 1, 4, "after the end")]
    [InlineData("{\"a\": 1, \"a\": 2}", 1, 10, "duplicate key \"a\"")]
    [InlineData("{\n  \"x\": 1,\n  \"y\": ,\n}", 3, 8, "expected a value")]
    [InlineData("\u00A0 1", 1, 1, "unexpected")]
    public void RejectsMalformedInput(string json, int line, int column, string fragment)
    {
        var ex = Assert.Throws<SimJsonException>(() => SimJson.Parse(json, "bad.json"));
        Assert.Contains(fragment, ex.Message);
        Assert.Equal(line, ex.Line);
        Assert.Equal(column, ex.Column);
        Assert.StartsWith("bad.json (line " + line + ", column " + column + "): ", ex.Message);
    }

    [Fact]
    public void RejectsNestingBeyondMaxDepth()
    {
        string json = new string('[', SimJson.MaxDepth + 1) + new string(']', SimJson.MaxDepth + 1);
        var ex = Assert.Throws<SimJsonException>(() => SimJson.Parse(json));
        Assert.Contains("nesting", ex.Message);
    }

    [Fact]
    public void ColumnCountsFromLineStartAfterCrLf()
    {
        var ex = Assert.Throws<SimJsonException>(() => SimJson.Parse("{\r\n\"a\": 1,\r\n  x}"));
        Assert.Equal(3, ex.Line);
        Assert.Equal(3, ex.Column);
    }

    [Fact]
    public void Parse_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => SimJson.Parse(null!));
    }
}
