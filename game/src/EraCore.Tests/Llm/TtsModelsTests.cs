using System.Text.Json;
using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// TTS 请求/响应模型契约（T-138, F-140 预留接口面）：audio/speech 线名
/// 往返（source-gen）、格式枚举线名精确、未知格式拒绝、响应模型音频语义。
/// </summary>
// [F-140][C-16] T-138：TTS 语音通道预留——请求模型往返
public class TtsModelsTests
{
    [Fact]
    public void Request_roundtrips_through_sourcegen_context()
    {
        // legal: audio/speech 线名（model/input/voice/response_format）往返全等
        var request = new TtsSpeechRequest
        {
            Model = TtsProviderPresets.MimoTtsModelId,
            Input = "觉醒系统上线。",
            Voice = "narrator-a",
            ResponseFormat = TtsAudioFormat.Mp3,
        };

        string json = JsonSerializer.Serialize(request, TtsJsonContext.Default.TtsSpeechRequest);

        // 线名断言用 ASCII 片段（默认转义器会转义非 ASCII 字符；中文内容经
        // 往返全等在下方覆盖）
        Assert.Contains("\"model\":\"mimo-v2.5-tts\"", json);
        Assert.Contains("\"input\":\"", json);
        Assert.Contains("\"voice\":\"narrator-a\"", json);
        Assert.Contains("\"response_format\":\"mp3\"", json);

        var parsed = JsonSerializer.Deserialize(json, TtsJsonContext.Default.TtsSpeechRequest);
        Assert.NotNull(parsed);
        Assert.Equal(request, parsed);
        Assert.Equal("觉醒系统上线。", parsed.Input);
    }

    [Fact]
    public void Default_request_is_wav_with_blank_selector_fields()
    {
        // boundary: 出厂请求=wav 容器、空 model/voice（预留态，未指向任何服务）
        var request = new TtsSpeechRequest();

        Assert.Equal(TtsAudioFormat.Wav, request.ResponseFormat);
        Assert.Equal(string.Empty, request.Model);
        Assert.Equal(string.Empty, request.Input);
        Assert.Equal(string.Empty, request.Voice);
    }

    [Fact]
    public void Format_wire_names_are_exact_three_lowercase_strings()
    {
        // legal: 三种容器线名逐字精确（小写）
        Assert.Equal(
            "\"wav\"", JsonSerializer.Serialize(TtsAudioFormat.Wav, TtsJsonContext.Default.TtsAudioFormat));
        Assert.Equal(
            "\"mp3\"", JsonSerializer.Serialize(TtsAudioFormat.Mp3, TtsJsonContext.Default.TtsAudioFormat));
        Assert.Equal(
            "\"pcm\"", JsonSerializer.Serialize(TtsAudioFormat.Pcm, TtsJsonContext.Default.TtsAudioFormat));
    }

    [Fact]
    public void Unknown_format_string_is_rejected_not_silently_defaulted()
    {
        // illegal: 未知格式串抛 JsonException（禁静默回退——与 LlmProtocol 同律）
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TtsAudioFormat>("\"flac\"", TtsJsonContext.Default.TtsAudioFormat));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<TtsAudioFormat>("\"WAV\"", TtsJsonContext.Default.TtsAudioFormat));
    }

    [Fact]
    public void Response_has_audio_only_with_bytes()
    {
        // boundary: 空字节数组=未合成；有字节=HasAudio（拒绝态永不构造本响应）
        var empty = new TtsSpeechResponse(TtsAudioFormat.Wav, []);
        Assert.False(empty.HasAudio);

        var withAudio = new TtsSpeechResponse(TtsAudioFormat.Pcm, [1, 2, 3]);
        Assert.True(withAudio.HasAudio);
        Assert.Equal(TtsAudioFormat.Pcm, withAudio.Format);
    }
}
