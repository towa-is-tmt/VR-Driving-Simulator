@echo off
rem 看訓練曲線：雙擊後用瀏覽器開 http://localhost:6006
call "%USERPROFILE%\anaconda3\Scripts\activate.bat" mlagents
cd /d C:\AgentRL
tensorboard --logdir results
pause
