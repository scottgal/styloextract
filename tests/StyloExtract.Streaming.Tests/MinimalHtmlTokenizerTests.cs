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

    [Fact]
    public void Tag_without_class_attribute_has_zero_class_hash()
    {
        ReadOnlySpan<byte> bytes = "<div>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.ClassHash.Should().Be(0UL);
    }

    [Fact]
    public void Extracts_class_attribute_with_double_quotes()
    {
        ReadOnlySpan<byte> bytes = "<div class=\"foo\">"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.ClassHash.Should().Be(XxHash3.HashToUInt64("foo"u8));
    }

    [Fact]
    public void Extracts_class_attribute_with_single_quotes()
    {
        ReadOnlySpan<byte> bytes = "<div class='bar baz'>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.ClassHash.Should().Be(XxHash3.HashToUInt64("bar baz"u8));
    }

    [Fact]
    public void Extracts_class_when_other_attributes_precede_it()
    {
        ReadOnlySpan<byte> bytes = "<div id=\"x\" data-y=\"1\" class=\"foo\">"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.ClassHash.Should().Be(XxHash3.HashToUInt64("foo"u8));
    }

    [Fact]
    public void Substring_collision_does_not_count_as_class_attribute()
    {
        // "myclass=" should NOT match "class=" — must be at a word boundary
        ReadOnlySpan<byte> bytes = "<div myclass=\"x\">"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.ClassHash.Should().Be(0UL);
    }

    [Fact]
    public void Skips_script_body_does_not_emit_inner_tags()
    {
        ReadOnlySpan<byte> bytes = "<script>alert(\"<x></x>\")</script><p></p>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var open).Should().BeTrue();
        open.TagNameHash.Should().Be(XxHash3.HashToUInt64("script"u8));
        open.IsClose.Should().BeFalse();

        // Next emitted tag must be </script>, NOT <x> or </x>
        tokenizer.TryReadTag(out var close).Should().BeTrue();
        close.TagNameHash.Should().Be(XxHash3.HashToUInt64("script"u8));
        close.IsClose.Should().BeTrue();

        // Then the <p> outside the script body
        tokenizer.TryReadTag(out var p).Should().BeTrue();
        p.TagNameHash.Should().Be(XxHash3.HashToUInt64("p"u8));
        p.IsClose.Should().BeFalse();
    }

    [Fact]
    public void Skips_style_body_does_not_emit_inner_tags()
    {
        ReadOnlySpan<byte> bytes = "<style>.a { content: \"<x>\"; }</style><p></p>"u8;
        var tokenizer = new MinimalHtmlTokenizer(bytes);

        tokenizer.TryReadTag(out var open).Should().BeTrue();
        open.TagNameHash.Should().Be(XxHash3.HashToUInt64("style"u8));

        tokenizer.TryReadTag(out var close).Should().BeTrue();
        close.TagNameHash.Should().Be(XxHash3.HashToUInt64("style"u8));
        close.IsClose.Should().BeTrue();

        tokenizer.TryReadTag(out var p).Should().BeTrue();
        p.TagNameHash.Should().Be(XxHash3.HashToUInt64("p"u8));
    }
}
