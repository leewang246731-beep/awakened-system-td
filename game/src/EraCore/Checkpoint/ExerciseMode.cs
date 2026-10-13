using EraCore.Sim;

namespace EraCore.Checkpoint;

/// <summary>
/// Run parameters of one exercise (演习) attempt (v3.2 §3: 终局关失败送一次
/// 免费演习；敌人×0.5). The scenario flag is folded INTO the seed parameters:
/// the exercise runs on a derived seed, never the level seed, so an exercise
/// replay record can never collide with the real run's — the replay key
/// carries the scenario, and a fresh derivation from the same level seed
/// always yields the same exercise seed (deterministic, no stored state).
/// </summary>
/// <param name="LevelSeed">Seed of the real level.</param>
/// <param name="RunSeed">Derived exercise run seed (flag-mixed).</param>
/// <param name="EnemyCountMultiplier">Enemy count multiplier of the scenario (0.5).</param>
public sealed record ExerciseRunParameters(long LevelSeed, long RunSeed, double EnemyCountMultiplier);

/// <summary>
/// Exercise-mode scenario transform (C-04, T-057; F-004): derives run
/// parameters and halves the enemy count multiplier of a level config.
/// </summary>
public static class ExerciseMode
{
    /// <summary>Enemy count multiplier of exercise runs (敌人×0.5, v3.2 §3).</summary>
    public const double EnemyCountMultiplier = 0.5;

    /// <summary>
    /// Flag salt of the derivation — arbitrary but frozen: changing it
    /// re-rolls every historical exercise seed (replay keys break).
    /// </summary>
    private const ulong ExerciseFlagSalt = 0xD1B54A32D192ED03UL;

    /// <summary>
    /// Derives the exercise run parameters of a level seed: the flag enters
    /// through a SplitMix64-style mix, so RunSeed ≠ LevelSeed and the map
    /// (levelSeed → runSeed) is a fixed bijection branch of the seed space.
    /// </summary>
    public static ExerciseRunParameters DeriveRunParameters(long levelSeed) =>
        new(levelSeed, MixExerciseFlag(levelSeed), EnemyCountMultiplier);

    /// <summary>
    /// Halves the enemy count multiplier of the wave schedule (敌人×0.5 as
    /// the count-side scaling; threat knobs stay untouched — hp/interval are
    /// NOT part of the exercise discount).
    /// </summary>
    /// <exception cref="ArgumentNullException">config is null.</exception>
    public static SimulationConfig EnemyHalved(SimulationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config with
        {
            Waves = config.Waves with { Scaling = config.Waves.Scaling * EnemyCountMultiplier },
        };
    }

    /// <summary>
    /// Splits the scenario flag into the seed bits: xor the salt, then the
    /// SplitMix64 finalizer (bijective — no two level seeds collide onto one
    /// exercise seed, and no exercise seed collides onto the identity branch).
    /// </summary>
    private static long MixExerciseFlag(long levelSeed)
    {
        ulong z = (ulong)levelSeed ^ ExerciseFlagSalt;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return unchecked((long)(z ^ (z >> 31)));
    }
}
