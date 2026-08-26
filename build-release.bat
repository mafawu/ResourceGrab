@echo off
rem ============================================================
rem  一键编译 Release 版本
rem  双击本文件即可运行，编译结果会保留在窗口中
rem ============================================================

setlocal
chcp 65001 >nul
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [错误] 未找到 dotnet 命令，请先安装 .NET SDK：
    echo        https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo 开始编译 Release 版本...
echo 解决方案: ResourceGrab.slnx
echo.

dotnet build ResourceGrab.slnx -c Release

if errorlevel 1 (
    echo.
    echo [失败] 编译出错，请检查上方日志。
    pause
    exit /b 1
)

echo.
echo [成功] 编译完成，0 错误！
echo 输出目录: BIN\Release\
echo.
pause
endlocal
