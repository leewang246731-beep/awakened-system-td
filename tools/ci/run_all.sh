#!/usr/bin/env bash
# run_all.sh — 本地一键流水线（T-027 [DEBT-W0] 清账；与 .github/workflows/ci.yml 同序同判据）。
#
# 顺序（08 §3 任务纪律第 4 条 PR 必跑段的 W0 形态）：
#   1) redline_scan.py          → G2 红线零违规（06 §3.1 首批 7 条）
#   2) dotnet test              → G1 测试全绿（game/EraTD.sln 全部测试，不启动 Godot）
#   3) dotnet build -c Release  → G3 构建零警告（TreatWarningsAsErrors=true，警告即红）
#   4) smoke_godot.sh           → G4 Godot headless 冒烟（判据=SMOKE_OK）
# 任一步失败立即记 FAIL 并退出非 0；结尾打印门禁汇总表（G1~G4 对应
# docs/engineering/13-quality-gates.md；G5~G10 为流程性门禁，不在本脚本判定范围）。
#
# 用法：
#   bash tools/ci/run_all.sh                 # 全跑（W0 出口条件⑤ 要求至少一次不跳 smoke 的全绿记录）
#   SKIP_SMOKE=1 bash tools/ci/run_all.sh    # 跳过冒烟（G4 记 SKIP 不记 PASS——跳过不是通过）
#
# 环境纪律（09-env-locked.md）：NUGET_PACKAGES 未设时兜底 D:\nuget（禁 C 盘缓存）；
# dotnet 按 PATH → 环境事实登记的 D 盘 SDK 顺序解析；DOTNET_ROOT 兜底同 smoke_godot.sh。

set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SLN="$REPO_ROOT/game/EraTD.sln"
LOG_DIR="$(mktemp -d -t era_runall_XXXXXX)"
trap 'rm -rf "$LOG_DIR"' EXIT

# ---- 工具解析 ---------------------------------------------------------------

PYTHON=""
for cand in python3 python; do
  # 逐一实测可执行性：Windows 的 Store 别名 stub 在 PATH 上但不可用（静默失败源）
  if command -v "$cand" >/dev/null 2>&1 \
     && "$cand" -c "import sys; sys.exit(0 if sys.version_info >= (3, 8) else 3)" 2>/dev/null; then
    PYTHON="$cand"
    break
  fi
done
if [ -z "$PYTHON" ]; then
  echo "run_all: python 不可用（redline_scan.py 需要 Python 3.8+；注意 Windows Store 别名 stub 不算可用）" >&2
  exit 2
fi

if command -v dotnet >/dev/null 2>&1; then
  DOTNET_CLI="dotnet"
elif [ -x /d/dotnet/dotnet.exe ]; then
  DOTNET_CLI=/d/dotnet/dotnet.exe
else
  echo "run_all: dotnet 不可用（PATH 无 dotnet 且未找到 D 盘 SDK，见 09-env-locked.md）" >&2
  exit 2
fi

if [ -z "${DOTNET_ROOT:-}" ] && [ -d /d/dotnet ]; then
  export DOTNET_ROOT='D:\dotnet'   # Godot(mono) 定位 .NET 运行时（09-env-locked §二）
fi
if [ -z "${NUGET_PACKAGES:-}" ]; then
  if [ -d /d/nuget ]; then
    export NUGET_PACKAGES='D:\nuget'
    echo "run_all: 已兜底导出 NUGET_PACKAGES=$NUGET_PACKAGES"
  else
    echo "run_all: 警告：NUGET_PACKAGES 未设置且 D 盘缓存不存在，将落默认 C 盘缓存（违纪，见 09-env-locked）" >&2
  fi
fi
# dotnet 输出钉死英文，汇总行解析（Failed:/Passed:）不随系统区域漂移
export DOTNET_CLI_UI_LANGUAGE=en

# ---- 门禁状态收集 -----------------------------------------------------------

declare -A G_STATUS G_DETAIL
FAILED=0

record() { # $1=门禁号 $2=状态 $3=明细
  G_STATUS[$1]="$2"
  G_DETAIL[$1]="$3"
  if [ "$2" = "FAIL" ]; then FAILED=1; fi
}

step_header() {
  echo ""
  echo "==> [step $1] $2"
}

# ---- Step 1/4: G2 红线扫描 --------------------------------------------------

step_header 1 "G2 红线扫描 redline_scan.py"
if "$PYTHON" "$REPO_ROOT/tools/ci/redline_scan.py" --json "$LOG_DIR/redline.json" \
      2>&1 | tee "$LOG_DIR/redline.log"; then
  SCAN_RC=0
else
  SCAN_RC=$?
fi
if [ "$SCAN_RC" -eq 0 ]; then
  SUMMARY_LINE=$(grep -E '^summary:' "$LOG_DIR/redline.log" | tail -n 1)
  record G2 PASS "${SUMMARY_LINE#summary: }"
elif [ "$SCAN_RC" -eq 2 ]; then
  record G2 FAIL "redline_scan 运行错误（exit 2）"
  echo "run_all: 任一步失败即停（G2 扫描器故障）" >&2
  exit 1
else
  record G2 FAIL "存在 FAIL 违规（exit $SCAN_RC，明细见上方输出）"
  echo "run_all: 任一步失败即停（G2 红线违规）" >&2
  exit 1
fi

# ---- Step 2/4: G1 测试全绿 --------------------------------------------------

step_header 2 "G1 测试 dotnet test（$SLN）"
T0=$SECONDS
if "$DOTNET_CLI" test "$SLN" --nologo >"$LOG_DIR/test.log" 2>&1; then
  TEST_RC=0
else
  TEST_RC=$?
fi
TEST_SECS=$((SECONDS - T0))
tail -n 25 "$LOG_DIR/test.log"
if [ "$TEST_RC" -eq 0 ]; then
  # 汇总行（每个测试程序集一行）：Passed!  - Failed:  0, Passed: 37, ... 求和
  TP=0; TF=0
  while IFS= read -r m; do
    f=$(echo "$m" | sed -n 's/.*Failed:[[:space:]]*\([0-9]\+\).*/\1/p')
    p=$(echo "$m" | sed -n 's/.*Passed:[[:space:]]*\([0-9]\+\).*/\1/p')
    TP=$((TP + ${p:-0})); TF=$((TF + ${f:-0}))
  done < <(grep -E '^Passed!' "$LOG_DIR/test.log")
  record G1 PASS "dotnet test: $TP passed, $TF failed (${TEST_SECS}s)"
else
  record G1 FAIL "dotnet test exit $TEST_RC（末 25 行见上；完整日志 $LOG_DIR/test.log）"
  echo "run_all: 任一步失败即停（G1 测试红）" >&2
  exit 1
fi

# ---- Step 3/4: G3 构建零警告（Release） --------------------------------------

step_header 3 "G3 构建 dotnet build -c Release（警告即红）"
T0=$SECONDS
if "$DOTNET_CLI" build "$SLN" -c Release --nologo \
     -p:TreatWarningsAsErrors=true >"$LOG_DIR/build.log" 2>&1; then
  BUILD_RC=0
else
  BUILD_RC=$?
fi
BUILD_SECS=$((SECONDS - T0))
grep -E "警告|Warning|error|错误" "$LOG_DIR/build.log" | head -n 10 || true
tail -n 5 "$LOG_DIR/build.log"
if [ "$BUILD_RC" -eq 0 ]; then
  record G3 PASS "Release 构建成功，TreatWarningsAsErrors=true（零警告，${BUILD_SECS}s）"
else
  record G3 FAIL "Release 构建失败 exit $BUILD_RC（-p:TreatWarningsAsErrors=true；日志 $LOG_DIR/build.log）"
  echo "run_all: 任一步失败即停（G3 构建红）" >&2
  exit 1
fi

# ---- Step 4/4: G4 Godot 冒烟 -------------------------------------------------

if [ "${SKIP_SMOKE:-0}" = "1" ]; then
  step_header 4 "G4 Godot 冒烟 —— SKIP_SMOKE=1，跳过（记 SKIP 不记 PASS）"
  record G4 SKIP "SKIP_SMOKE=1（跳过非通过；W0 出口条件⑤ 须有不跳 smoke 的全绿记录）"
else
  step_header 4 "G4 Godot headless 冒烟 smoke_godot.sh（判据=SMOKE_OK）"
  T0=$SECONDS
  if bash "$REPO_ROOT/tools/ci/smoke_godot.sh" >"$LOG_DIR/smoke.log" 2>&1; then
    SMOKE_RC=0
  else
    SMOKE_RC=$?
  fi
  SMOKE_SECS=$((SECONDS - T0))
  tail -n 12 "$LOG_DIR/smoke.log"
  if [ "$SMOKE_RC" -eq 0 ] && grep -q "SMOKE_OK" "$LOG_DIR/smoke.log"; then
    record G4 PASS "SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，${SMOKE_SECS}s）"
  else
    record G4 FAIL "smoke exit $SMOKE_RC 或未捕获 SMOKE_OK（日志 $LOG_DIR/smoke.log）"
    echo "run_all: 任一步失败即停（G4 冒烟红）" >&2
    exit 1
  fi
fi

# ---- 汇总表 -------------------------------------------------------------------

echo ""
echo "==================== 门禁汇总（G1~G4 ↔ docs/engineering/13-quality-gates.md）===================="
for g in G1 G2 G3 G4; do
  printf '  %-4s %-14s : %-5s %s\n' "$g" "$(case $g in
    G1) echo 测试全绿;;
    G2) echo 红线零违规;;
    G3) echo 构建零警告;;
    G4) echo 冒烟通过;; esac)" \
    "${G_STATUS[$g]}" "${G_DETAIL[$g]}"
done
echo "  G5~G10 流程门禁  : 本脚本不判定（code-review 双轴/金样独立 commit/覆盖率阈值/性能门禁/文档三件套/契约变更流程——逐条见 13-quality-gates.md）"
echo "=================================================================================================="

if [ "$FAILED" -ne 0 ]; then
  echo "run_all: 总体 FAIL"
  exit 1
fi
if [ "${G_STATUS[G4]}" = "SKIP" ]; then
  echo "run_all: 总体 PASS（G4=SKIP——此记录不能充当 W0 出口条件⑤ 的冒烟证据）"
else
  echo "run_all: 总体 PASS（G1~G4 全 PASS）"
fi
exit 0
