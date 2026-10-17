using EraCore.Llm;
using Xunit;

namespace EraCore.Tests.Llm;

/// <summary>
/// TTS 预设端点常量契约（T-138, F-140）：mimo-v2.5-tts 走 OpenAI 兼容
/// audio/speech 口径、与已实测 MiMo chat 预设同源 base_url、语音名不虚构。
/// </summary>
// [F-140][C-16] T-138：mimo-v2.5-tts 端点常量预留
public class TtsProviderPresetsTests
{
    [Fact]
    public void Mimo_preset_targets_openai_compatible_speech_endpoint()
    {
        // legal: 模型 id 小写、端点路径=audio/speech（OpenAI 兼容口径）
        var preset = TtsProviderPresets.MimoV25Tts();

        Assert.Equal("mimo-v2.5-tts", preset.Model);
        Assert.Equal(TtsProviderPresets.MimoTtsModelId, preset.Model);
        Assert.Equal("audio/speech", preset.EndpointPath);
        Assert.Equal(TtsProviderPresets.MimoTtsEndpointPath, preset.EndpointPath);
        Assert.Equal("xiaomi-mimo-tts", preset.Id);
    }

    [Fact]
    public void Speech_url_composes_base_plus_endpoint_path()
    {
        // legal: 完整语音 URL=base（去尾斜杠）+audio/speech
        var url = TtsProviderPresets.MimoV25Tts().SpeechUrl;

        Assert.Equal("https://api.xiaomimimo.com/v1/audio/speech", url.AbsoluteUri);
    }

    [Fact]
    public void Mimo_tts_base_url_matches_the_live_verified_chat_preset()
    {
        // legal: 与 T-072 实测 chat 预设同一服务根（密钥/白名单同源的依据）
        Assert.Equal(
            LlmProviderPresets.XiaomiMiMo().BaseUrl,
            TtsProviderPresets.MimoV25Tts().BaseUrl);
    }

    [Fact]
    public void Reserved_voice_field_stays_empty_until_the_endpoint_names_voices()
    {
        // boundary: 端点未开通——语音名录未经验证，留空而非虚构值
        Assert.Equal(string.Empty, TtsProviderPresets.MimoV25Tts().DefaultVoice);
    }
}
