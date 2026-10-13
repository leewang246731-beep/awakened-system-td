using EraCore.IO;
using EraCore.Secrets;
using Xunit;

namespace EraCore.Tests.Secrets;

// [F-084] C-17 密钥容器序列化（两个后端共享的落盘格式）——合法/边界/非法
public class SecretsContainerTests : SecretsSandbox
{
    // ---------------------------------------------------------------- legal

    [Fact]
    public void Roundtrip_preserves_every_entry()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zhipu-glm"] = "sk-1234567890abcdef",
            ["xiaomi-mimo"] = "sk-abcdefgh1234",
            ["custom-relay"] = "rel-key-01",
            ["unicode-provider-智谱"] = "unicode-value-密钥",
        };
        var files = NewFiles();

        SecretsContainer.Write(files, "secrets.bin", map);
        var loaded = SecretsContainer.Read(files, "secrets.bin");

        Assert.Equal(map.Count, loaded.Count);
        foreach (var (id, value) in map)
        {
            Assert.Equal(value, loaded[id]);
        }
    }

    [Fact]
    public void Encode_is_deterministic_regardless_of_insertion_order()
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zhipu-glm"] = "one",
            ["xiaomi-mimo"] = "two",
        };
        var b = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["xiaomi-mimo"] = "two",
            ["zhipu-glm"] = "one",
        };

        Assert.Equal(SecretsContainer.Encode(a), SecretsContainer.Encode(b));
    }

    // -------------------------------------------------------------- boundary

    [Fact]
    public void Missing_file_reads_as_an_empty_map()
    {
        Assert.Empty(SecretsContainer.Read(NewFiles(), "absent.bin"));
    }

    [Fact]
    public void Empty_map_roundtrips_and_keeps_a_stable_header()
    {
        var files = NewFiles();
        var empty = new Dictionary<string, string>();

        SecretsContainer.Write(files, "secrets.bin", empty);

        // 边界：清除后的稳定落盘形态 = 仅 magic+version+count(0) 的 16 字节头。
        var raw = files.ReadAllBytes("secrets.bin");
        Assert.Equal(16, raw.Length);
        Assert.Empty(SecretsContainer.Read(files, "secrets.bin"));
    }

    [Fact]
    public void Empty_string_values_are_preserved_verbatim()
    {
        var files = NewFiles();
        var map = new Dictionary<string, string> { ["zhipu-glm"] = "" };

        SecretsContainer.Write(files, "secrets.bin", map);

        Assert.Equal(string.Empty, SecretsContainer.Read(files, "secrets.bin")["zhipu-glm"]);
    }

    // ---------------------------------------------------------------- illegal

    [Fact]
    public void Garbage_bytes_are_rejected_as_corruption()
    {
        // 非法面：密钥文件拒绝"尽力解析"——坏魔数/坏版本/坏计数一律抛
        // IOException（fail loud），绝不返回半份映射。
        Assert.Throws<IOException>(() =>
            SecretsContainer.Decode("NOTASEC!"u8.ToArray()));
        Assert.Throws<IOException>(() =>
            SecretsContainer.Decode(new byte[15])); // 头都不完整
    }

    [Fact]
    public void Tampered_version_and_count_are_rejected()
    {
        var bytes = SecretsContainer.Encode(
            new Dictionary<string, string> { ["zhipu-glm"] = "one" });

        var badVersion = (byte[])bytes.Clone();
        badVersion[8] = 99; // version 字段（小端 int32 首字节）
        Assert.Throws<IOException>(() => SecretsContainer.Decode(badVersion));

        var badCount = (byte[])bytes.Clone();
        badCount[12] = 0x7f; // count 字段指向非法大数
        Assert.Throws<IOException>(() => SecretsContainer.Decode(badCount));
    }

    [Fact]
    public void Truncated_and_extended_bodies_are_rejected()
    {
        var bytes = SecretsContainer.Encode(
            new Dictionary<string, string> { ["zhipu-glm"] = "one" });

        var truncated = bytes[..^1];
        Assert.Throws<IOException>(() => SecretsContainer.Decode(truncated));

        var extended = bytes.Append((byte)0).ToArray();
        Assert.Throws<IOException>(() => SecretsContainer.Decode(extended));
    }

    [Fact]
    public void Forged_duplicate_provider_entries_are_rejected()
    {
        // 非法面：手工伪造含重复 provider id 的容器必须被解析层拒绝，
        // 不允许"后者覆盖前者"的静默合并。
        var one = SecretsContainer.Encode(
            new Dictionary<string, string> { ["zhipu-glm"] = "first" });
        var two = SecretsContainer.Encode(
            new Dictionary<string, string> { ["zhipu-glm"] = "second" });

        // one 的头(16B)+条目 与 two 的条目 拼接，并把 count 改为 2
        var entrySpan = two[16..];
        var forged = one.Concat(entrySpan).ToArray();
        forged[12] = 2;
        forged[13] = 0;
        forged[14] = 0;
        forged[15] = 0;

        Assert.Throws<IOException>(() => SecretsContainer.Decode(forged));
    }
}
