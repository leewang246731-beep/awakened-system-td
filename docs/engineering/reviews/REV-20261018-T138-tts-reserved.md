# REV-20261018-T138 — TTS 语音通道预留（mimo-v2.5-tts 接口面）

| 项 | 内容 |
|---|---|
| 任务 | T-138（08 v1.15/1.20 W8 批 4；行 216：TTS 语音通道预留，S 级，验收=接口验证，**不进首发验收**） |
| 特性 | F-140（TTS 语音通道，远期）｜C-16（LLM/BYOK 通道族） |
| 依据 | 08 任务计划 T-138 行「mimo-v2.5-tts 接口预留」；AGENTS 纪律 5（BYOK 密钥）/8（每任务一 commit）/10（诚实汇报）；既有 BYOK 架构（LlmGateway 门序/ISecretStore 纪律 RL5/F-084） |
| 交付物 | `EraCore/Llm/TtsModels.cs`+`TtsProviderPresets.cs`+`TtsChannel.cs`（接口层）；`LlmConfig.cs` 预留 `tts` 段+`LlmConfigLoader` 校验/Normalize；`SettingsFormState` 预留文案常量+tts 保留；设置页 `TtsReservedRow` 灰置条目（scene+panel+smoke 判据）；测试 `TtsModelsTests`/`TtsProviderPresetsTests`/`TtsChannelTests`+`SettingsFormStateTests` 增量；本 REV |
| 环境 | 本机开发机（D 盘纪律：`NUGET_PACKAGES=D:\nuget`、`DOTNET_ROOT=D:\dotnet`）；无任何真实 TTS 网络调用（端点未开通） |

## 一、预留边界声明（本 REV 核心）

**本任务是接口与配置面的预留，不是功能实现。** 边界逐条：

1. **零真实调用**：mimo TTS 端点未开通，本任务未发出任何 TTS 网络请求、未录制任何 TTS cassette、未验证任何真实语音输出。`ITtsProvider` **没有任何实现**（含测试件以外）——它是未来接线的端口（seam），非半截实现。
2. **绝不谎报成功**：`TtsChannelStatus.Ok` 在本版本**不可达**——四道门（Disabled→NotConfigured→MissingKey→NotImplemented）全过后停在 `NotImplemented`（预留未实现），文案明示「本次调用未发出任何网络请求」。测试 `Ok_status_is_unreachable_in_this_build` 钉死该边界。
3. **端点常量只声明口径**：`audio/speech`（OpenAI 兼容）+`mimo-v2.5-tts`+MiMo chat 同源 base_url（`https://api.xiaomimimo.com/v1`，与 T-072 实测 chat 预置逐字相等——测试钉死）。**语音名（voice）不虚构**：`DefaultVoice` 留空，待端点开通后随实测落名。
4. **密钥纪律同构 BYOK**：`tts` 段无任何 key 字段；未来语音调用复用 `tts.provider` 指向的 provider 行密钥（ISecretStore，RL5/F-084）。**无 Key 零请求**——通道在触网前检查 ISecretStore，空 Key 即 `MissingKey` 拒绝，与 LlmGateway 同构（测试钉死：provider 端口零调用）。
5. **设置页不可交互**：「语音（预留）」行为灰置纯 Label 说明（任务卡 verbatim 文案），**无任何可点击开关**——smoke 判据逐一遍历行内节点断言无 `BaseButton` 家族控件。
6. **不动 08/AGENTS/config/**：主管记账与密钥目录零触碰。

## 二、接口面清单

| 面 | 成员 | 说明 |
|---|---|---|
| 请求模型 | `TtsSpeechRequest`（model/input/voice/response_format） | OpenAI 兼容 audio/speech 请求体；source-gen 往返（`TtsJsonContext`，D-05） |
| 格式枚举 | `TtsAudioFormat`（Wav/Mp3/Pcm，线名 wav/mp3/pcm 小写精确） | 未知串抛 `JsonException` 禁静默回退（与 `LlmProtocol` 同律） |
| 响应模型 | `TtsSpeechResponse`（Format+Audio 字节，`HasAudio`） | 二进制载荷永不进配置/日志面 |
| Provider 抽象 | `ITtsProvider`（`Endpoint`+`SpeakAsync`） | 未来接线端口；本版本零实现零调用 |
| 端点预设 | `TtsProviderPresets.MimoV25Tts()`+`MimoTtsModelId`/`MimoTtsEndpointPath` 常量 | `SpeechUrl` = base（去尾斜杠）+ `audio/speech` |
| 状态位 | `TtsChannelStatus`（Ok/Disabled/NotConfigured/MissingKey/NotImplemented） | Ok 本版本不可达（§一.2） |
| 通道门面 | `TtsChannel.SpeakAsync` | 门序四段，每段双语文案、零网络、Audio=null |

## 三、配置面（llm.config.json 预留 tts 段）

- `LlmTtsConfig`：`enable`（**出厂 false**）/`provider`（=providers[].id，未来取其 ISecretStore 密钥）/`model`（默认 `mimo-v2.5-tts`）/`voice`（默认空）——无 key 字段（§一.4）。
- 装载：`Normalize` 补全缺席段（旧版无 tts 的配置文件装载后=默认 off，测试以手写旧版文档钉死）；启用态才校验——`tts 已启用但 provider/model/voice 不完整`或`tts.provider 'x' 不在 providers 列表中`；禁用态零约束（与禁用模板行同律）。
- 序列化：`LlmJsonContext` 注册 `LlmTtsConfig`；出厂保存产物自带 `"tts":{"enable":false,...}` 段（配置面自文档化）。`SettingsFormState.BuildConfig` 逐字保留基线 tts 段（BYOK 行编辑不丢/不重置 tts；测试钉死）。

## 四、设置页面

`settings_panel.tscn` 新增 `TtsReservedRow`（HBoxContainer，`modulate` 灰置）：Caption「语音（预留）」+Notice「语音通道已预留，功能随后续版本开放」（`SettingsFormState.TtsReserved*` 常量，任务卡 verbatim）。三处接线：WireNodes 取节点、静态文案上屏（与 CodingPlanNotice 同路）、`ApplyTierSurfaces` 可见性矩阵计入常显行（非 BYOK 可编辑字段，Web 版约束下不隐藏）。headless smoke 增判据：行可见+两文案逐字相等+行内无任何 `BaseButton` 家族控件；`SETTINGS_OK` 行追加 `tts=reserved-notice-ok`。

## 五、测试增量（23 个新测试方法，全部无真实 TTS 调用）

| 文件 | 方法 | 口径 |
|---|---|---|
| `Llm/TtsModelsTests.cs` | 5 | 请求模型 source-gen 往返全等/默认 wav+空选择器/格式线名逐字精确/未知与大小写敏感拒绝/响应字节语义 |
| `Llm/TtsProviderPresetsTests.cs` | 4 | 模型 id 小写+端点路径 audio/speech/SpeechUrl 拼接/base 与 chat 预设同源/voice 不虚构（空） |
| `Llm/TtsChannelTests.cs` | 13 | 默认 off→Disabled 零调用/三种不全→NotConfigured/无 Key→MissingKey 零调用（BYOK 同构）/全齐→NotImplemented 零调用且文案含「预留」/Ok 不可达/空请求与构造防御/旧版文档→默认 off/tts 段往返全等/启用态两校验错误/禁用态零约束/设置页文案 verbatim |
| `Presentation/SettingsFormStateTests.cs` | +1 | BuildConfig 逐字保留基线 tts 段 |

零请求 oracle：`CountingTtsProvider`（计数 fake，mock 端口获 CS-3.4 白名单授权；被测通道本身不 mock）——四个拒绝态各自断言 `CallCount==0`。

## 六、红线自查（R5/R7/R9/T1/T2/T3/T8/R13）

- 首跑自查 2 处 T1 误伤（自述「绝不假成功」撞 T1 禁词「假成功」）→ 改述「谎报成功」复扫清零；**我方全部触面（9 文件）红线零违规**。
- 并行注记：T-137（L2 正式版）代理同期在仓，其 WIP 的 R7×2/T1×2/T2×7 由该任务自清，与本任务无关（本 REV 门禁口径见 §七）。

## 七、完成门槛验证（run_all 实录，2026-10-18 安静窗，总体 PASS）

```
G2   红线零违规 : PASS  9 checks | 0 FAIL | 0 WARN | exit_code=0
G2.5 资产规格校验 : PASS  files=641 rejected=0 warned=0 allowed_sizes=[16, 32, 48, 64, 96, 112] max_palette=64
G2.6 模组离线校验 : PASS  0 FAIL, 3 mods loaded (8 content documents)
G1   测试全绿   : PASS  dotnet test: 6617 passed, 0 failed (838s)
G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，29s）
G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，20s）
run_all: 总体 PASS（G1~G4 全 PASS）
```

设置面板 headless 冒烟实录（G4 内）：`SETTINGS_OK providers=4 mask=on declare=on probe=disabled fetch=disabled backend=dpapi-currentuser render_tier=roundtrip-ok key_file=clean disclosure=statemachine-ok compare=3tier-ok ai_label=manifest-linked tts=reserved-notice-ok`（末段=本任务判据：灰置行在位+文案 verbatim+行内零可点击控件）。

**并行实录（诚实记录，T-137 L2 正式版同期在仓）**：
1. 期间共享构建三次被 T-137 WIP 半成品暂阻（L2TierStore/L2RouteDecider 编译错），本任务未触碰对方文件，待其自愈后门禁全绿。
2. 一次满载全量跑（与 T-137 测试泳道并行）出 3 红（含 SimGroupingPerfGuardrail 共享机护栏），静窗复跑同树 6524/6524 全绿证实为负载伪影；终录 6617/0 为安静窗合并树（T-137 三 PR+本任务全部交付面）。
3. 两共享文件（settings_panel.tscn/SettingsPanelUi.cs）的非重叠 T-138 改动随 T-137 PR2（a0ec940）合并入库（对方 commit 注记在案，合并态门禁全绿）；本 commit 承接其余 10 个独占文件。

## 八、提交

- 单 commit（S 级）：`T-138: TTS voice channel interface reservation (mimo-v2.5-tts wire models+endpoint presets/reserved status gates/tts config section/greyed settings row)`——本 commit 含 10 个独占文件（3 新产品文件+3 新测试文件+`LlmConfig`/`LlmConfigLoader`/`SettingsFormState`/`SettingsFormStateTests` 增量+本 REV）；两共享文件（scene+panel）的 T-138 改动已随 T-137 PR2（a0ec940）合并（§七.并行实录③）。
- 未动 `docs/engineering/08-task-plan.md`/`AGENTS.md`（主管记账）；未动 `config/`；未动金样（无 T8 面）。
