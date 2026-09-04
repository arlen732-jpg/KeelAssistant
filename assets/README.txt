把鬧鈴音檔放在這個資料夾，預設檔名為 alarm.wav（見 appsettings.json 的 AlarmSoundPath）。
格式建議：.wav（System.Media.SoundPlayer 僅支援 wav；mp3 不支援）。
若此檔不存在，鬧鈴會退回 Windows 系統音。
