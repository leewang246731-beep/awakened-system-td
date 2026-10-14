using EraCore.IO;
using EraCore.Narrative;
using EraCore.Presentation;
using Xunit;

namespace EraCore.Tests.Presentation;

// [C-22] [F-122] T-093 人格切换持久层（§10.7 四人格：毒舌/参谋/沙雕/傲娇，
// 只改表达不改能力）：三行——合法（四 id 往返+标签）、边界（缺文件=参谋
// 缺省）、非法（未知 id 保存拒绝/坏 JSON 落缺省）。
public class PersonaSwitchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        DevFileStore.DefaultDevRoot, ".tmp-tests", Guid.NewGuid().ToString("N"));

    private readonly IFileStore _store;

    public PersonaSwitchTests()
    {
        _store = new DevFileStore(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---------------------------------------------------------------- legal

    [Theory] // legal — 四人格 id 往返：保存→重载→解析回同一枚举（§10.7 冻结集）
    [InlineData("advisor")]
    [InlineData("snarky")]
    [InlineData("goofy")]
    [InlineData("tsundere")]
    public void Save_then_load_roundtrips_every_frozen_persona(string personaId)
    {
        PersonaSettingsStore.Save(_store, personaId);

        Assert.Equal(personaId, PersonaSettingsStore.Load(_store).PersonaId);
        Assert.Equal(personaId, PersonaChoice.IdOf(PersonaChoice.TryParse(personaId)!.Value));
    }

    [Fact] // legal — 标签映射齐全（四行 zh 标签与冻结 id 一一对应）
    public void Frozen_ids_carry_zh_labels()
    {
        Assert.Equal(4, PersonaChoice.FrozenIds.Count);
        Assert.Equal("参谋", PersonaChoice.LabelZh("advisor"));
        Assert.Equal("毒舌", PersonaChoice.LabelZh("snarky"));
        Assert.Equal("沙雕", PersonaChoice.LabelZh("goofy"));
        Assert.Equal("傲娇", PersonaChoice.LabelZh("tsundere"));
        Assert.Equal(SystemPersona.Snarky, PersonaChoice.TryParse("snarky"));
    }

    // ---------------------------------------------------------------- boundary

    [Fact] // boundary — 缺文件：工厂缺省=参谋（人格切换是玩家的主动选择）
    public void Load_missing_file_yields_advisor_default()
    {
        Assert.Equal("advisor", PersonaSettingsStore.Load(_store).PersonaId);
        Assert.Equal("advisor", PersonaSettings.Default.PersonaId);
    }

    // ---------------------------------------------------------------- illegal

    [Fact] // illegal — 坏 JSON / 未知人格 id 存档 → 参谋缺省（不阻断启动）
    public void Corrupt_or_unknown_file_falls_back_to_advisor()
    {
        _store.WriteAllBytes(PersonaSettingsStore.RelativePath, "{ broken"u8.ToArray());
        Assert.Equal("advisor", PersonaSettingsStore.Load(_store).PersonaId);

        _store.WriteAllBytes(
            PersonaSettingsStore.RelativePath, """{"persona_id": "dark-lord"}"""u8.ToArray());
        Assert.Equal("advisor", PersonaSettingsStore.Load(_store).PersonaId);
        Assert.Null(PersonaChoice.TryParse("dark-lord"));
    }

    [Fact] // illegal — 保存未知 id 拒绝；null 入参拒绝
    public void Save_rejects_unknown_and_null_arguments()
    {
        Assert.Throws<ArgumentException>(() => PersonaSettingsStore.Save(_store, "celebrity"));
        Assert.Throws<ArgumentNullException>(() => PersonaSettingsStore.Save(_store, null!));
        Assert.Throws<ArgumentNullException>(() => PersonaSettingsStore.Load(null!));
    }
}
