using System.Net;
using CartoonArchive.Storage;

namespace CartoonArchive.Tests;

/// <summary>
/// The SSRF allow/deny decision. These cases are the reason the guard exists: if any of
/// them flips to the wrong answer, the URL-upload feature becomes a probe for whatever is
/// reachable from the container.
/// </summary>
public class SsrfGuardTests
{
    [Theory]
    // Loopback
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")] // whole 127/8, not just .0.1
    [InlineData("::1")]
    // RFC1918 private ranges
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")] // lower edge of 172.16/12
    [InlineData("172.31.255.255")] // upper edge of 172.16/12
    // Link-local, which is also where cloud instance metadata lives
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    // Other reserved space
    [InlineData("0.0.0.0")]
    [InlineData("192.0.0.1")] // 192.0.0.0/24
    [InlineData("100.64.0.1")] // CGNAT
    [InlineData("100.127.255.255")]
    [InlineData("224.0.0.1")] // multicast
    [InlineData("240.0.0.1")] // reserved
    [InlineData("255.255.255.255")]
    // IPv6
    [InlineData("fe80::1")] // link-local
    [InlineData("fc00::1")] // unique local, low bit of the fc00::/7 mask
    [InlineData("fd12:3456::1")] // unique local, high bit of the fc00::/7 mask
    [InlineData("ff02::1")] // multicast
    public void Rejects_private_and_reserved_addresses(string address)
    {
        Assert.True(SsrfGuard.IsPrivateOrLocal(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("172.15.255.255")] // just below 172.16/12 - must stay allowed
    [InlineData("172.32.0.1")] // just above 172.16/12 - must stay allowed
    [InlineData("100.63.255.255")] // just below CGNAT
    [InlineData("100.128.0.1")] // just above CGNAT
    [InlineData("11.0.0.1")] // adjacent to 10/8
    [InlineData("192.167.255.255")] // adjacent to 192.168/16
    [InlineData("192.0.1.1")] // outside 192.0.0.0/24
    [InlineData("223.255.255.255")] // just below multicast
    [InlineData("2606:4700::1111")] // public IPv6
    [InlineData("fbff::1")] // just below fc00::/7
    public void Allows_public_addresses(string address)
    {
        Assert.False(SsrfGuard.IsPrivateOrLocal(IPAddress.Parse(address)));
    }

    /// <summary>
    /// ::ffff:127.0.0.1 is loopback wearing an IPv6 costume. The guard normalizes
    /// IPv4-mapped addresses before applying the IPv4 rules; without that step every
    /// private range below is reachable by writing it in mapped form.
    /// </summary>
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.1.1")]
    [InlineData("::ffff:169.254.169.254")]
    public void Rejects_ipv4_mapped_private_addresses(string address)
    {
        var addr = IPAddress.Parse(address);
        Assert.True(addr.IsIPv4MappedToIPv6, "test case is not actually IPv4-mapped");
        Assert.True(SsrfGuard.IsPrivateOrLocal(addr));
    }

    [Fact]
    public void Allows_ipv4_mapped_public_address()
    {
        Assert.False(SsrfGuard.IsPrivateOrLocal(IPAddress.Parse("::ffff:8.8.8.8")));
    }
}
