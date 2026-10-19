@echo off
rem 步驟 A：建立英文路徑連結（只需要做一次）
rem 原因：ML-Agents 的連線元件（gRPC）讀不到含中文的路徑，專案在「桌面」底下會連不上 Python
rem 做法：建立 C:\AgentRL，它和原專案是「同一個資料夾」，不會複製檔案
if exist C:\AgentRL\Assets (
  echo [已存在] C:\AgentRL 已經建立過了，不用再建。
) else (
  mklink /J C:\AgentRL "%~dp0.."
)
echo.
echo 接下來：Unity Hub ^> Add ^> Add project from disk ^> 選 C:\AgentRL
pause
