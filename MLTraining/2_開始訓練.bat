@echo off
rem 步驟 C：開始／繼續訓練 v5（引導改成「切入強度接近目標」，精準度改用平滑分數）
rem 已經有 v5 的訓練進度 → 自動接著訓練（--resume），不會把之前的成果洗掉
rem 沒有進度 → 以 v4 的模型為起點開始（--initialize-from），比從零開始快很多。之前的結果都保留不動
rem 看到「Listening on port 5004」後，60 秒內回 Unity 按 Play；要停止請在這個視窗按 Ctrl+C（會自動存檔）
set RUNID=GhostCutIn_v6
set FROMID=
rem v6 從零開始訓練（v2～v5 學到「切在玩家車頭前 4.5m」的壞習慣，接著練改不掉）。要接續某一版就在 FROMID= 後面填 run-id
set INITARG=
if not "%FROMID%"=="" set INITARG=--initialize-from=%FROMID%
rem 時間倍率 50：訓練場是純數學模擬、不靠物理，加速到 50 倍不會失真
call "%USERPROFILE%\anaconda3\Scripts\activate.bat" mlagents
if errorlevel 1 (
  echo [錯誤] 找不到 Anaconda 的 mlagents 環境，請確認安裝在 %USERPROFILE%\anaconda3
  pause
  exit /b
)
cd /d C:\AgentRL
if errorlevel 1 (
  echo [錯誤] 找不到 C:\AgentRL，請先執行「1_建立英文路徑連結.bat」
  pause
  exit /b
)
if exist "results\%RUNID%\GhostCutIn\checkpoint.pt" (
  echo [接著訓練] 找到 %RUNID% 之前的進度，從上次停下的地方繼續
  mlagents-learn MLTraining\GhostCutIn.yaml --run-id=%RUNID% --time-scale=50 --resume
) else (
  echo [開始訓練] %RUNID%（起點：%FROMID%，空白 = 從零開始）
  mlagents-learn MLTraining\GhostCutIn.yaml --run-id=%RUNID% --time-scale=50 --force %INITARG%
)
echo.
echo 訓練好的模型在：C:\AgentRL\results\%RUNID%\GhostCutIn.onnx
pause
