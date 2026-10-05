@echo off
rem 在 ServerHost 目录里直接启动后端服务器。
cd /d "%~dp0"
dotnet run --project "GameServerHost.csproj" -- %*
