using SignalAtlas.Decode;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M3 — decoder registry dispatch (SPEC §8.4). A frame is routed to the first decoder that
/// claims its protocol; an unknown protocol is a no-op rejection, never a throw
/// (test list: "decoder registry: unknown proto no-ops").
/// </summary>
public class DecodeRegistryTests
{
    /// <summary>A decoder that claims exactly one protocol and echoes a canned frame.</summary>
    private sealed class StubDecoder(string protocol) : IProtocolDecoder
    {
        public string Protocol { get; } = protocol;
        public bool CanDecode(string protocol) =>
            string.Equals(protocol, Protocol, StringComparison.OrdinalIgnoreCase);

        public DecodeOutcome Decode(ReadOnlyMemory<byte> frameBytes) =>
            DecodeOutcome.Decoded(new DecodedFrame(
                Protocol, "test",
                new Dictionary<string, string> { ["tag"] = Protocol },
                1.0,
                new[] { new EvidenceItem("crc", "pass", 1.0) }));
    }

    private static DecoderRegistry Registry() =>
        new(new IProtocolDecoder[] { new StubDecoder("ADS-B"), new StubDecoder("Wi-Fi") });

    // 1. Known protocol routes to its decoder (case-insensitive).
    [Fact]
    public void Decode_KnownProtocol_RoutesToMatchingDecoder()
    {
        var outcome = Registry().Decode("wi-fi", ReadOnlyMemory<byte>.Empty);

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Frame);
        Assert.Equal("Wi-Fi", outcome.Frame!.Identifiers["tag"]);
    }

    // 2. Unknown protocol → rejected no-op, never throws (AC registry no-op).
    [Fact]
    public void Decode_UnknownProtocol_RejectedNoOpNeverThrows()
    {
        var registry = Registry();

        var outcome = registry.Decode("Bluetooth", ReadOnlyMemory<byte>.Empty);

        Assert.False(outcome.Success);
        Assert.Null(outcome.Frame);
        Assert.Contains("no decoder for 'Bluetooth'", outcome.RejectReason);
    }

    // 3. First matching decoder wins when several claim the protocol.
    [Fact]
    public void Decode_FirstMatchingDecoderWins()
    {
        var first = new StubDecoder("FM");
        var second = new StubDecoder("FM");
        var registry = new DecoderRegistry(new IProtocolDecoder[] { first, second });

        var outcome = registry.Decode("FM", ReadOnlyMemory<byte>.Empty);

        Assert.True(outcome.Success);
        Assert.Equal("FM", outcome.Frame!.Identifiers["tag"]);
    }

    // 4. No decoders registered → still a no-op rejection, not a throw.
    [Fact]
    public void Decode_NoDecoders_Rejects()
    {
        var registry = new DecoderRegistry(Array.Empty<IProtocolDecoder>());

        var outcome = registry.Decode("ADS-B", ReadOnlyMemory<byte>.Empty);

        Assert.False(outcome.Success);
    }
}
