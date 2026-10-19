@echo off
rem 打包版加速訓練：用打包好的訓練場執行檔、不顯示畫面、同時開 4 份（4 x 32 = 128 台學習車）
rem 先在 Unity 執行 Tools > 危險駕駛AI > 打包訓練場（加速訓練用）；改過訓練場程式後要重新打包
rem 訓練場是純數學模擬（固定每步 0.02 秒、不靠物理），開 4 份、100 倍速都不會讓模擬變不準
rem 已經有進度 → 自動接著訓練（--resume）；沒有進度 → 以上一版模型為起點（--initialize-from）
rem 這個方式不用開 Unity、不用按 Play。要停止請在這個視窗按 Ctrl+C（會自動存檔）
set RUNID=GhostCutIn_v6
set FROMID=
rem v6 從零開始訓練（v2～v5 學到「切在玩家車頭前 4.5m」的壞習慣，接著練改不掉）。要接續某一版就在 FROMID= 後面填 run-id
set INITARG=
if not "%FROMID%"=="" set INITARG=--initialize-from=%FROMID%
set ENVS=4
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
if not exist "TrainingBuild\GhostCutInTrain.exe" (
  echo [錯誤] 找不到 TrainingBuild\GhostCutInTrain.exe
  echo 請先在 Unity 執行 Tools ^> 危險駕駛AI ^> 打包訓練場（加速訓練用）
  pause
  exit /b
)
if exist "results\%RUNID%\GhostCutIn\checkpoint.pt" (
  echo [接著訓練] 找到 %RUNID% 之前的進度，從上次停下的地方繼續
  mlagents-learn MLTraining\GhostCutIn.yaml --run-id=%RUNID% --env=TrainingBuild\GhostCutInTrain.exe --num-envs=%ENVS% --no-graphics --time-scale=100 --resume
) else (
  echo [開始訓練] %RUNID%（起點：%FROMID%，空白 = 從零開始），同時開 %ENVS% 份訓練場
  mlagents-learn MLTraining\GhostCutIn.yaml --run-id=%RUNID% --env=TrainingBuild\GhostCutInTrain.exe --num-envs=%ENVS% --no-graphics --time-scale=100 --force %INITARG%
)
echo.
echo 訓練好的模型在：C:\AgentRL\results\%RUNID%\GhostCutIn.onnx
echo 換模型：在檔案總管「複製並覆蓋」Assets\script\危險駕駛AI\機器學習\GhostCutIn_v6.onnx，
echo 再到 Unity 的 Project 視窗把它改名成新版本（例如 GhostCutIn_v7）。不要先刪除再拖進 Unity，引用會斷
pause
