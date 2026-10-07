# Vendored ECommons

Upstream: https://github.com/NightmareXIV/ECommons  
Commit: 9ef3961c329fa99bd4c65769b39a56cbe2d2917a  
Version: 3.2.1.22

One local compatibility change: `ExcelServices/Sheets/QuestDialogueText.cs` explicitly aliases `ExcelPage` to `Lumina.Excel.ExcelPage` to avoid a name collision with the bundled FFXIVClientStructs type. The complete upstream license is included as `LICENSE.md`.

The build script passes the selected Dalamud development-library directory to both projects; no external MoodlesPlus checkout is required.
