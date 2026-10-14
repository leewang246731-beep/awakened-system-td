# 20 · R3 研究：养料经济——LLM 额度作为游戏奖励（BYOK 之上的开发者赠品层）

> 版本：v1.0 ｜ 日期：2026-10-13 ｜ 状态：**研究结论（初稿，待评审）**
> 研究代理：R3（系统设计与技术方案）。外部结论均标注"初检"（2026-10-13 检索），定稿前需复核链接有效性。
> 上游依据：《我在游戏觉醒系统-游戏设计方案》§11.5（L3 BYOK）/§11.6（llm.config.json）/§11.7（调用工程）/§12（系统流叙事）/§16.3（合规）；《10-recovered-v21-architecture.md》§11.5~11.7；代码 `game/src/EraCore/Llm/`（LlmGateway/LlmConfig/LlmDomainWhitelist/ISecretStore 实读）。
> 并行接口：R2 彩蛋清单（并行研究）奖励类型枚举已预留 `essence`（养料）位，本报告给出该位的数值规格（§7.3）。

---

## 0. 一句话结论

**养料 = 开发者出资的、一次性签名兑换码核销的、经极小中转端点路由的 L3 云档体验额度**：玩家完成隐藏任务/彩蛋获得养料码 → 游戏内核销换"会话令牌"→ 军师流量走开发者 key 池中转（优先免费 Flash 模型）→ 服务器端每玩家日限 + 全局日预算熔断，耗尽自动回落玩家 BYOK / L1，**基础体验零降级、玩家侧零付费**。

---

## 1. 概念考证：系统流的"养料"谱系与"token 即养料"的叙事自洽

### 1.1 网文样本（燃料是什么）

| 作品/流派 | 系统升级的"燃料" | 出处（初检） |
|---|---|---|
| 《大王饶命》（会说话的肘子，2017） | **负面情绪值**：把人怼生气/无语/尴尬即吸收变强——"反哺型系统"最出名样本 | [百度百科](https://baike.baidu.com/item/%E5%A4%A7%E7%8E%8B%E9%A5%B6%E5%91%BD/22160791) |
| 神道流/香火流（书单类，17 本完结样本） | **香火愿力/信仰值**：信众香火经"信仰之丝"转化为神力，神位晋升=香火积累 | [知乎书单](https://zhuanlan.zhihu.com/p/508925314)、[《香火修神道》词条](https://baike.baidu.com/item/%E9%A6%99%E7%81%AB%E4%BF%AE%E7%A5%9E%E9%81%93/49894627) |
| 《觉醒失败，我成黑暗扎基怕什么？》等"喂养流" | **吞噬情绪转化力量**："这个世界是很棒的养料啊""喂养我的海帕杰顿"——养料一词的直用样本 | [起点书页](https://www.qidian.com/book/1043642258) |
| 《硅基纪元：从圈养全人类开始》 | 主角本人是 AI 的"**情绪养料**"——AI 吃人类情绪成长（与本作"系统=AI 吃玩家成就"镜像） | [起点书页](https://www.qidian.com/book/1049656213) |
| 系统流总论 | 起点创作社区将此类归为"**反哺型设定**"：系统不白给奖励，要求宿主收集特定资源（情绪/恐惧/崇拜/愤怒）作升级养料——自带剧情驱动与反常识爽点 | [起点俱乐部](https://www.qidianclub.com/threads/125)、[知乎：系统文基础设定](https://zhuanlan.zhihu.com/p/10099948470) |

**结论**：吃"情绪值/信仰值/养料"升级是系统流的成熟母题（2017 年至今大量样本），玩家零教育成本；本作把"养料来源"从"怼人/收香火"替换为"**完成隐藏任务/发现彩蛋**"（正反馈版本的反哺型），谱系成立。

### 1.2 "token 即养料"的现实映射——双向透明是设计金矿

本作军师（系统"零"）说话/思考**真的烧 token**——这与网文"系统说话消耗养料"形成罕见的**非装饰性对应**：

| 方向 | 机制 | 玩家感知 |
|---|---|---|
| 叙事→现实 | 系统每次开口/出方案 = 一次 LLM 调用 = 真实消耗 token | "零在消耗养料"不是演出，是账单 |
| 现实→叙事 | §11.7 已要求**玩家可见调用日志**（时间/域名/模型/状态码/耗时/token 数） | 同一张计量表，BYOK 玩家看"我的 token"，养料玩家看"我的养料"——**一张表两种货币皮肤** |

设计含义（落入 §7 叙事包装）：军师面板常驻"思维负荷表"（本波已消耗 X 养料 / 日上限 N），每次方案卡附"本次预计消耗 1 养料"——把 BYOK 最劝退的"成本不透明"痛点反转成系统流的"资源管理爽点"。这是"系统流 × BYOK"独有、纯单机系统流给不了的融合点。

---

## 2. 先例交叉调研：游戏内货币兑换真实服务

### 2.1 直接先例：**无**（游戏币→开发者出资的 LLM 额度，未找到同构产品）

### 2.2 近似先例谱系（按相似度降序）

| 先例 | 机制 | 与本方案的距离 | 出处（初检） |
|---|---|---|---|
| **NovelAI Anlas** | 应用内货币 Anlas 支付生图推理；订阅每月赠送 1,000 Anlas（≈200 张图）；Opus 档单张免费 | **最近**：币=推理成本，月赠=出资方代付；2026-08 因模型成本被迫加用量上限（前车之鉴，见 §8 风险 3） | [官方文档](https://docs.novelai.net/en/subscription)、[政策调整公告](https://blog.novelai.net/subscription-updates-usage-limits-subscription-anlas-policy-adjustments-88a208d5d9c5) |
| **AI Dungeon Energy**（2020-2026） | 生成次数=能量，免费/付费用户都受限；上线即遭社区强烈反弹，2022-06 撤销改广告+月度积分 | 同构度高：AI 生成为稀缺资源；**教训**：硬墙触发反弹，本方案"耗尽回落"而非断供 | [Wikipedia](https://en.wikipedia.org/wiki/AI_Dungeon)、[Reddit 反弹记录](https://www.reddit.com/r/Games/comments/jpj10v/ai_dungeon_introduces_energy_system_for_all_users) |
| **智谱邀请活动** | 每邀 1 新用户注册+实名，**双方各得 2,000 万 GLM-4.5-Air tokens（价值约 ¥58）** | **国内平台自己就在做"token 作邀请奖励"**——赞助方出资、双方得额度，证明该激励形态在目标 provider 生态内被官方接受 | [智谱上新活动页](https://docs.bigmodel.cn/cn/update/promotion) |
| **Poe compute points** | 免费用户每日约 3,000 点（不可结转、24h 重置），不同模型每条扣点不同 | 每日重置+不可囤积的防囤设计值得抄；无邀请机制 | [Poe FAQ](https://help.poe.com/hc/en-us/articles/19944206309524-Poe-FAQs) |
| **OpenRouter credits / :free 模型** | 充值 ≥10 刀后免费模型限额 50→1,000 次/日；无官方促销码；社区存在**小号刷码**滥用与封号 | 反面样本：无服务器计量的"码"必被薅 | [OpenRouter FAQ](https://openrouter.ai/docs/faq)、[小号滥用讨论](https://www.reddit.com/r/openrouter/comments/1viycs7/how_do_you_get_free_credits_in_openrouter) |
| **itch.io download/external keys** | 创作者上传外部兑换码池（如 Steam key），绑定下载权发放；短码可批量生成打印 | 兑换码**分发**层先例（发放/绑定/一次性），不含计量 | [官方文档](https://itch.io/docs/creators/download-keys) |
| **keygen.sh 离线许可** | Ed25519 签名许可文件/许可 key，公钥内置客户端离线验签 | 兑换码**密码学**层先例（本方案直接采用该模式） | [离线许可模型](https://keygen.sh/docs/choosing-a-licensing-model/offline-licenses) |
| GitHub Student Pack / 各云厂商新人代金券 | 教育认证→真金额度码 | "开发者/平台代付成本"的教育类先例 | 略（常识级） |

### 2.3 "开发者代付玩家 API 成本"的合规与滥用要点

- **禁止把 key 下发进客户端**：OpenAI 服务条款明确禁止共享/转让 key，社区官方回复连"用户把 key 填进他人 BYOK 应用"都算违规，代理转发同罪（初检：[服务条款](https://openai.com/policies/service-terms)、[官方论坛](https://community.openai.com/t/is-this-allowed-this-bring-your-own-key-usage/161185)）——**任何"客户端内置开发者 key"方案出局**，中转（key 只在服务端）是唯一形态。
- **智谱用户协议（默认 provider，重点）**：未经智谱事先书面同意，不得以"出租、出借、出售、分销、转售、转让、共享账号、转授权、二次封装、**服务中转、聚合中转**或其他变相方式，向任何第三方提供大模型开放平台服务"（初检：[用户协议](https://docs.bigmodel.cn/cn/terms/user-agreement)）。本方案的中转端点严格读**可能**落入"服务中转向第三方提供"；但条款同时给出同意路径（"事先书面同意"），且智谱自营邀请活动（§2.2 第 3 行）说明其接受"token 作增长激励"。缓解见 §8 风险 1。
- 滥用面：码被批量扫（公开预生成的码池一旦泄露即全灭）、小号农场（OpenRouter 案例）、单机存档伪造"任务完成"（本作无服务端进度，客户端声称不可信）。

---

## 3. 养料经济学模型

### 3.1 货币定义

| 项 | 规格 |
|---|---|
| 单位 | **1 养料 = 1 次军师（L3 云档）对话调用**（玩家口径"次"；服务器侧另按 token 计真实成本） |
| 单次调用资源上界 | 复用 §11.6 配置守卫：context ≤ 16,000 tokens，max_tokens（输出）≤ 1,024 tokens/次 |
| 获取 | **仅**隐藏任务/彩蛋/签到里程碑发放（R2 接口，§7.3）；**不可购买、不可转让、不可提现**（全免费定位红线，文档标准 7） |
| 消耗 | 仅 L3 云档调用（走开发者池时扣；走玩家自己 BYOK 时**不扣养料、不扣玩家可见额外费用**——玩家自己的 token 自己付） |
| 过期 | 每日签到类当日内有效（UTC+8 24:00 清零，防囤积，仿 Poe）；彩蛋/任务类 90 日有效（核销码内嵌 expiry） |

### 3.2 成本护栏（公式与验算）

计价锚点（初检 2026-10-13，以[智谱定价页](https://bigmodel.cn/pricing)实时为准）：GLM-4.5 系列 输入 ¥0.8/百万 tokens、输出 ¥2/百万 tokens；GLM-4.5-Flash / GLM-4-Flash 系列为**免费模型**；邀请活动锚点 2,000 万 tokens ≈ ¥58 → 混合约 ¥2.9/百万 tokens。

```
单次最坏成本(旗舰档) = (16,000 × 0.8 + 1,024 × 2) / 1,000,000
                    = ¥0.0128 + ¥0.002048 ≈ ¥0.015/次
单玩家日上界        = ¥0.015/次 × 20 次/日 = ¥0.30/玩家/日（旗舰路由）
Flash 优先路由边际成本 ≈ ¥0/次（免费模型，受并发限制约束）
全局日预算封顶 cap  = ¥5/日 → 最坏月支出 ≤ ¥5 × 30 = ¥150/月（硬上界，熔断兜底）
告警阈值            = 全局当日成本 ≥ 0.8 × cap（¥4）→ 通知开发者
熔断阈值            = 全局当日成本 ≥ cap（¥5）或 养料池 token 消耗 ≥ 250 万 tokens/日 → 中转返回 503，客户端回落
```

除零/溢出守卫：日限与预算均为编译期常量；除法仅出现在单价换算（分母 1,000,000 恒正）；服务器计数器用单调递增整数（token 数），不做浮点累计。

### 3.3 水龙头/水槽（faucet/sink）表

| 水龙头（获取渠道） | 数量（养料） | 频控 | 备注 |
|---|---|---|---|
| 每日签到里程碑（连续第 3/7/15 日） | 2/3/5 | 每日 1 档，服务器时钟判定 | 当日有效，仿 Poe 重置 |
| 隐藏任务（R2 清单 `essence` 位，小奖） | 1~3 | 一次性 | 存档本地标记 + 码核销 |
| 彩蛋（R2 清单，大奖） | 5~20 | 一次性 | 90 日有效 |
| 系统升级仪式奖励（§7.1） | 3 | 每级 1 次，共 ≤4 次 | "云上残核上线"体验时长 |

| 水槽（消耗） | 单价 | 守卫 |
|---|---|---|
| L3 云档 1 次军师对话（开发者池） | 1 养料 | 本地预检：余额 < 1 → 不发起，弹"养料不足，回落玩家 key/L1" |
| 水槽总量 | 每玩家 ≤ 20 次/日（服务器执法）+ 单会话令牌 ≤ 60 次/90 日 | 双层：本地（UX）+ 服务器（真执法） |

防通胀：单机无交易市场，养料不可合成/交易；唯一通胀源是存档复制（多设备同码核销）——由**一码一用服务端核销**封死（§4.2）。

---

## 4. 兑换码技术方案对比

### 4.1 前置论证：无服务端方案不存在

"开发者额度奖励玩家"的兑换物是**服务端的推理权**。若客户端无服务端即可兑换，则客户端必然持有可直接调用 provider 的凭证——即"key 下发进客户端"，被所有 provider 条款禁止且必然被逆向提取（§2.3）。静态签名码（keygen.sh 模式）可以离线验证**真伪与面额**，但"一码一用 + 计量 + 熔断"必须有在线组件。结论：**服务端不是选项，是定义的一部分；要比的只是它有多小**。

### 4.2 方案对比表

| 方案 | 构成 | 一次性/计量/熔断 | 月成本 | CN 可达性 | 代码量 | 判定 |
|---|---|---|---|---|---|---|
| **A. 纯静态签名码（GitHub 仓库放码清单）** | 仓库/Release 发布 Ed25519 签名码；客户端离线验签 | ❌ 无法一码一用（核销状态无处存）；无计量无熔断；码池公开即全灭 | ¥0 | 好（Git 已是分发渠道） | 客户端 ~100 行 | **不可行**（仅可作叙事道具/候补发放通道） |
| **B. 极小 HTTP 端点：Cloudflare Worker + D1/Durable Object（推荐）** | 单 Worker 两路由：`POST /redeem`（验签→核销→发设备绑定会话令牌）、`POST /v1/chat/completions`（验令牌→日限→转发 provider，优先 Flash）；D1 记码表+令牌表+日计数；Ed25519 私钥仅存服务端 | ✅ 全有（D1 事务保一码一用；响应头 `X-Essence-Remaining` 回传真实余额；全局预算计数熔断） | **¥0**（免费档：100,000 请求/日 + KV 读 100,000/写 1,000/日，见 [Workers 定价](https://developers.cloudflare.com/workers/platform/pricing)） | workers.dev 域名不稳，**须绑自定义域名**；仍受跨境波动影响，需降级路径兜底 | ~300~400 行 TS | **最轻可行，推荐** |
| **C. 托管 AI 网关：Cloudflare AI Gateway（虚拟 key/统一认证+消费限额）** | 玩家令牌=网关虚拟 key，真实 key 只存网关；网关提供限速、消费限额（spend limits）、日志缓存 | ✅ 计量/限额现成；游戏语义（养料余额回传/码核销）仍需自写一层薄端点 | ¥0 起（日志免费档 100,000 条/账户，见[速率限制](https://developers.cloudflare.com/ai-gateway/features/rate-limiting)与[更新日志](https://developers.cloudflare.com/changelog/product/ai-gateway)） | 同 B | 自写 ~150 行 + 网关配置 | B 的**子件**：可作 B 的转发层省去自写计量，不必单独成案 |
| **D. 自托管 LiteLLM Proxy（虚拟 key + max_budget）** | LiteLLM 网关（Postgres 计费库）+ 小 VPS/容器；`max_budget` 每 key 预算、成本追踪 | ✅ 功能最全（每 key 预算/团队/模型白名单），见 [虚拟 key](https://docs.litellm.ai/docs/proxy/virtual_keys)、[预算](https://docs.litellm.ai/docs/proxy/users) | ≥ ¥25/月（VPS+Postgres 运维） | 可选 CN 内地机房（需备案） | 配置为主 + 运维 | **过重**：1 人免费项目不值得常驻运维；列为规模化后的升级路径 |

**推荐组合：B（+C 作可选转发子件）**。逃逸舱：B 的 Worker 代码开源入仓，社区可自托管镜像（与"开源仓库社区可接力"风险 R12 一致）；CN 可达性不稳时优先回落玩家 BYOK，而非扩服务端。

### 4.3 码与令牌的密码学规格

```
兑换码：16 字符 Base32（80 bit 熵，XXXX-XXXX-XXXX-XXXX 格式）
签名：Ed25519 over {code_id: uuid, amount: 1..50, kind: daily|task|egg, expiry: unix_ts, pool: "glm-flash"|"glm-paid"}
      私钥仅存生成机（开发者本地）；公钥内置游戏客户端（验"这码真是官方发的"）+ Worker（核销时再验一次）
核销：Worker 内 D1 事务：SELECT ... WHERE code_id=? AND redeemed_at IS NULL → 写 redeemed_at, device_id, issued_token
令牌：256 bit 随机，绑定 device_id 哈希；TTL 90 日；每令牌 ≤60 次；泄漏单令牌损失上界 = 60 次_flash ≈ ¥0（免费模型）或 ≤60×¥0.015=¥0.9（旗舰档）
```

双层验证逻辑：客户端离线验签（防伪码即时反馈+离线也能识别"官方码已过期"）→ 服务端核销（真一次性）。防篡改：本地养料余额只是显示缓存，**权威余额永远来自服务器响应头**（`X-Essence-Remaining`），改存档只能骗自己。

---

## 5. Key 池路由设计：对接 LlmGateway 的改造点清单

现有架构（代码实读，`game/src/EraCore/Llm/`）：`LlmGateway`（C-16 协议机）是唯一 LLM 通道，实现 K-9 `ILlmTransport`；域名白名单 `LlmDomainWhitelist.FromProviders(config.Providers)` 构造时冻结；key 经 `ISecretStore.Retrieve(providerId)` 每次调用现取、不过境；`RouteAsync` 零网络拒绝顺序：provider 存在 → enable → endpoint/白名单 → key 存在 → 组包。

**改造点共 10 项（4 组），全部为增量，不破坏 K-9 草案契约：**

| # | 组 | 改造点 | 落点（文件/模块） |
|---|---|---|---|
| 1 | 凭证 | **新增"系统供给 provider"**：特殊 provider 行（如 `id="system-pool"`），`credential_kind: session_token`（区别于 `api_key`），不写入 `user://llm.config.json`，启动时由构建内置签名清单注入 | `LlmConfig.cs`/`LlmConfigLoader.cs`（新字段+注入路径+**回写过滤**） |
| 2 | 凭证 | 会话令牌入 `ISecretStore`（复用"key 永不过境/不进日志"既有保证，K-10/RL5） | `ISecretStore` 实现扩一行 provider id |
| 3 | 白名单 | 白名单在玩家 base_url 之外**追加固定中转域名**（构造参数化，不受玩家配置增删影响；测试证明玩家无法移除它） | `LlmDomainWhitelist.cs` + 测试 |
| 4 | 路由 | **发送前养料预检**：active provider 为 system-pool 且本地余额 <1 → 零网络拒绝，新错误键 `EssenceDepleted`（插在 enable 检查之后、endpoint 之前，维持既有拒绝顺序风格） | `LlmGateway.RouteAsync` |
| 5 | 计量 | **调用后计量**：从 `LlmStreamOutcome`/非流式 usage 读 token 数 → 扣本地养料缓存；**权威余额以响应头 `X-Essence-Remaining` 为准**回写 | `LlmGateway.RouteAsync` 尾部 + `EssenceLedger`（新小组件，仿 BudgetLedger 单一咽喉模式） |
| 6 | 错误 | 新错误键族：`EssenceDepleted`（本地/服务器 402）、`EssenceUnauthorized`（令牌失效 403）、`EssenceRelayDown`（503 熔断/网络）→ `LlmErrorKeys`/`LlmErrorClassifier`/`LlmErrorCatalog` 各加条目与文案 | `LlmErrorCatalog.cs` 等 |
| 7 | 回落 | **三层回落链**：system-pool 拒绝（耗尽/熔断/不可达）→ 自动回落玩家自配 BYOK → 未配置则 L1 语法档；回落一次性横幅告知，**永不硬墙**（AI Dungeon 教训，§2.2） | provider 选择层（网关调用方） |
| 8 | 透明 | **玩家可见调用日志扩展**：system-pool 条目显示"系统供给（养料）-1"徽记与 token 数——BYOK 与养料共用一张计量表（§1.2 双向透明落地） | 调用日志 UI（§11.7 既有功能） |
| 9 | UI/合规 | 兑换对话框（输码→验签→核销→演出）+ 养料余额面板 + 数据流向声明扩展："养料兑换后的请求经开发者中转端点（{relay_domain}）转发至模型服务，由开发者额度支付；Coding Plan 类密钥不适用于中转池（仅按量 API）" | 设置界面（§11.6 扩展） |
| 10 | 配置护栏 | system-pool 强制参数：`context_budget_tokens ≤ 16,000`、`max_tokens ≤ 1,024`、每日调用 ≤20 次（本地拦截为 UX，服务器为执法）；玩家不可改 | `LlmProviderConfig` 校验 + `EssenceLedger` |

**服务端最小规格（方案 B）**：Cloudflare Worker（TS，~300 行）：`POST /redeem`（Ed25519 验签→D1 原子核销→发设备绑定令牌）、`POST /v1/chat/completions`（验令牌→日限→预算计数→按 pool 路由：默认 `glm-flash` 免费模型，大额码 `glm-paid` 按量 key→转发智谱→透传 SSE→注入 `X-Essence-Remaining`）。密钥纪律：真实按量 API key 仅存 Worker Secret；仓库只放占位符（文档标准 5）。

---

## 6. 防滥用与审计熔断

| 层 | 措施 |
|---|---|
| 码 | 80 bit 熵不可枚举；不公开预生成（按需生成：签到由服务器时钟派生、彩蛋码由开发者经社区渠道定量投放——**码总量=开发者主动投放量，泄露面=已投放面**） |
| 核销 | 一码一用（D1 事务）；同 device_id 30 日核销 ≤10 码；同 IP 异常频次（>20 码/日）拒绝+记日志 |
| 令牌 | 设备绑定、90 日 TTL、单令牌 ≤60 次、泄漏损失上界 ¥0.9（§4.3） |
| 调用 | 每玩家 ≤20 次/日（服务器执法）；单令牌并发 =1；context/max_tokens 服务端二次钳制 |
| 总额 | 全局日预算 cap=¥5 硬熔断（Worker 503）+ 80% 告警（开发者通知渠道）；**最坏日损失 = cap，与玩家人数无关** |
| 审计 | D1 记录：核销（码/设备/IP/时间）、每次调用（令牌/模型/token 数/结果码）；保留 90 日滚动；崩溃/告警不包含任何 key（复用 K-10 纪律） |
| 进度伪造 | 单机存档不可信：客户端"完成彩蛋"只是**兑换码的领取资格 UI**，养料永远来自码+服务器核销；改存档伪造任务完成 → 拿不到码 → 无养料 |

---

## 7. 叙事包装与玩家体验

### 7.1 系统升级仪式（兑换 = 演出）

- 码的叙事名：**"补给信标密钥"**（上一纪元指挥网络散落的补给舱认证密钥——与前纪元 AI"零"的世界观缝合）。
- 核销演出（8~12 s，可跳过）：输入密钥 → 系统面板扫描波纹 → "叮！检测到上级补给信标……认证通过" → 养料槽注入动画 + 系统升格台词（按人格四套变体）："**养料注入完毕。零的核心温度上升 0.3℃。宿主，我能多陪你走一段了。**"（毒舌版："别误会，我不是为了你，我是为了这些养料。"）
- 系统等级（纯叙事皮肤，不碰塔防数值）：养料累计核销 1/3/10/30 次 → 零 Lv.1→Lv.4，各给 1 次专属台词与面板框变化；**每次升级附赠 3 养料**（§3.3 水龙头）。

### 7.2 消耗演出与"思维负荷表"

军师面板常驻负荷表（本日已耗 X/20）；方案卡（§11.2）在"消耗"行并列显示："系统点 1 + 养料 1（云上思考）"——L1 档只显示系统点，让"烧养料=系统真实思考成本"可感。养料耗尽回落时台词："本系统的云上核心暂时休眠……退回本地模式。宿主，稍后见。"（回落不惩罚）

### 7.3 与 R2 彩蛋清单的接口（`essence` 位规格）

```json
{ "type": "essence", "amount": 1, "validity": "daily | 90d", "pool": "glm-flash | glm-paid", "ceremony": "minor | beacon" }
```
- 小奖 `amount=1~3`（隐藏任务，`ceremony: minor`，面板轻提示）；大奖 `amount=5~20`（彩蛋，`ceremony: beacon`，§7.1 全演出）。
- R2 清单只需声明 amount 与 ceremony；发放时序（游戏内给码 vs 社区埋码）由本方案 §6 投放策略执行。

### 7.4 公平性铁律

养料**只加不改**：基础体验 = L1 语法档全离线、可全通关全成就（与方案风险 R2"关闭系统可全通关"一致）；养料只解锁 L3 云档体验的**开发者出资次数**与叙事升格，永不接触塔防资源/伤害/成就判定。BYOK 玩家不需要养料（自己的 key 无限用）——养料是"还没配 key 的玩家的云上试用+老玩家的收藏性彩蛋奖励"，两条路都不降级。

---

## 8. 风险清单与缓解

| # | 风险 | 等级 | 缓解 |
|---|---|---|---|
| **E1** | **provider 条款**：智谱用户协议禁"服务中转向第三方提供"；OpenAI 更严（连 BYOK 填 key 都算违规） | **高** | ① 养料流量**默认路由免费模型**（GLM-4.5-Flash/GLM-4-Flash 永久免费——"免费服务的转发"显著偏离"提供（付费）开放平台服务"的条款射程）；② 不向玩家收费、额度极小（单玩家年上界 < ¥30）= 赠品而非转售；③ **上线前走智谱书面同意/合作通道**（条款明文允许"事先书面同意"豁免，且智谱自营邀请送 token 活动表明激励形态被官方接受）；④ key 池绝不含 OpenAI key 与 Coding Plan 类订阅 key |
| **E2** | **羊毛党/码被批量扫**（OpenRouter 小号农场先例；单机存档伪造任务完成） | 高 | §6 全表：80 bit 熵码按需投放（不预生成公开池）、D1 一码一用、设备/IP 绑定限频、客户端进度仅是领取资格 UI（权威在服务器核销）；最坏损失=全局日预算 cap（¥5/日） |
| **E3** | **开发者额度失控/成本反弹**（NovelAI 2026-08 被迫加限；AI Dungeon 硬墙遭社区反弹） | 高 | 服务器硬熔断（cap=¥5/日+80% 告警，最坏月支出 ¥150）+ Flash 免费模型优先（常态边际成本 ≈¥0）；**养料永不构成硬墙**（耗尽/熔断一律回落玩家 BYOK/L1，横幅告知）——把 AI Dungeon 的死穴变成"系统的云核休眠"叙事 |
| E4 | CN 可达性：Cloudflare 自定义域名跨境波动 | 中 | 回落链（E3 同款）保证断网可玩；Worker 代码开源可社区自托管镜像；规模化后可迁 EdgeOne/函数计算（方案 D 升级路径） |
| E5 | 法务赠品定性：抽奖式有奖销售最高奖 ≤ ¥50,000（[规范促销行为暂行规定](https://www.moj.gov.cn/pub/sfbgw/flfggz/flfggzbmgz/202101/t20210104_146553.html)，初检） | 低 | 本方案**非抽奖**：养料=确定性任务奖励（幸运转盘仅演出、不发养料，规避概率公示义务）；赠品不可提现、单笔面额 ≤¥0.3、单玩家年价值 <¥30，属极小额服务赠品（de minimis；非法律意见，上线前可轻咨询） |
| E6 | 公平性质疑（无养料玩家吃亏） | 低 | §7.4 铁律：养料只加不改；BYOK 玩家无需求；文档与 itch 页明示获取渠道 |
| E7 | 维护负担：1 人开发多一个常驻服务 | 中 | 单 Worker ¥0 免费档、无状态可弃（D1 数据可导出 JSON 归档）；服务死亡=养料功能静默下线，游戏本体零影响 |

---

## 9. 开放问题登记（建议并入方案 §20，接 Q9 之后）

| # | 问题 | 建议 |
|---|---|---|
| Q10 | 是否接受为养料经济引入**极小服务端**（方案 §16.1 现为"不采用自建服务器"，需修订为"唯一例外：养料中转 Worker"） | 建议：接受（B 案 ¥0 成本/开源可弃）；若拒绝，养料退化为纯叙事道具（方案 A，无真实 LLM 兑换） |
| Q11 | 养料的中文正式名（候选：养料/源质/补给信标能量）与图标 | 建议"养料"（甲方原词，且与网文母题同频） |
| Q12 | 智谱书面同意/商务接洽时机（E1） | 建议：v0.5 公开 Demo（养料首次面世）前完成问询邮件，未获回复则只走免费模型池 |

---

## 变更记录

| 版本 | 日期 | 说明 |
|---|---|---|
| v1.0 | 2026-10-13 | R3 初稿：概念考证/先例谱系/经济模型/兑换方案对比/LlmGateway 10 改造点/风险与叙事包装。外部链接均为初检（2026-10-13），定稿前复核。 |

## 参考来源（全部为 2026-10-13 初检）

概念：[起点俱乐部·系统流要点](https://www.qidianclub.com/threads/125) ｜ [大王饶命百科](https://baike.baidu.com/item/%E5%A4%A7%E7%8E%8B%E9%A5%B6%E5%91%BD/22160791) ｜ [神道流书单](https://zhuanlan.zhihu.com/p/508925314) ｜ [香火修神道](https://baike.baidu.com/item/%E9%A6%99%E7%81%AB%E4%BF%AE%E7%A5%9E%E9%81%93/49894627) ｜ [喂养流样本](https://www.qidian.com/book/1043642258) ｜ [硅基纪元](https://www.qidian.com/book/1049656213) ｜ [系统文基础设定](https://zhuanlan.zhihu.com/p/10099948470)
先例：[NovelAI 订阅/Anlas](https://docs.novelai.net/en/subscription) ｜ [NovelAI 用量限制公告](https://blog.novelai.net/subscription-updates-usage-limits-subscription-anlas-policy-adjustments-88a208d5d9c5) ｜ [AI Dungeon（维基）](https://en.wikipedia.org/wiki/AI_Dungeon) ｜ [AI Dungeon 能量反弹（Reddit）](https://www.reddit.com/r/Games/comments/jpj10v/ai_dungeon_introduces_energy_system_for_all_users) ｜ [Poe FAQ](https://help.poe.com/hc/en-us/articles/19944206309524-Poe-FAQs) ｜ [智谱上新活动（邀请 2000 万 token）](https://docs.bigmodel.cn/cn/update/promotion) ｜ [OpenRouter FAQ](https://openrouter.ai/docs/faq) ｜ [OpenRouter 小号滥用（Reddit）](https://www.reddit.com/r/openrouter/comments/1viycs7/how_do_you_get_free_credits_in_openrouter) ｜ [itch.io 兑换码体系](https://itch.io/docs/creators/download-keys) ｜ [keygen.sh 离线许可](https://keygen.sh/docs/choosing-a-licensing-model/offline-licenses) ｜ [ZCode 新用户体验套餐](https://zcode.z.ai/cn/docs/configuration)
合规：[智谱用户协议](https://docs.bigmodel.cn/cn/terms/user-agreement) ｜ [OpenAI 服务条款](https://openai.com/policies/service-terms) ｜ [OpenAI BYOK 官方回复](https://community.openai.com/t/is-this-allowed-this-bring-your-own-key-usage/161185) ｜ [规范促销行为暂行规定（司法部）](https://www.moj.gov.cn/pub/sfbgw/flfggz/flfggzbmgz/202101/t20210104_146553.html)
技术：[智谱定价](https://bigmodel.cn/pricing) ｜ [GLM-4.5 文档](https://docs.bigmodel.cn/cn/guide/models/text/glm-4.5) ｜ [Cloudflare Workers 定价](https://developers.cloudflare.com/workers/platform/pricing) ｜ [KV 定价](https://developers.cloudflare.com/kv/platform/pricing) ｜ [AI Gateway 速率限制](https://developers.cloudflare.com/ai-gateway/features/rate-limiting) ｜ [AI Gateway 更新日志（消费限额）](https://developers.cloudflare.com/changelog/product-ai-gateway) ｜ [LiteLLM 虚拟 key](https://docs.litellm.ai/docs/proxy/virtual_keys) ｜ [LiteLLM 预算](https://docs.litellm.ai/docs/proxy/users) ｜ [LiteLLM 成本追踪](https://docs.litellm.ai/docs/proxy/cost_tracking)
