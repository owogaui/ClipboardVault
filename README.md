# ClipboardVault 剪貼簿備份工具

Windows 背景工具，自動把新複製的文字、圖片與檔案存到你選擇的本機資料夾。

## 下載

[Windows x64 預覽版（內含 .NET 執行環境）](https://github.com/owogaui/ClipboardVault/releases/tag/v0.1.0-preview)

## 使用

1. 解壓縮 Windows 壓縮檔，保持所有檔案放在一起。
2. 執行 `ClipboardVault.exe`，選擇儲存位置。
3. 之後的複製操作會自動備份。右下角系統匣圖示按右鍵，可暫停／恢復、開啟或更換資料夾、設定開機自動啟動及結束。
4. 若找不到圖示，查看工作列的隱藏圖示區。

開機自動啟動預設關閉。移動程式後請重新設定自動啟動。結束前會完成已排入的備份。

## 100 MB 硬性限制

- 每次複製的內容總和必須 **小於 100,000,000 bytes**（十進位 100 MB）。等於或超過都略過。
- 多個檔案加總計算，整批成功或整批略過，不留下半筆正式備份。
- 文字按 UTF-8 編碼大小計算；圖片按輸出 PNG 大小計算。
- 檔案讀取前與寫入時都檢查大小；來源被其他程式寫入而無法穩定讀取時會略過。
- 每筆另外附有少量 `record.json` 中繼資料，限制指複製內容大小。
- 這不是整個備份資料夾的累積上限。沒有自動刪除，請定期整理。

## 保存格式

`選擇的資料夾/yyyy-MM-dd/HHmmss_fff_唯一編號/`

每筆含 `text.txt`、`image.png` 或編號後的原始檔名，以及時間、內容類型、大小記錄。先寫入 `.pending_` 暫存資料夾，全部成功後才改為正式名稱。程式異常終止時可能留下 `.pending_` 資料夾，可在程式結束後手動檢查及清除。

連續相同內容會去重（在同一次啟動期間）；檔案以實際內容與檔名比較。去重須讀取檔案，可能短暫使用額外磁碟空間。

## 支援範圍與隱私

- 支援一般 Unicode 文字、Windows 剪貼簿圖片與一般檔案清單。
- 不支援資料夾、檔案連結、HTML／RTF 格式保留，或只有虛擬檔案格式的應用程式內容。
- 同一筆剪貼簿含多種格式時，優先順序：檔案、圖片、文字。
- 這是明文備份，可能包含密碼、驗證碼及個人資料。需要時先暫停；不會判斷或遮蔽敏感內容。
- 不會上傳剪貼簿內容，沒有網路傳輸程式碼。
- Windows 剪貼簿只保留目前內容。極快速連續複製、剪貼簿被占用，或背景佇列已滿時，可能略過。忙碌、超限與失敗會以系統匣通知提示，通知顯示也受 Windows 通知設定影響。
- 圖片轉碼會短暫在主執行緒執行，超大圖片可能造成延遲；100 MB 指儲存內容，並非記憶體上限。
- 設定位於 `%LOCALAPPDATA%/ClipboardVault/settings.json`；不會提交到 Git。

## 建置與測試

本機預覽版使用 .NET 6 Windows Desktop Runtime（x64），此環境已有安裝。正式 GitHub 發布工作流程使用 .NET 8，產生內含執行階段的 Windows x64 版本。

```powershell
dotnet build src/ClipboardVault.csproj -c Release
& ./src/bin/Release/net8.0-windows/ClipboardVault.exe --self-test
Get-Content ./src/bin/Release/net8.0-windows/self-test-result.txt
```

自我測試不讀取或修改真實剪貼簿。涵蓋 100 MB 邊界、整批累計限制及寫入前拒絕。

## GitHub 發布

專案已附 CI 與發布工作流程。第一次 main 分支建置成功後建立 v0.1.0-preview；之後推送新的 `v*` 標籤會建立對應 Release，附上 Windows x64 壓縮檔。正式發布前仍應在 Windows 上人工測試文字、圖片、多檔案、暫停、資料夾切換與開機啟動。

備份資料請放在專案以外的資料夾。只提交來源碼與文件。不要把個人剪貼簿內容加入 GitHub。


