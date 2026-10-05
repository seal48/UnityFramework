@echo off
rem 一键启动后端服务器（独立进程）。
rem 用法：start-server.cmd            监听 127.0.0.1:7777（被占用时自动往后找端口）
rem       start-server.cmd --port 9000
rem       start-server.cmd --selftest
rem       start-server.cmd --frametest
cd /d "%~dp0"
dotnet run --project "ServerHost\GameServerHost.csproj" -- %*
