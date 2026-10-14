#!/bin/bash
# 新电脑执行：开发环境一键装（全 D 盘）。前置：已装 Git + Node.js。
set -e
echo "== 1/4 .NET 8 SDK -> D:\dotnet =="
mkdir -p /d/dotnet /d/nuget
if [ ! -f /d/dotnet/dotnet.exe ]; then
  curl -sL https://dot.net/v1/dotnet-install.ps1 -o /d/dotnet-install.ps1
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'D:\dotnet-install.ps1' -Channel 8.0 -InstallDir 'D:\dotnet'
fi
/d/dotnet/dotnet.exe --version
echo "== 2/4 Godot 4.7.2 mono -> D:\game\tools\godot_extract =="
GODOT_DIR=/d/game/tools/godot_extract
if [ ! -d "$GODOT_DIR" ]; then
  mkdir -p /d/game/tools/godot_dl
  curl -sL -o /d/game/tools/godot_dl/godot.zip "https://github.com/godotengine/godot/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_win64.zip"
  unzip -q -o /d/game/tools/godot_dl/godot.zip -d /d/game/tools/godot_extract_tmp
  inner=$(find /d/game/tools/godot_extract_tmp -maxdepth 1 -type d -name 'Godot_v*')
  mv "$inner" "$GODOT_DIR" && rm -rf /d/game/tools/godot_extract_tmp /d/game/tools/godot_dl
fi
find "$GODOT_DIR" -name "Godot_v*console.exe" | head -1
echo "== 3/4 用户环境变量 DOTNET_ROOT + PATH =="
powershell.exe -NoProfile -Command "[Environment]::SetEnvironmentVariable('DOTNET_ROOT','D:\dotnet','User'); \$p=[Environment]::GetEnvironmentVariable('Path','User'); if(\$p -notlike '*D:\dotnet*'){[Environment]::SetEnvironmentVariable('Path', (\$p.TrimEnd(';')+';D:\dotnet'),'User')}; 'ENV OK'"
echo "== 4/4 完成。下一步：clone 仓库 + 密钥归位（见 docs/engineering/23-migration-plan.md §三）=="
