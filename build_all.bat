@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo ==========================================================
echo  AutoCADBlockAttrPlugin - 编译 AutoCAD 2010~2024 全部版本
echo  产物目录: dist_all\
echo  加载后命令行会打印加载结果/命令/用法，EditBlockAttHelp 可重看
echo ==========================================================
echo.

set "PY="
where py >nul 2>&1 && set "PY=py -3"
if not defined PY (where python >nul 2>&1 && set "PY=python")
if not defined PY (
  echo [错误] 未找到 Python 3，请先安装 Python 3 或直接用命令行执行 build_all.py
  goto :end
)

%PY% tools\build_all.py
if errorlevel 1 (
  echo.
  echo [失败] 构建或校验未通过，请查看上方输出。
) else (
  echo.
  echo [完成] 15 个版本 DLL 已生成到 dist_all\
)

:end
echo.
if not defined PLAREA_NO_PAUSE pause
