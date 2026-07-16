namespace SignalAtlas.Collector;

/// <summary>
/// RECEIVE-ONLY abstraction over a HackRF SDR (SPEC §4.1, via SoapySDR). The receive-only
/// invariant (SPEC §4.2 L1) is enforced at the type level: this interface deliberately exposes
/// NO transmit/send/tx member, so Signal Atlas software can never invoke the radio's physical
/// transmit path. A reflection test asserts the absence of any such member.
/// </summary>
public interface IHackRfDevice
{
    /// <summary>True only when a HackRF is connected over USB and the SoapySDR bindings loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>Open the device for RECEIVE, tuning to the given center frequency, sample rate, and RX gain.</summary>
    void OpenReceive(long centerFreqHz, int sampleRateHz, double gainDb);

    /// <summary>
    /// Read the next block of interleaved signed-8-bit I/Q samples (HackRF native format).
    /// An empty buffer signals end-of-stream.
    /// </summary>
    ReadOnlyMemory<byte> ReadBlock();

    /// <summary>Stop receiving and release the device.</summary>
    void Close();
}
