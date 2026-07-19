@echo off
chcp 65001 >nul
title 资讯速览小助手 - 重启中...

echo ========================================
echo   资讯速览小助手 - 一键重启
echo ========================================
echo.

echo [1/3] 正在停止旧进程...
taskkill /f /im 资讯速览小助手.exe >nul 2>&1
taskkill /f /im cloudflared-windows-amd64.exe >nul 2>&1
taskkill /f /im cloudflared.exe >nul 2>&1
timeout /t 2 /nobreak >nul
echo 已停止。

echo.
echo [2/3] 正在启动应用 (端口 5000)...
start "" "%~dp0bin\Debug\net8.0\资讯速览小助手.exe"
timeout /t 4 /nobreak >nul
echo 应用已启动。

echo.
echo [3/3] 正在启动 Cloudflare 隧道...
echo.
echo   固定网址：https://news.nihaofushiqi.asia
echo.
start "Cloudflare隧道 - news.nihaofushiqi.asia" cmd /k ""%USERPROFILE%\cloudflared.exe" tunnel run news-briefing"

echo ========================================
echo   启动完成！
echo.
echo   访问地址：https://news.nihaofushiqi.asia （长期固定）
echo ========================================
echo.
pause
