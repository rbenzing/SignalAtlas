using System.Security.Cryptography;
using System.Text;

namespace SignalAtlas.Domain;

/// <summary>
/// Produces a stable GUID from a string seed so the deterministic core (SPEC §6.1 P5)
/// never depends on <see cref="Guid.NewGuid"/>. Same seed → same GUID, always.
/// </summary>
public static class DeterministicGuid
{
    public static Guid From(string seed)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(seed), hash);
        return new Guid(hash[..16]);
    }
}
