using System.Globalization;
using System.Runtime.InteropServices;

namespace KeelAssistant.Calendar;

/// <summary>
/// 本機桌面 Outlook 行事曆存取（COM late-binding，免裝 Office PIA、不綁 Outlook 版本）。
/// 僅提供「查」與「塞」兩項。所有呼叫須在 STA 執行緒（透過 HttpApiServer.RunOnUi）。
/// COM 物件用完一律 Marshal.FinalReleaseComObject 釋放，避免殘留 OUTLOOK.EXE 程序。
/// </summary>
public sealed class CalendarService
{
    // Outlook COM 列舉常數（late-binding 無法用具名列舉，於此集中定義）
    private const int OlFolderCalendar = 9;     // OlDefaultFolders.olFolderCalendar
    private const int OlAppointmentItem = 1;    // OlItemType.olAppointmentItem

    // Outlook Restrict 篩選字串需用美式日期格式，固定 InvariantCulture 避免本機地區設定干擾
    private const string RestrictDateFormat = "MM/dd/yyyy hh:mm tt";

    /// <summary>查詢「指定某一天」（00:00–23:59:59）的行事曆項目，依開始時間排序。</summary>
    public List<CalendarEvent> ListDay(DateTime day)
        => List(day.Date, day.Date.AddDays(1).AddTicks(-1));

    /// <summary>查詢指定區間（含邊界）的行事曆項目，依開始時間排序。</summary>
    public List<CalendarEvent> List(DateTime from, DateTime to)
    {
        dynamic app = GetOutlookApp();
        dynamic? ns = null, calendar = null, items = null, restricted = null;
        var result = new List<CalendarEvent>();
        try
        {
            ns = app.GetNamespace("MAPI");
            calendar = ns.GetDefaultFolder(OlFolderCalendar);
            items = calendar.Items;
            items.IncludeRecurrences = true;   // 展開週期性會議
            items.Sort("[Start]");

            var filter = $"[Start] >= '{from.ToString(RestrictDateFormat, CultureInfo.InvariantCulture)}'"
                       + $" AND [Start] <= '{to.ToString(RestrictDateFormat, CultureInfo.InvariantCulture)}'";
            restricted = items.Restrict(filter);

            // COM 的 IEnumerable 僅能透過 C# foreach 迭代（內部走 IEnumVARIANT），
            // 不可直接呼叫 GetEnumerator()；foreach 結束時會自動釋放列舉器。
            foreach (dynamic item in restricted)
            {
                try
                {
                    result.Add(new CalendarEvent
                    {
                        Id = item.EntryID,
                        Subject = ((string?)item.Subject)?.Trim() ?? "",
                        Start = (DateTime)item.Start,
                        End = (DateTime)item.End,
                        Location = ((string?)item.Location)?.Trim() ?? "",
                        IsAllDay = item.AllDayEvent
                    });
                }
                finally { Release(item); }
            }
            return result;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"讀取 Outlook 行事曆失敗：{ex.Message}", ex);
        }
        finally
        {
            Release(restricted);
            Release(items);
            Release(calendar);
            Release(ns);
            Release(app);
        }
    }

    /// <summary>新增一筆行程，回傳其 EntryID。reminder 為 null 時沿用 Outlook 預設提醒。</summary>
    public string Add(string subject, DateTime start, DateTime end, string? location, string? body, int? reminder)
    {
        dynamic app = GetOutlookApp();
        dynamic? appt = null;
        try
        {
            appt = app.CreateItem(OlAppointmentItem);
            appt.Subject = subject.Trim();
            appt.Start = start;
            appt.End = end;
            if (!string.IsNullOrWhiteSpace(location)) appt.Location = location.Trim();
            if (!string.IsNullOrWhiteSpace(body)) appt.Body = body.Trim();
            if (reminder is > 0)
            {
                appt.ReminderSet = true;
                appt.ReminderMinutesBeforeStart = reminder.Value;
            }
            appt.Save();
            return appt.EntryID;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"新增 Outlook 行程失敗：{ex.Message}", ex);
        }
        finally
        {
            Release(appt);
            Release(app);
        }
    }

    /// <summary>取得 Outlook.Application COM 物件（若已開啟則回傳現有實例，single-instance server）。</summary>
    private static dynamic GetOutlookApp()
    {
        Type? t = Type.GetTypeFromProgID("Outlook.Application");
        if (t is null)
            throw new InvalidOperationException("找不到 Outlook，請確認本機已安裝並可開啟桌面版 Outlook。");
        return Activator.CreateInstance(t)
            ?? throw new InvalidOperationException("無法啟動 Outlook，請確認 Outlook 可正常開啟。");
    }

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            try { Marshal.FinalReleaseComObject(comObject); } catch { }
        }
    }
}

/// <summary>
/// 行事曆項目。Start/End 保留原生 DateTime（供同步鬧鐘回推「開始前 N 分鐘」用），
/// 另提供 StartText/EndText 以統一日期格式 yyyy/MM/dd HH:mm 對外顯示。
/// </summary>
public sealed class CalendarEvent
{
    public string Id { get; set; } = "";
    public string Subject { get; set; } = "";
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Location { get; set; } = "";
    public bool IsAllDay { get; set; }

    public string StartText => Start.ToString("yyyy\\/MM\\/dd HH:mm");
    public string EndText => End.ToString("yyyy\\/MM\\/dd HH:mm");
}
