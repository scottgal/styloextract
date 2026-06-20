using FluentAssertions;
using StyloExtract.Templates.Serialization;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class PqGramVectorCodecTests
{
    [Fact]
    public void Encode_Decode_Roundtrips()
    {
        var src = new Dictionary<string, double>
        {
            ["*,*,html,body,*,*"] = 3,
            ["body,main,article,h1,p,p"] = 5
        };

        var bytes = PqGramVectorCodec.Encode(src);
        var decoded = PqGramVectorCodec.Decode(bytes);

        decoded.Should().BeEquivalentTo(src);
    }

    [Fact]
    public void Encode_EmptyDictionary_ProducesValidBytes()
    {
        var bytes = PqGramVectorCodec.Encode(new Dictionary<string, double>());
        var decoded = PqGramVectorCodec.Decode(bytes);
        decoded.Should().BeEmpty();
    }
}
