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
        Assert.Throws<ArgumentException>(() => PersonaChoice.PortraitKey("dark-lord"));
    }

    // ---------------------------------------------- T-126 三联动（F-122/F-137）

    [Fact] // legal — [F-122][F-137] 三联动矩阵：4 人格 × 4 时代——音色行在表、
            // 立绘键四张互异、切入自报行在库（台词/音色/立绘三臂同拍可解析）
    public void Every_persona_x_era_resolves_all_three_switch_arms()
    {
        EraCore.Audio.PersonaTimbreTable timbres = EraCore.Tests.Audio.AudioContent.Timbres();
        EraCore.Narrative.PersonaLineBank bank = EraCore.Narrative.PersonaLineBank.Parse(
            EraCore.Tests.Narrative.NarrativeContent.Read("persona-lines.json"), "narrative/persona-lines.json");

        Assert.Equal(4, EraCore.Audio.PersonaTimbreTable.Eras.Count);
        foreach (string personaId in PersonaChoice.FrozenIds)
        {
            SystemPersona persona = PersonaChoice.TryParse(personaId)!.Value;
            foreach (string era in EraCore.Audio.PersonaTimbreTable.Eras)
            {
                // ①音色臂：{era}:{persona} 行在表（off 模式恒中性旁白）
                EraCore.Audio.PersonaTimbre timbre = timbres.Of(persona, era);
                Assert.Equal($"{era}:{personaId}", timbre.Id);
                Assert.Equal(EraCore.Audio.PersonaTimbreTable.NarratorId,
                    EraCore.Audio.VoiceDowngrade.Resolve(persona, era, systemEnabled: false));

                // ②立绘臂：P4 立绘键（与 visual-assets personas 批同名，装载侧另证）
                Assert.StartsWith("persona-", PersonaChoice.PortraitKey(personaId));

                // ③台词臂：切入自报行在库且人格键贯穿
                EraCore.Narrative.PersonaLineRow row = bank.First(persona, EraCore.Narrative.PersonaLineFace.Switch);
                Assert.Equal(persona, row.ToDialogueLine().Persona);
            }
        }

        // 立绘四键互异（四人格四张脸——P4 立绘 1:1）
        Assert.Equal(4, PersonaChoice.PortraitKeys.Distinct().Count());
    }

    [Fact] // legal — [F-137] 立绘键落在 visual-assets.json personas 批（键→素材文件可解析）
    public void Portrait_keys_resolve_to_registered_visual_assets()
    {
        EraCore.Cinematics.VisualAssetCatalog catalog = EraCore.Cinematics.VisualAssetCatalog.Parse(
            EraCore.Tests.Narrative.NarrativeContent.ReadSibling("cinematics", "visual-assets.json"),
            "cinematics/visual-assets.json");

        foreach (string key in PersonaChoice.PortraitKeys)
        {
            Assert.NotNull(catalog.ImageByKey(key)); // P4 personas 批登记键
        }
    }
}
