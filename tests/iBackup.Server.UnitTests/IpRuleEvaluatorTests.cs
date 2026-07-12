using iBackup.Server.Application.Common;
using Xunit;

namespace iBackup.Server.UnitTests;

public class IpRuleEvaluatorTests
{
    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values);
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.5")]
    [InlineData("::1")]
    [InlineData(null)]
    [InlineData("")]
    public void Loopback_and_unknown_are_always_allowed_even_with_no_rules(string? ip)
    {
        Assert.True(IpRuleEvaluator.IsAllowed(ip, Empty, Empty));
    }

    [Fact]
    public void Default_deny_blocks_unlisted_remote_ip()
    {
        Assert.False(IpRuleEvaluator.IsAllowed("203.0.113.7", Empty, Empty));
    }

    [Fact]
    public void Wildcard_whitelist_allows_any_remote_ip()
    {
        Assert.True(IpRuleEvaluator.IsAllowed("203.0.113.7", Set("*"), Empty));
    }

    [Fact]
    public void Exact_whitelist_allows_only_that_ip()
    {
        var whitelist = Set("203.0.113.7");
        Assert.True(IpRuleEvaluator.IsAllowed("203.0.113.7", whitelist, Empty));
        Assert.False(IpRuleEvaluator.IsAllowed("203.0.113.8", whitelist, Empty));
    }

    [Fact]
    public void Blacklist_takes_precedence_over_wildcard_whitelist()
    {
        // '*' allows everything, but an explicit blacklist entry still denies.
        Assert.False(IpRuleEvaluator.IsAllowed("203.0.113.7", Set("*"), Set("203.0.113.7")));
        // other IPs remain allowed by the wildcard
        Assert.True(IpRuleEvaluator.IsAllowed("203.0.113.9", Set("*"), Set("203.0.113.7")));
    }

    [Fact]
    public void Blacklist_cannot_lock_out_loopback()
    {
        // Even a wildcard blacklist must not deny the local host.
        Assert.True(IpRuleEvaluator.IsAllowed("127.0.0.1", Empty, Set("*")));
        Assert.True(IpRuleEvaluator.IsAllowed("::1", Empty, Set("*")));
    }

    [Fact]
    public void Wildcard_blacklist_denies_all_remote_ips()
    {
        Assert.False(IpRuleEvaluator.IsAllowed("203.0.113.7", Set("*"), Set("*")));
    }

    [Fact]
    public void Unparseable_ip_is_denied()
    {
        Assert.False(IpRuleEvaluator.IsAllowed("not-an-ip", Set("*"), Empty));
    }

    [Fact]
    public void Ipv6_is_normalized_for_matching()
    {
        // Rules are stored normalized (NormalizeRuleValue on insert); an incoming
        // request in any equivalent textual form must still match.
        var whitelist = Set(IpRuleEvaluator.NormalizeRuleValue("2001:0db8:0000::1"));
        Assert.True(IpRuleEvaluator.IsAllowed("2001:db8:0000:0000:0000:0000:0000:0001", whitelist, Empty));
    }

    [Theory]
    [InlineData("*", true)]
    [InlineData("192.168.1.10", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("999.999.999.999", false)]
    [InlineData("192.168.1.0/24", false)] // CIDR not supported (yet)
    [InlineData("hello", false)]
    public void Rule_value_validation(string value, bool expected)
    {
        Assert.Equal(expected, IpRuleEvaluator.IsValidRuleValue(value));
    }

    [Fact]
    public void NormalizeRuleValue_keeps_wildcard_and_canonicalizes_ip()
    {
        Assert.Equal("*", IpRuleEvaluator.NormalizeRuleValue("*"));
        Assert.Equal("2001:db8::1", IpRuleEvaluator.NormalizeRuleValue("2001:0db8:0000::1"));
        Assert.Equal("192.168.0.1", IpRuleEvaluator.NormalizeRuleValue("  192.168.0.1 "));
    }
}
