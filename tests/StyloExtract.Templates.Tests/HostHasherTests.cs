using FluentAssertions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class HostHasherTests
{
    [Fact]
    public void Hash_SameHostSameKey_ProducesIdenticalBytes()
    {
        var key = new byte[32];
        Array.Fill(key, (byte)42);
        var h = new HostHasher(key);

        var a = h.Hash("example.com");
        var b = h.Hash("example.com");

        a.Should().Equal(b);
        a.Length.Should().Be(16);
    }

    [Fact]
    public void Hash_DifferentHosts_ProduceDifferentBytes()
    {
        var key = new byte[32];
        var h = new HostHasher(key);

        h.Hash("example.com").Should().NotEqual(h.Hash("other.com"));
    }

    [Fact]
    public void Hash_HostCaseDifference_ProducesIdenticalBytes()
    {
        var key = new byte[32];
        var h = new HostHasher(key);

        h.Hash("Example.COM").Should().Equal(h.Hash("example.com"));
    }
}
