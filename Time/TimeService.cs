namespace KeelAssistant.Time;

/// <summary>
/// 取代 life-assistant 的 get_local_time.py：提供當下精準時間、星期與時段判斷。
/// 時段邏輯與原 Python 腳本一致。
/// </summary>
public static class TimeService
{
    private static readonly string[] WeekdayMap =
        { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日" };

    /// <summary>時段判斷：清晨 / 上午 / 中午 / 下午 / 傍晚 / 深夜。</summary>
    public static string GetPeriod(int hour) => hour switch
    {
        >= 5 and < 9 => "清晨",
        >= 9 and < 12 => "上午",
        >= 12 and < 14 => "中午",
        >= 14 and < 18 => "下午",
        >= 18 and < 22 => "傍晚",
        _ => "深夜"
    };

    /// <summary>取得目前時間資訊，欄位與 get_local_time.py 的 JSON 輸出對齊。</summary>
    public static object Now()
    {
        var now = DateTime.Now;
        return new
        {
            full_datetime = now.ToString("yyyy\\/MM\\/dd HH:mm:ss"),
            date = now.ToString("yyyy\\/MM\\/dd"),
            time = now.ToString("HH:mm"),
            weekday = WeekdayMap[(int)now.DayOfWeek == 0 ? 6 : (int)now.DayOfWeek - 1],
            period = GetPeriod(now.Hour),
            hour = now.Hour
        };
    }
}
