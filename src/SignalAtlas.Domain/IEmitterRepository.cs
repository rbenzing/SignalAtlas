namespace SignalAtlas.Domain;

/// <summary>
/// Read + upsert access to correlated emitters (SPEC §7.2 emitters, §8.5). The live pipeline
/// loads <see cref="All"/> at the start of a run so correlation is stateful across runs, and
/// <see cref="Upsert"/>s each emitter it produces (idempotent on the deterministic emitter id, §7.8).
/// </summary>
public interface IEmitterRepository
{
    IReadOnlyList<Emitter> All();
    void Upsert(Emitter e);
}
