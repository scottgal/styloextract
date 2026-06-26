using System.IO.Hashing;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class MinimalHtmlTokenizerTests
{
    [Fact]
    public void Emits_open_tag_for_simple_tag()
    {
        ReadOnlySpan<byte> bytes = "<div>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.IsClose.Should().BeFalse();
        evt.TagNameHash.Should().Be(XxHash3.HashToUInt64("div"u8));
    }

    [Fact]
    public void Emits_close_tag()
    {
        ReadOnlySpan<byte> bytes = "</span>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.IsClose.Should().BeTrue();
        evt.TagNameHash.Should().Be(XxHash3.HashToUInt64("span"u8));
    }

    [Fact]
    public void Returns_false_when_no_more_tags()
    {
        ReadOnlySpan<byte> bytes = "<div></div>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out _).Should().BeTrue();
        tokenizer.TryReadTag(out _).Should().BeTrue();
        tokenizer.TryReadTag(out _).Should().BeFalse();
    }

    [Fact]
    public void Emits_two_consecutive_tags_with_correct_open_close()
    {
        ReadOnlySpan<byte> bytes = "<p>hi</p>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var open).Should().BeTrue();
        open.IsClose.Should().BeFalse();
        open.TagNameHash.Should().Be(XxHash3.HashToUInt64("p"u8));

        tokenizer.TryReadTag(out var close).Should().BeTrue();
        close.IsClose.Should().BeTrue();
        close.TagNameHash.Should().Be(XxHash3.HashToUInt64("p"u8));
    }
}
