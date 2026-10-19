# 灰車路網整合 SOP(以後拿到新的 unitypackage 就照這個做)

## 前置:先備份

1. Unity 存檔(Ctrl+S)
2. 把 `Assets/Scenes/cc.unity` 複製一份到專案外(例如桌面,加日期)
3. 把 `Assets/車流資料/CarNavigation.cs` 也備份一份(如果對方也改了程式)

---

## 步驟 1:匯入套件(只拿物件,不拿腳本)

`Assets → Import Package → Custom Package...`

**匯入視窗:先按左下角 `None` 全部取消,再只勾要的**

| 要勾 | 不要勾 |
|------|--------|
| ✅ prefab(車輛/機車) | ❌ **所有 .cs**(尤其 CarNavigation / WaypointNode / TrafficLightTrigger) |
| ✅ 模型 .fbx、材質 .mat、貼圖 | ❌ 場景檔 .unity |
| ✅ 車流同捆包.prefab(路網在裡面) | ❌ 有 🔄 圖示的任何檔案(代表會覆蓋) |

> 🔄 圖示 = 會覆蓋你專案裡的同名檔案。看到就要小心。

**若 prefab 出現 Missing Script**:
`Tools → 清除 Missing Scripts (選取的 Prefab)`

---

## 步驟 2:拖進場景並對齊

1. 把 `車流同捆包.prefab` 拖進場景
2. 右鍵 → **Unpack Prefab Completely**
3. **對齊**:移動根物件,讓新舊節點重合
   - 本次專案使用的對齊值:`Position (445.4, 2.05, 168.4)`,Rotation/Scale 不動
   - 對齊方法:挑一個兩邊都有的路口節點,算出座標差,填進根物件 Position

---

## 步驟 3:依序執行工具(每步都要先選取「新拖進來的根物件」)

| 順序 | 選單 | 作用 |
|------|------|------|
| 1 | `Tools → 刪除新路網底下的車輛與號誌大腦` | 刪掉他的車和 Brain(你的不動) |
| 2 | `Tools → 合併路網(刪除重複節點並自動接縫)` | 刪重疊節點、保留新增的、自動接縫 |
| 3 | `Tools → 清除場景中所有重疊的紅綠燈(去重)` | 觸發區去重 |
| 4 | `Tools → 清除重疊的紅綠燈桿(依位置去重)` | 燈桿模型去重 |
| 5 | `Tools → 自動接線:紅綠燈 → Brain` | 新路口的燈才會變色 |

**步驟 2 的確認視窗要看數字**:
- 重疊數 ≈ 大部分、新增數 ≈ 少數 → 對齊正確,執行
- 重疊 = 0 → **對齊失敗**,按取消重新對齊

---

## 步驟 4:修復與驗證

| 選單 | 用途 |
|------|------|
| `Tools → 刪除場景中沒有路徑點的車` | 修「合併後車不會動」(節點被刪→currentWaypointNode 變 None) |
| `Tools → 一鍵清除所有空連線(None)` | 清掉 nextChoices 裡的 None |
| `Tools → 檢查路網(找出斷點與孤兒節點)` | 找死路/孤兒,點 Log 可跳到節點 |
| `Tools → 檢查紅綠燈觸發區設定` | 看 A/B 組比例(應接近 1:1) |
| `Tools → 列出所有紅綠燈桿位置` | 診斷燈桿重疊 |

**檢查路網的判讀**:
- 空連線 → 一定要清成 0
- 死路/孤兒在**地圖邊界** → 正常
- 死路/孤兒在**路中間** → 接縫沒接好,手動補 `nextChoices`

---

## 步驟 5:Play 實測清單

- [ ] 灰車/機車會跑到新路段
- [ ] 交界處不卡車
- [ ] 新路口:燈會變色、車依燈停走
- [ ] 若「綠燈停、紅燈走」→ 該路口 `TrafficLightTrigger` 的 `Is North South Side` 勾反了
- [ ] 救護車避讓正常
- [ ] 故障車繞行正常
- [ ] 玩家倒車 → 連鎖後退正常

---

## 常見問題

**Q: 合併後車全部不會動**
A: 它們的 `currentWaypointNode` 指向被刪的節點 → 跑 `刪除場景中沒有路徑點的車`,由 Spawner 補生

**Q: 新路口的燈不會變色**
A: 沒接到 Brain → 跑 `自動接線:紅綠燈 → Brain`。若還是不動,檢查燈物件命名是否為 `Red_A` / `Yellow_A` / `Green_A` / `Red_B` / `Yellow_B` / `Green_B`(可含 `(1)` 後綴)

**Q: 節點沒被合併(重疊數 0)**
A: 對齊沒做好,或容差太小。容差在 `MergeWaypointNetworkTool.cs` 的 `MERGE_TOLERANCE`(預設 2.5m)

**Q: 對方也改了 CarNavigation.cs 怎麼辦**
A: 不要直接覆蓋。用比對工具(VS Code Compare)看他改了什麼,手動把他的新功能移植到你的版本;你的版本裡有整套危險因子系統(救護車避讓、故障車繞行、連鎖倒車、卡死淡出),絕不能被蓋掉

---

## 你的 CarNavigation.cs 有哪些是「你的」(合併時務必保留)

搜這些關鍵字確認還在:
`HandleStuckDespawn` / `HandleAccidentBypass` / `ForceFadeOutClear` / `FadeOutAndDestroy` /
`SetYieldForAmbulance` / `CheckPlayerReverseImmediate` / `CheckCloseReversingCar` /
`ExecuteChainReverseFromCar` / `ExecuteChainReverseFromPlayer` / `AccidentDirector` /
`followStopDistance` / `alignSmoothSpeed` / `isInIntersectionZone` /
`IsYieldingForAmbulanceNow` / `IsPulledOverForAmbulanceNow` / `IsBypassingAccident`
