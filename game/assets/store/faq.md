# FAQ（商店页三条）— T-108 / F-150

> 口径对齐：设计文档 §1「FAQ 三条」与 §16 免费分发、Q7（AI 不收费 BYOK）、
> Q16A（AI 生成内容披露口径，31 号 v2.0）。README 与 itch 页直接复用本文件。

## Q1 军师能听懂多自由的话？／ How freely can I talk to the system?

**中文**：两个档位。**离线军令语法档开箱即用**，不配任何 Key——照着示例说
「守住东门」「在东门建两座木箭塔」「敌情怎么样」「三分钟后叫醒我」这类军令句
就行；**自然语言对话是可选 AI 档**（BYOK，自带按量 API Key），可以更口语地聊。
无论哪个档位，系统都只能用**封闭动作清单**（19 个带 Schema 校验的合法动作）
在游戏规则内行事——出招、等你审批、点数限量，**永不乱创造**；系统也可以
完全关闭，全通关全成就不受影响。

**EN**: Two tiers. The **offline command grammar works out of the box** with no
key — just issue orders like "hold the east gate", "build two arrow towers at
the east gate", "how's the enemy situation". **Free-form natural chat is the
optional AI tier** (BYOK — bring your own pay-as-you-go API key). Either way,
the system can only act through a **closed action list** (19 schema-validated
legal actions): it proposes, you approve, points are rationed — it never
invents powers. And you can turn the system off entirely without losing any
content or achievements.

## Q2 AI 档要花钱吗？／ Does the AI tier cost money?

**中文**：游戏端**不收任何钱**。AI 对话档的服务器费用由玩家自担（BYOK——
自带按量计费的 API Key，智谱 GLM / MiMo 等已预置模板）。注意：**Coding Plan
（套餐包月）密钥不能用于本游戏**——服务端会返回错误码 1315，请使用按量计费
Key。不配 Key 时军令语法档完整可玩，警报与帮助一样不少。

**EN**: The game itself charges nothing. The optional AI chat tier runs on your
own pay-as-you-go API key (BYOK; GLM / MiMo templates are preconfigured).
Note: **"Coding Plan" subscription keys cannot be used here** — the provider
returns error 1315. Without any key, the offline command grammar tier is fully
playable, alerts and all.

## Q3 这游戏免费吗？／ Is the game free?

**中文**：**永久免费，无任何付费项**——无内购、无广告、无 DLC 收费。代码
MIT 开源，美术音频资产 CC BY-NC（可自由游玩、直播、录视频、写攻略）。后续
章节（E3~E6）以免费更新的形式追加。

**EN**: **Free forever, no payments of any kind** — no IAP, no ads, no paid
DLC. Code is MIT open source; art/audio assets are CC BY-NC (play, stream,
record and write guides freely). Later chapters (E3–E6) ship as free updates.
