namespace SignalAtlas.Domain;

/// <summary>
/// A per-frequency transient RF store — cleared on retune so a new band starts clean; devices/observations
/// are NOT transient.
/// </summary>
public interface ITransientStore
{
    void Clear();
}
