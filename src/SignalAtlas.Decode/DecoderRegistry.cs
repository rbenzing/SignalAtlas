using SignalAtlas.Domain;

namespace SignalAtlas.Decode;

/// <summary>
/// Dispatches a demodulated frame to the first registered decoder that claims its protocol
/// (SPEC §8.4). Unknown protocol is a no-op: returns a rejection, never throws
/// (AC: "decoder registry: unknown proto no-ops"). Holds no reference to any concrete decoder.
/// </summary>
public sealed class DecoderRegistry(IEnumerable<IProtocolDecoder> decoders) : IDecoderRegistry
{
    private readonly IReadOnlyList<IProtocolDecoder> _decoders = decoders.ToList();

    public DecodeOutcome Decode(string protocol, ReadOnlyMemory<byte> frameBytes)
    {
        foreach (var decoder in _decoders)
        {
            if (decoder.CanDecode(protocol))
                return decoder.Decode(frameBytes);
        }

        return DecodeOutcome.Rejected($"no decoder for '{protocol}'");
    }
}
