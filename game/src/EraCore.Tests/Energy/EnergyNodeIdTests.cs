using EraCore.Energy;
using Xunit;

namespace EraCore.Tests.Energy;

// [C-08] EnergyNodeId — opaque ledger identity primitive (T-033).
public class EnergyNodeIdTests
{
    [Fact] // legal — value equality, the ledger keys nodes by it
    public void Same_value_ids_are_equal_and_hash_equal()
    {
        var a = new EnergyNodeId(7);
        var b = new EnergyNodeId(7);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);
    }

    [Fact] // legal — distinct values are distinct ids
    public void Different_value_ids_are_unequal()
    {
        Assert.NotEqual(new EnergyNodeId(1), new EnergyNodeId(2));
    }

    [Fact] // legal — the wrapped value round-trips (registration order and hashes depend on it)
    public void Value_round_trips()
    {
        Assert.Equal(42L, new EnergyNodeId(42).Value);
    }

    [Fact] // legal — usable as a dictionary key alongside mixed producers/consumers
    public void Works_as_dictionary_key()
    {
        var map = new System.Collections.Generic.Dictionary<EnergyNodeId, string>
        {
            [new EnergyNodeId(1)] = "labor-camp",
            [new EnergyNodeId(2)] = "tower",
        };

        Assert.Equal("labor-camp", map[new EnergyNodeId(1)]);
        Assert.Equal("tower", map[new EnergyNodeId(2)]);
    }
}
