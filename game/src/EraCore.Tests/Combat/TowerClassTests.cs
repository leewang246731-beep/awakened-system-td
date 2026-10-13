using System.Text.Json;
using EraCore.Combat;
using Xunit;

namespace EraCore.Tests.Combat;

// [F-026] six-class tower framework (SP-1a, 07 #24): JSON `tower.class`
// spellings bind to the frozen enum through the AOT-safe string converter.
public class TowerClassTests
{
    public static TheoryData<TowerClass, string> ClassRows => new()
    {
        // legal — the six frozen spellings (SP-1 L19: JSON 枚举值)
        { TowerClass.Physical, "physical" },
        { TowerClass.Pierce, "pierce" },
        { TowerClass.Splash, "splash" },
        { TowerClass.Energy, "energy" },
        { TowerClass.Slow, "slow" },
        { TowerClass.Support, "support" },
    };

    [Theory] // legal — round trip: serialize the enum, read the JSON name back
    [MemberData(nameof(ClassRows))]
    public void Enum_round_trips_through_frozen_json_spellings(TowerClass cls, string jsonName)
    {
        // [F-026] six-class enum ↔ JSON `tower.class` (SP-1 L19). Root-level
        // JSON string tokens carry their quotes in the serialized text.
        string written = JsonSerializer.Serialize(cls, TowerSpecJsonContext.Default.TowerClass);
        Assert.Equal($"\"{jsonName}\"", written);

        var read = JsonSerializer.Deserialize($"\"{jsonName}\"", TowerSpecJsonContext.Default.TowerClass);
        Assert.Equal(cls, read);
    }

    [Fact] // legal — deserialization is case-insensitive on the frozen names
    public void Enum_reading_is_case_insensitive()
    {
        // [F-026] converter tolerance: "Physical"/"PIERCE" resolve (write side stays lowercase)
        var read = JsonSerializer.Deserialize("\"Physical\"", TowerSpecJsonContext.Default.TowerClass);
        Assert.Equal(TowerClass.Physical, read);
    }

    [Theory] // illegal — anything outside the six frozen spellings is rejected
    [InlineData("\"beam\"")]
    [InlineData("\"tower\"")]
    [InlineData("\"\"")]
    [InlineData("17")]
    public void Enum_rejects_unknown_class_values(string json)
    {
        // [F-026] closed enum: unknown class strings fail deserialization (K-2 strictness)
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize(json, TowerSpecJsonContext.Default.TowerClass));
    }
}
