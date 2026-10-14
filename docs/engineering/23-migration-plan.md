# 23 新电脑迁移方案（ZCode 全环境 + 游戏项目）

> 目标：明天在新电脑上从零装 ZCode，10 分钟内恢复到"现在这台机器的全部工作能力"继续开发。
> 原则：**代码走 GitHub、密钥走 U 盘、工具全 D 盘重装**（新电脑性能更好，重装一遍最干净）。

## 一、需要迁移的东西清单（本机盘点结果）

| 类别 | 位置（本机） | 迁移方式 |
|---|---|---|
| 游戏项目（代码+文档+测试） | `D:\game`（git 仓库） | ✅ 已在 GitHub：`leewang246731-beep/awakened-system-td`（私有）→ 新机 clone |
| SSH 密钥（推送 GitHub 用） | `C:\Users\hydsoft\.ssh\id_ed25519(+.pub)` | U 盘（打包脚本已含）或新机新生成+网页添加 |
| 自定义 skill（视频生成） | `C:\Users\hydsoft\.zcode\skills\ark-video\` | U 盘（脚本已含）；`code-review`/`tdd` 是内置不用管 |
| API 密钥（vibeapi 生图/MiMo/ark/GitHub token） | `D:\game\config\*.txt` + `secrets.local.json`（**gitignore 不会进 GitHub**） | U 盘（脚本已含）|
| U 盘素材库 | `E:\game-assets` + `E:\game-videos` | 本来就在 U 盘，直接带走 |
| .NET SDK 8 | `D:\dotnet` | 新机重装（脚本自动） |
| Godot 4.7.2 mono | `D:\game\tools\godot_extract` | 新机重装（脚本自动下载） |
| NuGet 缓存 | `D:\nuget` | 新机自动重建 |
| 用户环境变量 | `DOTNET_ROOT=D:\dotnet`、PATH 追加 | 新机设置脚本自动 |

## 二、在旧电脑上执行（今天就做，2 分钟）

```bash
bash D:/game/tools/migrate/export_bundle.sh
```
→ 把 SSH 密钥、ark-video skill、全部 API 密钥打包成 `E:\zcode-migration\zcode_bundle.zip`（**加密 zip 或用完即删**——里面有全部钥匙）。
> U 盘插着即可；之后 U 盘拿到新电脑。

## 三、在新电脑上执行（明天，按顺序）

### 第 1 步：装基础环境（管理员 PowerShell 或 Git Bash）
```powershell
# 1) 装 Git（如没有）：winget install Git.Git
# 2) 装解压工具默认有；装 Node.js 22+（ZCode 依赖）：winget install OpenJS.NodeJS.LTS
# 3) 装 ZCode（官方渠道下载安装）
```

### 第 2 步：恢复迁移包
```bash
# U 盘插上后（假设盘符 E:）
mkdir -p ~/.ssh ~/.zcode/skills
# 解包（zip 内目录结构已组织好）：
unzip E:/zcode-migration/zcode_bundle.zip -d ~/zcode_restore
# SSH 密钥 →
cp ~/zcode_restore/ssh/* ~/.ssh/ && chmod 600 ~/.ssh/id_ed25519
# skill →
cp -r ~/zcode_restore/skills/ark-video ~/.zcode/skills/
# API 密钥 →（等第 4 步建好 D:\game 再放）
```

### 第 3 步：开发环境一键装（全 D 盘，不碰 C）
```bash
bash D:/game/tools/migrate/setup_new_machine.sh
```
→ 自动：装 .NET 8 SDK 到 `D:\dotnet` → 下载 Godot 4.7.2 mono 到 `D:\game\tools\godot_extract` → 设 `DOTNET_ROOT` 用户环境变量 → 建 `D:\nuget` → 验证 `dotnet --version`。

### 第 4 步：克隆项目 + 放回密钥
```bash
cd /d/ && git clone git@github.com:leewang246731-beep/awakened-system-td.git game
mkdir -p /d/game/config
cp ~/zcode_restore/config/* /d/game/config/   # API 密钥归位（gitignore 保护）
cd /d/game && git log --oneline | head -3      # 确认历史完整
```

### 第 5 步：验证全链路（5 分钟）
```bash
export NUGET_PACKAGES='D:\nuget' DOTNET_ROOT=/d/dotnet
bash tools/ci/run_all.sh    # 应全绿（G1~G4）
# 视频生成 skill 验证（可选，花 2.7 元）：
node ~/.zcode/skills/ark-video/scripts/arkvideo.mjs submit --i-know-the-cost --model doubao-seedance-2-5-260628 --duration 4 --resolution 480p --prompt "test" --label newpc
```

## 四、ZCode 本身的配置迁移说明

- **skills**：`ark-video` 随迁移包走（上 §2）；内置 skill（code-review/tdd）装 ZCode 自动有。
- **MCP**：本会话用到的 MCP（web_reader/image-search/analyze_image/node_repl）是 ZCode 插件市场/内置自带，新机装好 ZCode 后在插件面板重新启用即可，无需迁移文件。
- **会话上下文**：对话历史不迁移（每台机器独立）；把 `D:\game\docs\engineering\` 当作"团队记忆"——所有决策/审查/任务编排在库里，新会话读文档即可无缝接续（这正是我们把一切都写进 docs 的原因）。
- **git 身份**：`git config --global user.name "gamedev" && git config --global user.email "dev@local.game"`（或换你的 GitHub 邮箱）。

## 五、新电脑的并行度建议（性能更好后）

- 中端机：2 个实现代理并行 + 1 审查（当前纪律）；
- 高端机（≥16 核）：可升 3 实现+1 审查——**但性能门禁测试（spike①/SimGrouping）永远单独跑最后一步**（已写入工作纪律），避免墙钟抖动。
