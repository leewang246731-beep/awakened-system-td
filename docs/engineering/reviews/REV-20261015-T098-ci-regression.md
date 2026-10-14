# REV-20261015-T098-ci-regression — 组验算回归 CI 固化（T-098）+ EOL 规范化修复（T-096 打包阻塞项，验证记录并入）

| 项 | 内容 |
|---|---|
| 任务 | ① EOL 规范化修复（T-096 打包阻塞项：无 .gitattributes + core.autocrlf=true → 干净检出 CRLF → 哈希钉/针串测试红）② T-098 波次验算回归 CI 固化（组验算[回归]全 PR 必跑，数值漂移即红） |
| 依据 | T-094 隔离树/干净对照树实证（28-t094 §门禁表：13 失败=11×Llm 在线网络依赖+1×Holdout 哈希钉+1×NarrationBook schema）；AGENTS.md 纪律 9（同 seed 确定性）；CS-6（金样再生成唯一入口）；13-quality-gates（G1=dotnet test 全量） |
| 交付物 | `.gitattributes`（新建，EOL 法则）；`game/src/EraCore.Tests/Audio/NarrationBookTests.cs`（rows 针串 EOL 无关化，1 处）；本 REV。CI 零改动（PR 触发在位，§三） |
| commit | ① `chore: eol normalization via .gitattributes`（.gitattributes+测试修复+renormalize）② `T-098: ...`（本 REV） |

## 一、①EOL 修复——问题机制与实证复现

**机制**：开发机 Git for Windows **系统级** `core.autocrlf=true`（来源实测 `C:/Program Files/Git/etc/gitconfig`），仓库此前无 `.gitattributes` 对抗——全新 clone/worktree 检出时全部文本文件按 LF→CRLF 转换，工作树字节 ≠ 提交字节。

**修复前复现**（`git worktree add /d/game-wt-repro HEAD`，4603750，autocrlf 环境与 T-094 同）：

- 检出态：`git ls-files --eol` = `i/lf w/crlf attr/`（index 全 LF、检出全 CRLF、无属性）；`od -c` 可见 `\r\n`。
- 受影响测试过滤器（Holdout|AudioContent|Narration|Cassette|Corpus）→ **24 测试 2 红**：
  1. `HoldoutV1BlindTests.Holdout_v1_file_hash_is_pinned`：钉值 `cbf8794a…` vs CRLF 检出实测 `9e259d8d…`（全库**唯一**读内容文件算 SHA-256 钉死的测试——grep `HashData|ToHexString|[0-9a-f]{40,}` 全测试工程仅此一处字节钉；JournalReplayer 的 SHA-256 作用于测试运行期临时 journal，不读仓库内容；Llm cassette 走 ReadAllText+JSON/正则，CRLF 容错）。
  2. `NarrationBookTests.Illegal_schema_rejected(key="rows")`：`Assert.Throws() Failure: No exception was thrown`——**测试自身按 LF 假设写死**（任务纪律第 4 条的区分案例，非内容文件 EOL 问题）：针串 `"…stinger\": \"…ogg\" },\n"` 含显式 `\n`，而 `LegalJson` 是 raw string 字面量（C# 编译器保留源码行尾），CRLF 检出的源里实为 `…},\r\n` → 针失配 → 删行无效 → 4 行仍在 → Parse 合法 → 无异常 → 红。
- 其余 11 失败（T-094 记录的 Llm 在线/SSE）为 live 网络端点环境依赖（"服务端返回了无法解析的响应"，24 号 §5 先例），**与 EOL 无关**，不在本修复范围。

**同类隐患排查**（源内 `\n` 针串 grep 全测试工程）：`LlmGatewayStreamTests.cs:93`/`LlmGatewayErrorMatrixTests.cs:192` 的 `OpenAiSse.Replace("data: [DONE]\n\n", …)` 同为 raw-string 夹具+`\n` 针——但 Replace 失配时流仍带 [DONE] 正常归约，测试**弱化而非变红**（T-094 全量 5028 passed 中它们绿，佐证）。`*.cs eol=lf` 落地后源码检出恒 LF，此类针串从构造上安全，无需逐个改写。

## 二、①修法与验证

**`.gitattributes` 法则**（仓库级属性随 clone 走，优先级高于任何机器 autocrlf）：

1. `* text=auto` 兜底（文本检测入库规范化 LF，二进制不动）+ `.gitattributes eol=lf` 自身钉死；
2. 关键内容/源码显式 `eol=lf`（检出+入库双向）：`*.cs/*.csproj/*.sln/*.props/*.config/*.godot/*.gdignore/*.gitignore/*.gitkeep/*.editorconfig` + `*.json/*.jsonl/*.md/*.txt/*.svg` + `*.py/*.sh/*.mjs/*.js/*.ts/*.yml/*.yaml` + `*.tscn/*.tres/*.gd/*.gdshader/*.uid/*.import/*.url`；
3. 二进制显式 `binary`（`-diff -merge -text`）：`*.png/*.ogg/*.wav/*.zip/*.jar/*.dll/*.exe/*.pdb/*.so/*.dylib/*.meta`。

**renormalize 规模（如实）**：`git add --renormalize .` 对全库 1256 tracked 文件重过 clean 过滤器——**0 个 blob 重写**（index 808 文本 blob 本就全 LF、446 二进制、2 空文件；此前提交已规范入库）。本修复的价值纯在**检出侧**：attributes 使干净检出保持 LF。既有主工作树 96 个 w/crlf 文件 clean 后与 blob 全等（status 干净），不需触碰。

**修复后验证**（`git worktree add /d/game-wt-verify HEAD`，45eec13）：

- 检出态：`i/lf w/lf attr/text=auto eol=lf`（od 实证无 `\r`）；
- 受影响测试族（同过滤器 Holdout|AudioContent|Narration|Cassette|Corpus）→ **272/272 绿**（含修复的哈希钉与 rows 针两例）；
- 金样/回归族（Golden|Regression）→ **98/98 绿**（wave-e2/sim-e1/sim-e2/checkpoint-e1/regression-v1 金样内容文件的 EOL 验证顺带覆盖）；
- 主工作树 `bash tools/ci/run_all.sh` 全绿（§五）。

**NarrationBookTests 处置**（区分案例落地）：rows 针串去掉尾部 `\n`（行文本本身不含 CR/LF，EOL 无关）——LF/CRLF 源下均删成 3 行 → `RowCount` 拒绝路径不变，断言语义零变化。

## 三、②T-098——CI 固化确认（PR 触发在位，零改动）

`.github/workflows/ci.yml` `on:` 段（L30-34）：

```yaml
on:
  push:
    branches: [master, main]
  pull_request:
  workflow_dispatch:
  …
```

- **`pull_request:` 无分支过滤 = 全部 PR 必跑**（任务验收"组验算[回归]全 PR 必跑"的触发面在位，无需补）。
- **回归/验算覆盖面**（Stage 2 `dotnet test game/src/EraCore.Tests/EraCore.Tests.csproj`，G1 语义与本地 run_all Step 2 同一测试工程、无 filter 全量）：GoldenVerifications（四组验算 F.5）、WaveE2GoldenTableTests（wave-e2 金样表）、SimE2GoldenTests/SimGoldenTests（sim-e1/e2）、CheckpointGoldenTests（checkpoint-e1）、EndlessRegressionTests（T-083 参数化 1000 波回归，regression-v1 金样）、NumericRegressionTests、GoldenActsTests——push 与 pull_request 两触发同跑。
- 如实备注（非 T-098 范围，不扩改）：CI 五段与 run_all 的差异=CI Stage 1 未含 asset_validator（本地 Step 1.5 G2.5）——资产门禁不在本任务（数值回归）口径内，留档供后续任务决策。

## 四、②漂移即红语义链——本地篡改证明（REV 留档，远端零改动）

链路：金样 JSON 任一数值被篡改（或引擎重算漂移）→ `EndlessRegressionTests.Golden_window_summary_regression` 逐窗值比对 `Assert.True(equal, …)` 失败 → xUnit 红 → **`dotnet test` 退出码 1（实测）** → ci.yml Stage 2 step `run:` 非零 → GitHub Actions PR check 红。

**实证**（隔离 worktree /d/game-wt-verify 内，篡改后复原，`git status` 零残留）：

1. 篡改：`game/content/golden/regression-v1.json` windows[0] `"kills": 498 → 499`（模拟落盘金样被动过/数值漂移）；
2. 复跑：`dotnet test --filter FullyQualifiedName~EndlessRegressionTests.Golden_window_summary_regression` →

```
Failed EraCore.Tests.Regression.EndlessRegressionTests.Golden_window_summary_regression [FAIL]
   回归金样漂移：窗口 #0（wave 50）落盘 [509/499/41.75/7503793012927590745]，
   重算 [509/498/41.75/7503793012927590745]——先排查内容/公式/口径变更依据，
   再走 CS-6 再生成流程（独立 commit+依据编号）
Failed!  - Failed: 1, Passed: 0 …
```

3. 退出码实测 `= 1`（步骤红）；4. `git checkout --` 复原后同测试回绿（98/98 那轮）。

守卫带（击杀时间/通胀带）为 WARN 级不阻断（08 T-083 验收口径），阻断面=快照值+窗口/摘要哈希——与"数值漂移即红"的验收语义一致。

## 五、收尾门禁

主工作树 `bash tools/ci/run_all.sh`（NUGET_PACKAGES='D:\nuget'、DOTNET_ROOT=/d/dotnet）结果见下表（全绿后本 REV 定稿）：

| 门禁 | 结果 |
|---|---|
| G2 红线 | PASS（9 checks，0 FAIL 0 WARN） |
| G2.5 资产 | PASS（files=434 rejected=0 warned=0） |
| G1 测试 | PASS（5138 passed / 0 failed，162s） |
| G3 构建 | PASS（Release 零警告，TreatWarningsAsErrors=true，10s） |
| G4 冒烟 | PASS（SMOKE_OK，14s；含 ART/ART2/BRIDGE/PANEL 全横幅） |

（验证 worktree 两个为一次性取证环境，收尾后 `git worktree remove` 清理，不留库外残留。）

## 变更记录

- v1.0（2026-10-15）：T-098 CI 固化确认（PR 触发在位+漂移即红实证链）+ T-096 EOL 修复验证并入（.gitattributes 法则、renormalize 0 blob、前后 worktree 对照 2 红→272+98 绿、run_all 收尾）。
