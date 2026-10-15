# REV-20261016-w5-g7-matrix — BYOK 双协议×模板矩阵逐格实连留痕（W5 出口⑤ / 02-G7）

| 项 | 内容 |
|---|---|
| 任务挂接 | W5 出口⑤"BYOK 双协议×模板矩阵逐格通过（G7）"（08 §1 W5 行）；G7=02-consistency-audit §6 之 02-G7（"2 协议（openai/anthropic）×2 预置模板+自定义/LM Studio，连接测试探针（tools 透传）逐格通过"，挂 W3 T-072→W5 出口⑤；13 号正式门禁表中 G7=覆盖率阈值，两者不同物，本表 G7 指 02-G7） |
| 状态 | **逐格留痕成文**（W5 门禁处置 P1-2）：每格如实标注证据等级——实连 / 实连录制回放 / 协议级（FakeWirePort 零网络）/ 配置级；**未实连格显式标原因，不伪造实连** |
| 矩阵口径 | 行=模板×协议组合（设置页四模板下拉 F-081：GLM/MiMo/自定义/本地 LM Studio，单源 `LlmProviderPresets`；协议 radio=openai/anthropic 对任意行可配，`SettingsFormState`）；列=五探针面（连通/鉴权/模型列表/流式/tools 探针） |
| 相关提交 | 184e858（T-072 LlmGateway，"MiMo live-verified"）、d2fef8a（在线测试加固）、bc7dda7（T-091 流式/超时/错误码全量） |

## 1. 逐格执行汇总表

图例：**实连**=在线真调（有 key、有网络、真端点应答）；**实连→cassette 回放**=真响应经录制器落盘（CS-3.4 禁手写），CI 以回放复刻；**协议级**=FakeWirePort/合成数据单测（wire 形状、归约、分类——零网络）；**配置级**=模板常量/端点形状校验；**未实连**=无真调记录（本行给出原因与既有覆盖面）。

| 模板×协议 \ 探针面 | 连通 | 鉴权 | 模型列表（/models） | 流式（SSE） | tools 探针（EraPing/BuildPlan） |
|---|---|---|---|---|---|
| **GLM**（zhipu-glm，openai） | 未实连（无 GLM key：`config/secrets.local.json` 仅 MiMo key，09 号 §密钥）。覆盖=配置级+端点形状（LlmProviderPresetsTests.Zhipu_template_matches_design_schema；LlmDomainWhitelistTests bigmodel 行；OpenAiProtocolTests 以 bigmodel base_url 验端点拼接） | 未实连（同上）。覆盖=协议级（Bearer 头构造，OpenAiProtocolTests）；1315（Coding Plan key 服务端拒）为模板 Note 文档口径，非本仓实测 | 未实连（无 /models 真调）。覆盖=协议级（openai {base}/models 形状+Bearer，LlmModelListTests；白名单不含即零网络用例恰以 bigmodel 域构造） | 未实连（GLM 端点真 SSE 未录）。覆盖=协议级（SSE 归约族：SseLineBufferTests/LlmGatewayStreamTests/OpenAiProtocolTests 全量样例，FakeWirePort） | 未实连（EraPing 未真调 GLM）。覆盖=协议级（LlmConnectionProbeTests 成功/401/文本-only/零网络四态，FakeWirePort） |
| **MiMo**（xiaomi-mimo，openai） | **实连**：LlmOnlineTests 真调 api.xiaomimimo.com（key=本地 secrets；commit 184e858 "MiMo live-verified"；REV-20261014-T067 记录该在线探针实跑偶发网络抖动后重跑绿——真网络证据） | **实连**：真 key 200 应答（认证链随真调验证）；401→auth 分类语义=合成 cassette 回放（synthetic-error-401，LlmCassetteSyntheticTests） | **未实连**：真调仅 chat/completions 与探针，未真调 /models。覆盖=协议级（LlmModelListTests openai 形状用例即以 api.xiaomimimo.com/v1 为样例端点，含 401/空列表/坏配置矩阵） | **实连→cassette 回放**：mimo-buildplan-tool.json 录自真响应（RecordedAtUtc=2026-10-14T00:40:50Z，ResponseStatus=200，**ResponseIsSse=true**，stream:true 请求）；CI 回放复刻 SSE 归约+工具合并（LlmCassetteTests.Mimo_cassette_replays…） | **实连**：MiMo_live_connection_probe_verifies_tool_passthrough（EraPing 工具透传，F-085 全链）+ MiMo_live_function_calling_parses_buildplan（"在东门建两座弩塔"→BuildPlan 两格）——两条在线断言均真调 |
| **自定义+openai 协议**（custom-relay，openai radio） | 未实连（自定义行出厂 BaseUrl 空+禁用，LlmProviderPresetsTests.Custom_and_lmstudio_templates_ship_disabled——无既定端点可连）。覆盖=协议级（OpenAiProtocolTests 尾斜杠归一/非 http 拒绝） | 未实连（同上）。覆盖=协议级（Bearer 构造）+无 key 零网络（LlmGatewayTests 无 key 行→MissingKey 不发请求） | 未实连。覆盖=协议级（LlmModelListTests 坏 base_url→InvalidConfig 零网络） | 未实连。覆盖=协议级（LlmGatewayStreamTests：任意分片粒度/SSE 被吞回落 JSON/流中断归约——中转站坑位族） | 未实连（自定义端点本质不可预实连——端点未知）。覆盖=协议级（LlmConnectionProbeTests 文本-only→"中转站丢工具调用"提示用例，即本行 §11.6 已知坑）；玩家侧以"测试连接"探针现场验证 |
| **自定义+anthropic 协议**（custom-relay，anthropic radio） | 未实连（MiMo 有 anthropic 兼容端点 https://api.xiaomimimo.com/anthropic，预设 Note 记载，但未真调）。覆盖=协议级（AnthropicProtocolTests 端点 {base}/v1/messages，含 MiMo 兼容端点形态用例） | 未实连。覆盖=协议级（x-api-key+固定 anthropic-version 头，AnthropicProtocolTests） | 未实连。覆盖=协议级（LlmModelListTests anthropic {base}/v1/models+x-api-key 头形状） | 未实连。覆盖=协议级（AnthropicProtocolTests SSE：content_block_start/input_json_delta/message_stop+thinking_delta→reasoning；LlmGatewayStreamTests/ToolCallStreamTests anthropic 流式读循环+双 tool_use 交错） | 未实连。覆盖=协议级（OpenAI 形工具→{name,description,input_schema} 投影+漂移即抛，AnthropicProtocolTests；投影漂移边界亦有 illegal 用例） |
| **LM Studio**（local-lmstudio，openai） | 未实连（本机无 LM Studio 服务实测记录；模板出厂禁用+指向 localhost:1234，LlmProviderPresetsTests）。覆盖=协议级（与 openai 行同套） | 未实连（同上）。网关侧缺 key→MissingKey 零网络对本地行同样生效（LlmGatewayTests——LM Studio 免 key 场景亦须在设置页填任一 key 占位过闸） | 未实连（本地模型发现的真实用途无实测）。覆盖=协议级（openai 形状同一套） | 未实连。覆盖=协议级（同一套） | 未实连。覆盖=协议级（同一套；探针四态+第二 provider 各自锁域用例） |

**格计数**：25 格中**带实连证据的 4 格全在 MiMo 行**（连通/鉴权/tools=在线真调断言；流式=真 SSE 响应录制体经 cassette 回放）；其余 21 格均为协议级/配置级自动化覆盖，无真调——原因两类：①无对应 key/服务（GLM 无 key、LM Studio 无本机服务）；②端点本质未知（自定义/中转站行）。**无任何格为空白无覆盖**。

## 2. 无 key 跳过语义（第五类证据，贯穿全矩阵）

- 在线用例（LlmOnlineTests）在 `SKIP_ONLINE=1` 或 `config/secrets.local.json` 缺失时**显式跳过并断言跳过原因非空**（OnlineSkip.ReasonCaptured——"跳过不是通过"，沿用 GodotSharpGuardTests 先例）；CI 无 key 环境即走此语义，不产生假绿。
- 网关/列表/探针三入口的零网络拒绝矩阵（LlmGatewayTests/LlmModelListTests/LlmConnectionProbeTests）：禁用行、缺 key、域名不在冻结白名单、坏配置四类**均在发出任何网络字节之前拒绝**（port.CallCount=0 断言）。
- Web 档位平台门（T-110）：`web-tier-disabled` 先于一切常规门且**不触碰密钥存储**（AccessCountingStore.RetrieveCalls=0——LlmModelListTests.Web_disabled_fetch…；网关侧同形，REV-20261016-T106 §5j）。

## 3. 结论（诚实口径）

1. **G7（02-G7）"逐格通过"的实现语义**=每格有自动化判据：能实连的格（有 key/端点的 MiMo）以在线断言+录制回放双重钉住；不能预实连的格（GLM 无 key、自定义端点未知、LM Studio 无本机服务）以协议级（wire 形状/归约/分类/零网络纪律）覆盖——本 REV 即"逐格留痕"，防止"矩阵全绿"被读成"全部真连过"。
2. **实连锚点=MiMo（openai 协议）**：T-072 交付时 live-verified（184e858），探针（EraPing 透传）与军令→BuildPlan function calling 两条在线断言+cassette 录制体（真 SSE 200）为持久证据；本机每次 run_all 在 key 在位时真调该二用例。
3. **未实连清单（显式）**：GLM 全 5 格（无 key）、自定义+openai 全 5 格、自定义+anthropic 全 5 格、LM Studio 全 5 格、MiMo 模型列表 1 格——共 21 格无真调；其中 MiMo 的 anthropic 兼容端点与 GLM 端点在业主提供 key/需要时可复用 LlmOnlineTests 模式补录，不构成本门禁的隐藏债（玩家侧自定义/LM Studio 格按设计即由玩家现场"测试连接"探针验证——F-085 全链探针即为此场景而设）。
4. 密钥纪律（RL5）：cassette 双闸（认证头剥离+key 形态串拒写）由 LlmCassetteTests 四用例断言；本 REV 全文无任何 key 材料。

## 变更记录

- v1.0（2026-10-16，W5 门禁处置 P1-2）：首次成文。逐格汇总表（5 行×5 列）+无 key 跳过语义+结论；证据全部取自既有测试/cassette/提交记录，未新增任何网络调用。
