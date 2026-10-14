# 17 - 火山方舟（Volcano Ark）视频生成 API 深度研究报告

> 研究日期：2026-10-13/14
> 目的：为 ZCode 编写"视频生成 skill"提供完整、多源交叉验证的 API 依据。
> 主要信息源：火山方舟官方文档（创建/查询任务 API、Seedance 2.5 API 参考 1544244、Seedance 2.0/1.x API 参考 1520757）、模型广场、知乎/CSDN/掘金/搜狐社区文章、GitHub 开源实践（seedance-chain 等）。
> 结论可信度标注：【官方】= 官方文档原文；【多源】= 官方+第三方交叉一致；【单源】= 仅第三方，需自行验证。

---

## 1. API 端点总览（全部【官方】）

| 操作 | 方法 | 端点 |
|---|---|---|
| 创建视频生成任务 | POST | `https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks` |
| 查询单个任务 | GET | `https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks/{id}` |
| 查询任务列表（近 7 天） | GET | `https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks` |
| 取消/删除任务 | — | 官方文档 [82379/1521720]（取消进行中任务） |

- 请求头：`Authorization: Bearer <ARK_API_KEY>`、`Content-Type: application/json`。不支持 AK/SK 签名方式调用此接口，只支持 API Key【多源：官方文档 + seedance-chain 踩坑记录】。
- 任务 id 形如 `cgt-20260414114820-xxxxx`，**任务记录保留 7 天**；cancelled 状态任务 24 小时后自动删除。

### 1.1 创建任务响应

```json
{
  "id": "cgt-20260414114820-xxxxx",
  "model": "doubao-seedance-2-5-260628",
  "status": "queued"
}
```

### 1.2 查询任务响应（完整字段）

```json
{
  "id": "cgt-20260414114820-xxxxx",
  "model": "doubao-seedance-2-5-260628",
  "status": "succeeded",            // queued | running | cancelled | succeeded | failed
  "content": {
    "video_url": "https://ark-result-generation-....volces.com/...",   // 成功时
    "last_frame_url": "https://ark-result-generation-...jpeg",          // return_last_frame=true 时
    "file_url": "https://ark-result-generation-..."                     // 输出格式非视频文件时
  },
  "usage": { "completion_tokens": 411300, "total_tokens": 411300 },     // 计费依据
  "frames": 288,
  "framespersecond": 24,
  "duration": 11,
  "ratio": "16:9",
  "resolution": "1080p",
  "generate_audio": true,
  "seed": 1149278358,
  "service_tier": "default",
  "output_format": "mp4",
  "error": { "code": "...", "message": "..." },                         // failed 时
  "created_at": 1776547797,
  "updated_at": 1776548481
}
```

- 官方轮询示例：**每 10 秒查询一次，deadline 30 分钟**【官方：创建任务文档内代码注释】。
- `duration`（响应）= 向下取整(总帧数/24)【官方】。

---

## 2. 模型清单与能力矩阵（【官方】1544244 + 1520757 + 模型广场）

| 模型 | 模型 ID | 单段时长 | 分辨率（默认） | 特殊能力 |
|---|---|---|---|---|
| Seedance 2.5 | `doubao-seedance-2-5-260628` | **[4,30] 或 -1（智能）** | 480p/720p/1080p（720p） | 全模态参考（图+视频+音频）、edit/extend 任务类型、return_last_frame、mov 输出、priority、draft 草稿模式 |
| Seedance 2.0 系列 | doubao-seedance-2-0-*（快照 ID 见控制台） | [4,15] 或 -1 | 480p/720p/1080p/4k（720p；fast/mini 仅 480p/720p） | 全能参考（1-9 图+3 视频+3 音频）、return_last_frame |
| Seedance 1.5 pro | doubao-seedance-1-5-pro-*（见控制台） | [4,12] 或 -1 | 480p/720p/1080p（720p） | 不支持 frames 参数 |
| Seedance 1.0 pro | `doubao-seedance-1-0-pro-250528` | **[2,12] 整数** | 480p/720p/1080p（**1080p**） | frames 精确控帧、camera_fixed、service_tier=flex 半价 |
| Seedance 1.0 pro fast | `doubao-seedance-1-0-pro-fast-251015` | [2,12] | 480p/720p/1080p（1080p） | 同上，更快更便宜 |
| Seedance 1.0 lite | doubao-seedance-1-0-lite-*（见控制台） | 5s/10s | 480p/720p | 最便宜入门档【单源：量子位】 |

注：`doubao-seedance-1-0-lite-*`、`1-5`、`2-0` 系列的精确快照 ID 以控制台模型广场为准，文档未在本次抓取中逐一列出。**Skill 实现应把模型 ID 做成可配置项，默认 `doubao-seedance-2-5-260628`**。

### 2.1 模型开通条件【官方】

- Seedance 2.5 / 2.0 系列：主账号**现金余额 ≥ 200 元**或持有生效资源包，否则调用报错。
- 所有模型需先在控制台"开通管理"开通（否则 ModelNotOpen）。

---

## 3. 请求参数完整规则

### 3.1 content 数组（多模态输入组合）【官方】

| 组合 | 支持情况 |
|---|---|
| text | 文生视频 |
| text + image(s) | 图生视频 / 首帧 / 首尾帧 / 多图参考 |
| text + video | 视频生视频 |
| text + audio | 仅 2.5 |
| text + image + video / + audio / 三者混合 | 2.5/2.0 全支持 |
| draft_task（草稿转正式） | 2.5 |

**图片约束**：jpeg/png/webp/bmp/tiff/gif（2.0+ 另支持 heic/heif）；宽高比 [0.4, 2.5]；边长像素 [300, 6000]；单张 < 30MB；请求体总大小 < 64MB。
- 首帧：1 张（role 不填或 first_frame）；首尾帧：2 张（role=first_frame + role=last_frame）。
- 多图参考：2.5 最多 **30 张**（role=reference_image）；2.0 最多 **9 张**。

**视频约束**：mp4/mov；分辨率 480p~4k；2.5：单条 [2,30]s、最多 10 条、总时长 ≤ 30s（edit 任务单条 [4,30]s）；2.0：单条 [2,15]s、最多 3 条、总 ≤ 15s；单条 < 200MB；FPS [24,60]。
**音频约束**：wav/mp3；2.5：单条 [2,30]s、最多 10 条、总 ≤ 30s、单条 < 15MB；2.0：单条 [2,15]s、最多 3 条、总 ≤ 15s。
- 本地文件：SDK（Python/Go/Java）`image_url` 字段可直接传 bytes 二进制；curl 需公网 URL 或 base64 data URL【官方】。
- **尾帧续拍链路中，首帧图片建议压缩到 1MB 以内**，否则提交后可能长时间无响应【单源：seedance-chain 踩坑】。

### 3.2 关键参数表【官方】

| 参数 | 类型/取值 | 说明 |
|---|---|---|
| `model` | string | 模型 ID 或推理接入点 ID（ep-xxx） |
| `resolution` | 480p/720p/1080p/4k | 默认 720p（1.0 系列默认 1080p） |
| `ratio` | 16:9, 4:3, 1:1, 3:4, 9:16, 21:9, adaptive | 2.5 默认 adaptive；**edit/extend/首帧任务只支持 adaptive** |
| `duration` | int | 2.5: [4,30] 或 -1（模型智能决定）；2.0: [4,15] 或 -1；1.5: [4,12] 或 -1；1.0: [2,12]。**edit 任务只能 -1** |
| `frames` | int [29,289] | 仅 1.0 pro/fast；格式 25+4n；与 duration 二选一 |
| `generate_audio` | bool | **默认 true**（2.5/2.0 系列生成音频）。**不影响计费单价**（音频包含在 token 计费内，无音频附加费）【官方参数无加价说明 + EvoLink 明示"音频已包含无需额外付费"】 |
| `watermark` | bool | **默认 false**；true = 右下角"AI 生成"标识。注意语义：**true 才加水印** |
| `return_last_frame` | bool | 默认 false；true 时任务完成后 `content.last_frame_url` 返回**尾帧图片（无水印、与视频同宽高）**——官方指定的连续多段视频生成方式 |
| `omni_reference_task_type` | auto(默认)/reference/edit/extend | 仅 2.5。extend=续拍（必须有 reference_video，ratio 必须 adaptive）；edit=编辑（必须有 reference_video，视频时长 [4,30]，duration 必须 -1） |
| `seed` | [-1, 2147483647] | 随机种子 |
| `camera_fixed` | bool | 仅 1.0 pro/fast |
| `service_tier` | default/flex | **flex = 半价**，仅 1.0 pro/fast |
| `output_format` | mp4/mov | 仅 2.5，默认 mp4 |
| `draft` | bool | 草稿模式（480p 快速出稿），仅 2.5 |
| `callback_url` | string | 任务状态变更回调（queued/running/succeeded/failed/expired） |
| `execution_expires_after` | int 秒 | 默认 172800（48h），范围 [3600, 259200] |
| `priority` | [0,9] | 仅 2.5/2.0 |
| `tools` | [{type: web_search}] | 仅 2.5/2.0 |

### 3.3 弱参数传递（容错）

- 未识别参数默认忽略（不报错）；curl 可用查询串传弱参：`--rs 720p --rt 16:9 --dur 5 --seed 11 --cf false --wm true`（对应 resolution/ratio/duration/seed/camera_fixed/watermark）【官方】。

### 3.4 duration 上限结论（重点核实）

- **用户"单次 30 秒"的说法成立，但仅限 Seedance 2.5**：`duration` 取值 **[4, 30] 或 -1**（智能时长）【官方 1544244】。
- Seedance 2.0 系列：上限 **15 秒**【官方】。
- Seedance 1.5：上限 12 秒；1.0 系列：**2~12 秒**（且 2.0 前的智能时长 -1 不适用）【官方 1520757】。
- 注意：2.5 的 edit 任务 duration 只能 -1；参考视频总时长 ≤ 30s。

---

## 4. 计费（按 token，多源交叉）

### 4.1 官方单价表

| 模型 | 不含视频输入（纯文/图生） | 含视频输入 | 来源 |
|---|---|---|---|
| Seedance 2.5 | **70 元/百万 tokens** | **42 元/百万 tokens** | 【官方模型广场 + 知乎交叉】 |
| Seedance 2.0 | 46 元/百万 tokens | 28 元/百万 tokens | 【搜狐/掘金引官方】 |
| Seedance 2.0 mini | 0.023 元/千（23 元/M） | 0.014 元/千（14 元/M） | 【东方财富引官方】 |
| Seedance 1.0 pro | 15 元/百万 tokens（不分档） | 同 | 【OSCHINA/官方发布】 |
| Seedance 1.0 pro fast | 更低（约为 pro 的 60-70%，具体见控制台） | 同 | 【单源】 |

- **generate_audio 不加价**：音频生成包含在输出 token 计费内【官方参数文档无加价 + EvoLink"已包含"】。
- **只对成功生成的视频计费**，审核失败/生成失败不收费【多源：老张博客 + 官方计费原则】。
- token 消耗量由 分辨率 × 时长 决定（官方 usage 返回实际值；失败任务 usage.completion_tokens=0）。
- `service_tier: "flex"` = 5 折（仅 1.0 pro/fast）【官方】。
- 资源包可抵扣（Seedance 2.0 系列有专属资源包规则文档）。
- 新用户免费额度：注册即送，具体数值在控制台"开通管理"实时查询（文档不列固定值）。

### 4.2 按秒折算（实测口径，多源）

| 场景 | 成本 |
|---|---|
| 2.5 480P | ≈ ¥0.67/秒（官方 5s 示例 ¥3.36） |
| 2.5 720P | ≈ ¥1.5/秒（官方 5s 示例 ¥7.56）；30s 约 ¥45 起 |
| 2.5 1080P | ≈ ¥3.7/秒（API 上线首月 72 折曾至 ¥2.7/s，活动 2026-09-17 已结束） |
| 2.0 720P（默认） | ≈ ¥0.95/秒（15s ≈ 30.888 万 tokens ≈ ¥14.2） |
| 2.0 mini 720P | ≈ ¥0.5/秒 |
| 1.0 pro 1080P | ≈ ¥0.73/秒（5s 1080P ≈ ¥3.67） |
| 1.0 pro 480P | ≈ ¥0.16/秒（按 token 量折算） |

### 4.3 最便宜测试组合建议（为"只测几秒"）

按"能跑通全流程（创建→轮询→下载）且花费最小"：

1. **首选（若已开通 2.5）**：`doubao-seedance-2-5-260628` + `resolution: "480p"` + `duration: 4`（2.5 最小时长）+ `generate_audio: false` + `watermark: true`。成本 ≈ **¥2.4~2.7**（480P 0.67/s × 4s）。前提：账户余额 ≥ 200 元才能开通 2.5。
2. **更便宜**：`doubao-seedance-1-0-pro-250528` + `resolution: "480p"` + `duration: 2`（1.0 系列最小 2s）→ ≈ **¥0.3**；加 `service_tier: "flex"` 再减半 ≈ ¥0.15。缺点：2~12s 上限、无智能时长。
3. **中间档**：Seedance 2.0 mini 480p 5s ≈ ¥0.2 级别（需确认快照 ID 可用）。
4. 先在控制台"开通管理"确认是否有开户赠送 tokens，可 0 成本测试。
5. 通用省钱要点：480p + 不带参考视频（70 元/M 档但 token 总量最小）+ 用 draft 草稿模式（2.5，480p 快速验证构图，计费另计但更快）。

---

## 5. 限流与并发

- 限流对象：**同主账号 + 同模型**（不区分版本），由方舟设定，**不可手动调整**，预扣机制（提交即扣额度）【官方 1848593 / 1359411】。
- 视频生成模型默认限流数值在控制台"模型列表"页展示（JS 动态加载，本次未能程序化抓取固定数值）。社区经验：新账号视频并发默认约 **2 个任务**（seedance-chain 默认 batch-size=2；提额需工单/商务）【单源，以控制台为准】。
- 轮询建议（官方示例）：**每 10s 查询一次，单任务最长等 30 分钟**；另有 callback_url 可免轮询。
- 任务级超时：`execution_expires_after` 默认 48h（任务在队列/运行的最长存活）。
- Skill 实现建议：创建任务后 10s 间隔轮询 + 总超时 10~30 分钟 + 失败重试（失败不计费）；并发跑多任务时控制在 2 以内，遇 429/限流错误指数退避。

---

## 6. 长视频拼接（"轮做连接成几分钟"）

### 6.1 官方支持的两条路径【官方】

1. **尾帧续拍（推荐，全系列可用）**：
   - 创建任务时 `return_last_frame: true` → 完成后从 `content.last_frame_url` 拿到尾帧图（无水印、与视频同宽高）。
   - 24h 有效期内下载该图，作为下一段任务的 `first_frame` 图，循环 N 段。
   - 官方原文："可将上一段视频的尾帧作为下一个视频任务的首帧，快速生成多个连续视频"。
2. **Seedance 2.5 原生 extend 续拍**：`omni_reference_task_type: "extend"`，任务中放 `reference_video`（上段视频 URL）+ text 描述后续内容，模型直接基于参考视频续拍；要求 ratio=adaptive。视频参考输入还走更便宜的 42 元/M 档。
   - 另有 `edit` 类型（改写已有视频，duration 必须 -1）。

### 6.2 社区实践【多源：GitHub/CSDN/X】

- **seedance-chain**（github.com/PCPrincipal67/seedance-chain，Python，MIT）：
  - SHOTS 列表定义镜头（id/prompt/first_frame/duration/generate_audio）；`--chain` 模式串行（batch-size 必须 1）自动"尾帧→下一段首帧"；最后 ffmpeg 拼接。
  - 拼接命令要点：**重编码** `-c:v libx264 -c:a aac` 并做去重复帧处理，避免各段编码差异导致的衔接跳帧/花屏。
  - 踩坑：首帧图压缩到 1MB 内；链中段人物一致性会漂移，需高质量首帧 + prompt 中重复关键特征。
- **CSDN 长剧实战**（blog.csdn.net/yunhhhhh/article/details/166790081）："首尾帧接戏"三阶方案（资产层→生成层→校验层），解决角色崩脸/场景漂移/镜头断点。
- **X 实操**：先出前 10s → 截尾帧 → 作下段首帧续拍，避免生硬拼接画风跳变。

### 6.3 推荐生产方案（写进 skill）

- 单段：2.5，duration=30（或 -1），return_last_frame=true。
- 分钟级长片：N × 30s 尾帧链；或 2.5 extend 模式（一段 30s 参考视频 + 续写 prompt）。
- 拼接：ffmpeg concat + 统一重编码（libx264/aac，24fps，同分辨率）。
- 每段下载与尾帧保存要在 24h URL 有效期内完成；2.5 视频 URL **限下载 100 次**。

---

## 7. 结果下载【官方】

- `video_url` / `last_frame_url` **有效期 24 小时**；Seedance 2.5 视频 URL 下载次数上限 **100 次**。
- 下载即普通 HTTP GET（`curl -o out.mp4 "<video_url>"`），无需鉴权头（预签名 URL）。
- 任务记录（查询接口）保留 7 天；建议生成后立即转存对象存储/本地。

---

## 8. curl 完整示例

### 8.1 创建任务（文生视频 + 30 秒 + 返回尾帧）

```bash
curl -s -X POST "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks" \
  -H "Authorization: Bearer $ARK_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "model": "doubao-seedance-2-5-260628",
    "content": [
      {"type": "text", "text": "一位旅人穿过雨后竹林，镜头缓慢推进，电影感，自然音效"}
    ],
    "duration": 30,
    "resolution": "720p",
    "ratio": "16:9",
    "generate_audio": true,
    "watermark": false,
    "return_last_frame": true
  }'
# → {"id":"cgt-...","model":"doubao-seedance-2-5-260628","status":"queued"}
```

### 8.2 图生视频（首帧，1.0 系列最便宜测试）

```bash
curl -s -X POST "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks" \
  -H "Authorization: Bearer $ARK_API_KEY" -H "Content-Type: application/json" \
  -d '{
    "model": "doubao-seedance-1-0-pro-250528",
    "content": [
      {"type": "text", "text": "镜头缓慢拉远，城市夜景，霓虹闪烁"},
      {"type": "image_url", "image_url": {"url": "https://example.com/frame.jpg"},
       "role": "first_frame"}
    ],
    "duration": 2,
    "resolution": "480p",
    "watermark": true,
    "service_tier": "flex"
  }'
```

### 8.3 轮询（每 10s，最长 30 分钟）

```bash
TASK_ID="cgt-xxxx"
while true; do
  RESP=$(curl -s -H "Authorization: Bearer $ARK_API_KEY" \
    "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks/$TASK_ID")
  STATUS=$(echo "$RESP" | python -c "import sys,json;print(json.load(sys.stdin)['status'])")
  echo "$(date +%T) status=$STATUS"
  case "$STATUS" in
    succeeded) echo "$RESP" | python -m json.tool; break ;;
    failed)    echo "$RESP" | python -m json.tool; exit 1 ;;
    cancelled) echo "cancelled"; exit 1 ;;
  esac
  sleep 10
done
```

### 8.4 下载视频与尾帧

```bash
curl -o clip_001.mp4  "$VIDEO_URL"
curl -o clip_001_last.jpg "$LAST_FRAME_URL"   # 下一段 first_frame 用
```

### 8.5 2.5 原生续拍（extend）

```bash
curl -s -X POST "https://ark.cn-beijing.volces.com/api/v3/contents/generations/tasks" \
  -H "Authorization: Bearer $ARK_API_KEY" -H "Content-Type: application/json" \
  -d '{
    "model": "doubao-seedance-2-5-260628",
    "content": [
      {"type": "text", "text": "承接参考视频画面与运镜，继续向前探索，风格保持一致"},
      {"type": "video_url", "video_url": {"url": "https://example.com/clip_001.mp4"},
       "role": "reference_video"}
    ],
    "omni_reference_task_type": "extend",
    "ratio": "adaptive",
    "duration": 30
  }'
```

### 8.6 ffmpeg 拼接（重编码防跳帧）

```bash
printf "file 'clip_001.mp4'\nfile 'clip_002.mp4'\nfile 'clip_003.mp4'\n" > list.txt
ffmpeg -y -f concat -safe 0 -i list.txt -c:v libx264 -c:a aac -r 24 final.mp4
```

---

## 9. 错误码与常见坑

| 错误/坑 | 处置 |
|---|---|
| ModelNotOpen | 控制台"开通管理"开通对应模型 |
| 余额不足 200 元 | 2.5/2.0 系列开通硬门槛，充值或买资源包 |
| 上传图片长时间无响应 | 压缩首帧图 < 1MB |
| 首尾帧/edit/extend 任务 ratio 报错 | 这些任务 ratio 只能 adaptive |
| 2.5 edit 任务 duration 报错 | 只能 -1 |
| URL 过期 | 24h 内下载；2.5 限 100 次 |
| 弱参数传错 | 未识别参数默认忽略，不报错（排查时注意） |
| 限流 429 | 预扣机制；并发 ≤2，指数退避；提额走工单 |

---

## 10. 来源链接表

### 官方（火山方舟文档）

| 内容 | URL |
|---|---|
| 创建视频生成任务 API | https://www.volcengine.com/docs/ark/create-video-generation-task-api |
| 查询视频生成任务 API | https://www.volcengine.com/docs/ark/get-video-generation-task-api |
| 查询任务列表 API | https://www.volcengine.com/docs/82379/1521675 |
| Seedance 2.5 API 参考（1544244） | https://www.volcengine.com/docs/82379/1544244 |
| Seedance 2.0/1.x API 参考（1520757） | https://www.volcengine.com/docs/82379/1520757 |
| 视频生成入门教程 | https://www.volcengine.com/docs/ark/video-generation-tutorial |
| Seedance 2.5 教程 | https://www.volcengine.com/docs/ark/seedance-2-5 |
| 模型价格 | https://www.volcengine.com/docs/82379/1544106 （同 /docs/ark/model-pricing） |
| 计费说明 | https://www.volcengine.com/docs/82379/1099320 |
| 突发流量/限流最佳实践 | https://docs.volcengine.com/docs/82379/1848593 |
| 限流预扣 FAQ | https://docs.volcengine.com/docs/82379/1359411 |
| Seedance 2.0 资源包规则 | https://www.volcengine.com/docs/ark/seedance-2-0-model-resource-pack-rules |
| 免费推理额度 | https://docs.volcengine.com/docs/ark/free-inference-quota |
| 模型广场 Seedance 2.5（价格） | https://ark.volcengine.com/region:cn-beijing/model/detail?Id=doubao-seedance-2-5 |
| 取消/删除任务 | https://www.volcengine.com/docs/82379/1521720 |
| LAS 版 Seedance 1.x 说明（6492/2165104） | https://docs.volcengine.com/docs/6492/2165104 |

### 第三方交叉验证

| 内容 | URL |
|---|---|
| seedance-chain 链式续拍工具 | https://github.com/PCPrincipal67/seedance-chain |
| Seedance 长剧生产实战（CSDN） | https://blog.csdn.net/yunhhhhh/article/details/166790081 |
| Seedance 2.0 定价（搜狐） | https://www.sohu.com/a/992591198_121850782 |
| 1 秒 1 元定价（掘金） | https://juejin.cn/post/7615004797963681846 |
| 2.0 mini 定价（东方财富） | https://wap.eastmoney.com/a/202606163772833846.html |
| 1.0 pro 定价（OSCHINA） | https://www.oschina.net/news/354815 |
| Token 采购与 2.5 涨价分析（知乎） | https://zhuanlan.zhihu.com/p/2070186957263599209 |
| 渠道价格对比（X） | https://x.com/rionaifantasy/article/2085012332262949359 |
| 720P 每秒成本（什么值得买） | https://post.smzdm.com/p/a825vzd7 |
| EvoLink 音频不加价说明 | https://evolink.ai/zh/blog/seedance-2-0-pricing-api-cost-guide |
| OpenRouter Seedance 2.5 | https://openrouter.ai/bytedance/seedance-2.5 |
| API易 Seedance 2.0/2.5 API 参考 | https://docs.apiyi.com/api-capabilities/seedance2/video-generation |
| AgentsFlex Seedance 文档 | https://agentsflex.com/zh/video/volcengine.html |
| Java 对接 1.5 pro（CSDN） | https://blog.csdn.net/weixin_36332085/article/details/159283145 |
| 首尾帧衔接法（X） | https://x.com/Leslieyu0/status/2023776730591490541 |
| Seedance 1.0 lite 发布（量子位） | https://www.qbitai.com/2025/05/282155.html |
| volcengine-generate-video-skill | https://github.com/kaoputou/volcengine-generate-video-skill |

---

## 11. Skill 设计要点汇总（给 ZCode skill 的直接输入）

1. 端点固定为 `ark.cn-beijing.volces.com`，Bearer API Key（env `ARK_API_KEY`）。
2. 流程：POST 创建 → 保存 task id → 10s 间隔 GET 轮询（总超时 30min）→ succeeded 取 video_url → 立即下载（24h/100 次）。
3. 参数透传：model/duration/ratio/resolution/generate_audio/watermark/return_last_frame/seed/omni_reference_task_type；duration 上限按模型校验（2.5=30，2.0=15，1.x=12）。
4. 长视频：默认 return_last_frame=true，提供 `--chain N` 模式（尾帧→首帧循环）+ ffmpeg 拼接。
5. 便宜冒烟测试：1.0 pro 480p/2s/flex（≈0.15 元）或 2.5 480p/4s（≈2.4 元）。
6. 水印语义：`watermark: true` = 加"AI 生成"水印（默认 false 不加）。
7. 音频：2.5/2.0 默认生成（generate_audio 默认 true），不加价；拼接长片时各段统一 generate_audio 设置避免音轨缺失。
