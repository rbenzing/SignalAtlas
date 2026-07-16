namespace SignalAtlas.Collector;

/// <summary>
/// Stub SoapySDR-backed <see cref="IHackRfDevice"/> (SPEC §4.1, G3/§18.1). The real implementation
/// is the field-hardware step (SPEC §8.1 M8): it needs SoapySDR P/Invoke bindings (libSoapySDR /
/// SoapyHackRF) and a HackRF One connected over USB. Until that native layer exists,
/// <see cref="IsAvailable"/> is <c>false</c> so the collector gracefully falls back to the
/// file/synthetic sources and the app runs fully offline with zero hardware.
///
/// RECEIVE-ONLY: even the eventual real implementation must bind ONLY the SoapySDR RX stream
/// (SOAPY_SDR_RX). The transmit path is never wired — the receive-only invariant (SPEC §4.2 L1)
/// is sacred.
/// </summary>
public sealed class SoapyHackRfDevice : IHackRfDevice
{
    // No SoapySDR native bindings or connected HackRF present in this build. Real availability
    // would probe SoapySDRDevice_enumerate for a "driver=hackrf" match over USB.
    public bool IsAvailable => false;

    public void OpenReceive(long centerFreqHz, int sampleRateHz, double gainDb) =>
        throw new NotSupportedException(
            "SoapyHackRfDevice is a stub: real HackRF receive needs SoapySDR P/Invoke bindings " +
            "and a HackRF connected over USB (SPEC §4.1, field-hardware step M8). " +
            "IsAvailable is false, so the collector should fall back to the file/synthetic source.");

    public ReadOnlyMemory<byte> ReadBlock() =>
        throw new NotSupportedException(
            "SoapyHackRfDevice is a stub: no SoapySDR RX stream available (SPEC §4.1).");

    public void Close() =>
        throw new NotSupportedException(
            "SoapyHackRfDevice is a stub: nothing to close (SPEC §4.1).");
}
