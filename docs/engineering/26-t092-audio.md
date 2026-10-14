# 26 T-092 音频全量 E1/E2 报告（三态 BGM+人格音色+一次性旁白+CC0 台账）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-15 |
| 状态 | **已交付**（run_all G1~G4 全绿；smoke 新增 AUDIO_OK 段判据；EraCore.Tests 音频域 167 测试全绿，全套 4978 绿） |
| 任务卡 | T-092（C-25 音频全量 E1/E2；Wave 4，D 泳道；覆盖 F-112 回归/F-135 E1E2/F-136/F-137 薄版/F-138/F-139；依赖 T-060） |
| 验收标准 | 三档 BGM+人格音色+一次性旁白+CC0 台账（可听测+审计）（08-task-plan T-092 行） |
| 依据 | 设计方案 v3.2 §14（六时代各 3~4 曲三态分层[平静/交战/Boss 淡入淡出]+军师人格语音[独立音量开关/关闭换中性旁白]+叮三级）；14-art-license-audit §1.1/B4（Kenney 渠道 CC0 同源核验法）；CODE_STANDARDS CS-1.4/CS-1.5/CS-2.3/CS-3；T-060 DingSynth/DingAudioPlayer 先例 |
| 交付物 | EraCore/Audio/ 新 7 源文件+DingBus 域共存；game/content/audio/ 参数表×3；presentation/EraTD/audio/AudioDirector.cs+DingAudioPlayer.cs 音效位扩展+Main.cs 一行挂载；CC0 OGG×2 入库；许可证快照 docs/assets/licenses/kenney-music-jingles.txt；本报告 |

---

## 1. CC0 vs 代码合成来源分布（审计面）

| 交付面 | 来源 | 凭证 |
|---|---|---|
| 一次性旁白 stinger ×2 | **CC0 实体资产**（Kenney Music Jingles，包外页面+包内 License.txt 双重核验） | docs/assets/licenses/kenney-music-jingles.txt（页面直链+zip sha256+两入库文件 sha256+逐字节 License 快照）；入库文件 presentation/EraTD/audio/cc0/kenney-jingles-{pizzicato-07,steel-02}.ogg |
| 三态 BGM 循环 ×6（2 时代×3 态） | **代码合成**（零二进制入仓，确定性；WAV 产物落 res://audio/generated/ 供可听测，gitignore 排除） | §2 合成参数+算法；BgmSynthTests 字节级确定性+信号级断言 |
| 人格音色 token（4 人格+1 中性旁白） | **代码合成**（参数表数据化） | §3 参数表 persona-timbres.json；PersonaVoiceTests 四音色字节相异+轮廓信号断言 |
| 叮三级 | T-060 既有代码合成（本轮回归+总线并轨，不改音色） | §5 回归面；smoke AUDIO_OK ding_tiers=3 |

分配逻辑（零成本优先，任务卡预批准双路线）：BGM 三态分层要求三层共享和声基座以便淡入淡出无缝——代码合成按整数周期/循环量化天然满足；CC0 短乐（1.3~1.4 s 一次性收音）恰好匹配一次性旁白"播一次"的形态。真人配音与作曲外包为 §14 预留的后续可选线（音色/和声参数已表驱动，外包素材替换不动代码）。

## 2. 三态 BGM（F-135 E1/E2 薄版）

### 2.1 合成参数表（game/content/audio/bgm-programs.json，schema urn:eratd:content:audio:bgm-programs:1）

| 时代 | tempo | 根音 | 和弦（音程比） | 垫底增益 | 鼓点增益 | 低音增益 | 帽钉细分 | 循环 |
|---|---|---|---|---|---|---|---|---|
| era1 | 84 | A2 110 Hz | 小三和弦骨架 [1.0, 1.1892, 1.4983, 2.0] | 0.34 | 0.20 | 0.24 | 2 | 4 小节 4/4 ≈ 11.43 s |
| era2 | 100 | D3 146.83 Hz | 大三和弦骨架 [1.0, 1.2599, 1.4983, 2.0] | 0.32 | 0.22 | 0.26 | 4 | 4 小节 4/4 = 9.6 s |

三态=同垫底的**累积分层**（§14 口径）：calm=双失谐正弦垫（8 声部，慢呼吸 LFO）；combat=+鼓点网格（拍 1/3 底鼓[130→45 Hz 扫频]、拍 2/4 军鼓[190 Hz+噪声]、细分帽钉[LCG 噪声]，全事件 3 ms 释音坡消咔哒）；boss=+低音线（小节交替根音/五度，tanh 软削波）。每循环独立峰值归一化至 0.72（三态/两代等响度，淡入淡出不抽 pumps）。

**确定性机要**：一切音调分量按整数周期/循环量化（f=k/N），采样序列严格 N-周期（无缝循环的数学前提）；打击乐为拍位偏移的纯函数+逐 hit 种子 LCG（禁共享 Random，CS-1.4）。同机同 bytes；跨机不作位级承诺（同 DingSynth W2 批 3 F7 措辞），落盘 WAV 快照为可听测与回归锚点（BgmSynthTests.Wav_image_is_canonical 等）。守卫：节拍网格尾端+0.12 s 尾音越过循环尾→InvalidOperationException（参数行违规先于渲染暴露，BgmSynthTests.Cramped_grid_is_rejected）。

### 2.2 三态切换=CrossFadeBus（纯逻辑淡入淡出状态机）

等功率交叉淡化：from=cos(θ)、to=sin(θ)，全程 **from²+to²=1**（恒定感知响度）；时间域=驱动事件 tick（K-1 30 Hz，无墙钟，CS-1.4）。淡入淡出窗口 90 tick=3 s（AudioDirector.FadeSeconds）。重定向语义：淡化中再请求→从**主导侧**（增益>0.5）重启新淡化，有界不连续≤1−cos(π/4)；同目标请求=无操作。现场态驱动：AudioDirector._Process 只读快照（场上敌>0→交战；Boss 波经 SetBossWave 公共接口，波计划 WavePlan.HasBoss 接线随战役宿主落——薄版冒烟直驱三态全链）。

## 3. 军师人格语音（F-137 薄版，参数表数据化）

game/content/audio/persona-timbres.json（schema urn:eratd:content:audio:persona-timbres:1，5 行封闭集=四人格+中性旁白）：

| 音色 id | 基频 | 轮廓 | 波形 | 音节 | 音节/间隙 | 颤音 | 步进比 | 语义 |
|---|---|---|---|---|---|---|---|---|
| advisor 参谋 | 440 | steady 平稳 | 正弦 | 90 ms | 45 ms | — | 1.0 | 默认中性军师声 |
| snarky 毒舌 | 660 | **sharp 尖锐短促**（逐音节升调） | 软方波 | 60 ms | 25 ms | — | 1.06 | 语快而利 |
| goofy 沙雕 | 392 | **bounce 滑稽跳音**（纯四度交替） | 三角波 | 70 ms | 35 ms | — | 1.335 | 蹦跳感 |
| tsundere 傲娇 | 494 | **wobble 起伏**（6 Hz 颤音） | 正弦 | 100 ms | 40 ms | 6 Hz/4% | 1.0 | 情绪起伏 |
| narrator 旁白 | 262 | steady | 正弦 | 140 ms | 60 ms | — | 1.0 | 中性旁白降级目标 |

台词→声纹：音节数 3~6 由台词 key 的 FNV-1a 哈希确定性驱动（同台词同声，禁随机）；人格只改音色不改能力/措辞（§10.7，措辞归 T-089/T-126 文案域）。台词触发接 **DialogueLine.SoundCue 音效位**：DingAudioPlayer.PlayCueSlot("ding_info|ding_reward|ding_crisis", tick)（封闭槽集，§10.3 三级映射至未占用事件类防节流吞噬）；音色触发面 PlayLineVoice(line, persona, systemEnabled)（T-093/T-126 UI 绑定接口已就位）。人格语音走 **Voice 独立通道**（§4）。信号级验证：advisor 各音节过零率相等（steady）/snarky 末音节>首音节 10%（sharp）/goofy 奇偶音节过零率比≈1.335±20%（bounce）/四音色+旁白两两字节相异（PersonaVoiceTests）。

**OffModeDowngrade 联动**：VoiceDowngrade.Resolve(persona, systemEnabled)——系统开=现人格行，系统关=**中性旁白行**（§10.7/§14"关闭系统时换中性旁白"）；危机警报不降级（警报不缺失，面板域既有不变量）。

## 4. 一次性旁白（F-138）+混音总线

**一次性旁白**：game/content/audio/narration.json（schema urn:eratd:content:audio:narration:1）登记 4 锚=E1/E2 × 开场白/胜利交底，id=叙事章真实台词 key（AudioContentTests.Narration_anchors_exist_in_story_chapters 断言 F-138↔T-089 链接：e1.open.narrator/e1.end.handover/e2.open.narrator/e2.end.zero）。播放=CC0 stinger（开场=Pizzicato07 轻拨弦/胜利=Steel02 金属收束）+中性旁白合成音调（真人配音属 §14 可选外包线）；**一次性标记入存档**：NarrationBook 经 IFileStore 落 settings/narration.json（PanelSettingsStore 先例：损坏容忍不阻启动、陈旧 id 丢弃、落盘排序稳定 diff）。

**总线混音器**：AudioMixer 三通道独立音量+静音——Bgm（三态循环）/Sfx（T-060 叮总线，适配层同步 EffectiveGain(Sfx)→DingBus.Volume）/Voice（军师人格语音+旁白，§14 独立音量开关）；EffectiveGain=静音归零否则通道音量；ApplySettings 先验证后变更（原子，失败零半套用）。

## 5. 叮三级回归（F-112/F-136，T-060 既有）

总线并轨后回归面：smoke AUDIO_OK `ding_tiers=3`（三音色映射完整）+ `PlayCueSlot("ding_info")=Played`/未知槽=null（音效位接口）；EraCore.Tests 既有 DingBus/DingSynth/DingJournalMap 34 测试随全套 4978 绿；DING_OK 自检标记保持（audio/DingAudioPlayer.cs 既有 _Ready 序列，仅新增公共方法零行为变更）。

## 6. 冒烟扩展（smoke 音频段判据）

Main.cs 5f 挂载 AudioDirector（并行纪律：单行 Instantiate，冲突后提交者 rebase）；headless 全链自检失败即 Quit(1)（硬门禁）：

```
AUDIO_OK bgm=3states persona=4 narrated=2 bus=3vol cc0=2 eras=2 loops=6 fade=3s
         states_played=2 bgm_vol=0.80 sfx_vol=0.60 voice_vol=0.90 ding_tiers=3
         files=6 source=cc0-kenney+procedural
```

断言链：三态迁移×3+等功率中点 √½+完成态+同目标无操作；四音色字节相异+可听；中性旁白降级双向；两旁白首播 true+重播静默+存档 round-trip；三通道独立音量回读 0.80/0.60/0.90；叮三级回归；6 循环+2 stinger+6 WAV 落盘。可听测产物：res://audio/generated/bgm-era{1,2}-{calm,combat,boss}.wav + ding-*.wav + voice-*.wav（.gdignore+.gitignore 排除，任意播放器人工验听）。

## 7. 测试清单（EraCore.Tests/Audio/，167 项）

| 文件 | 覆盖 |
|---|---|
| BgmProgramTests | 参数表解析（派生时序/累计分层/边界/越界/和弦规则/冻结行数） |
| BgmSynthTests | 六循环长度/可听/16 位安全/频带能量态分离（combat 高带>calm 1.5×/boss 低带>combat 1.15×）/两代相异/WAV 容器/半环能量均衡（无缝前提）/确定性/非法态/null/ cramped 网格守卫 |
| WavContainerTests | 双采样率规范头+非法载荷 |
| CrossFadeBusTests | 等功率全程/中点/完成/同目标无操作/重定向主导侧/skew 钳制/1-tick 窗/非法参数 |
| AudioMixerTests | 三通道独立/静音记忆/快照 round-trip/端点/越界/未定义通道/原子性 |
| PersonaVoiceTests | 封闭集解析/四音色相异/steady-sharp-bounce 轮廓信号验证/旁白相异/可听无咔哒/降级策略/音节数界/确定性/WAV/九类非法 |
| NarrationBookTests | 首播一次/持久化 round-trip/缺文件/损坏文件/陈旧 id/排序落盘/未登记 id/六类非法 |
| AudioContentTests | 三入库表集成（六循环端到端/封闭音色集/**旁白锚↔章台词 key 跨域一致**/采样率界/交叉错接拒绝） |

## 8. 决策与遗留

- **BGM 合成而非 CC0 采样**：三态分层需共享和声基座的无缝循环（CC0 环境乐逐曲三态匹配不可脚本化保证）；零成本纪律下代码合成为任务卡预批准路线。E3+ 扩曲（T-121）=bgm-programs.json 加行，不动代码。
- **OGG 入库不经 Godot import 依赖**：AudioStreamOggVorbis.LoadFromBuffer（FileAccess.GetFileAsBytes）直读，headless --quit 首跑即可用；.import 伴生件为编辑器缓存（gitignore 排除，textures 先例同构）。
- **Boss 波驱动薄版**：SetBossWave 公共接口就位，WavePlan.HasBoss→AudioDirector 接线随战役宿主（T-085 关卡集/战役状态机）落；当前冒烟直驱。
- **人格语音 UI 触发面**：PlayLineVoice/PlayCueSlot 公共接口就位；气泡逐行语音绑定归 T-093（UI/UX 完备）/T-126（四人格全量+语音组）——其任务卡依赖列已含 T-092。
- **叮音色未人格化**：任务卡口径=人格语音差异化（F-137），叮三级为全局事件音（F-112 归 F-136 音效集）；如需"人格化叮"属后续增补。
