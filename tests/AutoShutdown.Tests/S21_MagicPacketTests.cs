using AutoShutdown.Core.WakeOnLan;
using Xunit;

namespace AutoShutdown.Tests;

/// <summary>S21-C1：Magic Packet 字节精确性与严格 MAC 校验。</summary>
public sealed class S21_MagicPacketTests
{
    [Fact]
    public void Build_Produces102BytesWith6LeadingFFAnd16MacRepeats()
    {
        byte[] mac = [0x01, 0x02, 0x03, 0x04, 0x05, 0x06];

        var packet = MagicPacket.Build(mac);

        Assert.Equal(102, packet.Length);
        // 前导 6 字节 0xFF
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(0xFF, packet[i]);
        }

        // MAC 重复 16 次，逐字节校验
        for (var repeat = 0; repeat < 16; repeat++)
        {
            for (var i = 0; i < 6; i++)
            {
                Assert.Equal(mac[i], packet[6 + repeat * 6 + i]);
            }
        }
    }

    [Fact]
    public void Build_WhenMacHasWrongLength_Throws()
    {
        byte[] shortMac = [0x01, 0x02, 0x03, 0x04, 0x05];

        Assert.Throws<ArgumentException>(() => MagicPacket.Build(shortMac));
    }

    [Theory]
    [InlineData("AA:BB:CC:DD:EE:FF")]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("AABB.CCDD.EEFF")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("aabbccddeeff")]
    public void MacAddress_ValidFormats_ParseAndCanonicalize(string input)
    {
        Assert.True(MacAddress.TryParse(input, out var mac));
        Assert.Equal(6, mac.Length);
        Assert.Equal("AA:BB:CC:DD:EE:FF", MacAddress.ToCanonical(mac));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("GG:HH:II:JJ:KK:LL")]
    [InlineData("AA:BB:CC:DD:EE")]
    [InlineData("AA:BB:CC:DD:EE:FF:00")]
    [InlineData("AA-BB-CC-DD-EE:FF")]
    [InlineData("AABBCCDDEEF")]
    [InlineData("AABBCCDDEEFG")]
    [InlineData("AABB.CCDD.EEF")]
    [InlineData("AABBCCDDEEFF00")]
    [InlineData("AA::BB:CC:DD:EE:FF")]
    [InlineData(null!)]
    public void MacAddress_InvalidFormats_Rejected(string? input)
    {
        Assert.False(MacAddress.TryParse(input, out _));
    }

    [Fact]
    public void TryBuild_WhenInvalidMac_ReturnsNull()
    {
        Assert.Null(MagicPacket.TryBuild("not-a-mac"));
    }

    [Fact]
    public void TryBuild_WhenValidMac_BuildsExactPacket()
    {
        var packet = MagicPacket.TryBuild("01-02-03-04-05-06");

        Assert.NotNull(packet);
        Assert.Equal(102, packet!.Length);
        Assert.Equal(0xFF, packet[0]);
        Assert.Equal(0x01, packet[6]);
        Assert.Equal(0x06, packet[11]);
        Assert.Equal(0x01, packet[12]); // 第二次重复起点
    }
}
