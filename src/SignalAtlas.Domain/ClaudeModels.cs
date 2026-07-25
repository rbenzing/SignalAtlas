namespace SignalAtlas.Domain;

/// <summary>
/// The Claude model ids used by the optional cloud paths (SPEC §8.12 analyst, §8.13 enhancement,
/// §18.6). Two tiers on the current Claude 5 family: a balanced default (<see cref="AnalystDefault"/> /
/// <see cref="EnhancementDefault"/>) for phrasing and routine reclassification, and <see cref="Hard"/>
/// for hard multi-step analysis / operator opt-in. These are ONLY reached when cloud is enabled + a key
/// is present + the node is online; the platform is complete with every Claude path disabled (AC-DA0).
/// </summary>
public static class ClaudeModels
{
    /// <summary>Balanced default for analyst phrasing (SPEC §8.12) — the Sonnet tier.</summary>
    public const string AnalystDefault = "claude-sonnet-5";

    /// <summary>Balanced default for a routine enhancement pass (SPEC §8.13).</summary>
    public const string EnhancementDefault = "claude-sonnet-5";

    /// <summary>Hard / opt-in multi-step tier (SPEC §8.12/§8.13) — the Opus tier.</summary>
    public const string Hard = "claude-opus-5";
}
