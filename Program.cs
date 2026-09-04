namespace KeelAssistant;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // 單一執行個體防護（避免多開）
        using var mutex = new Mutex(true, "KeelAssistant.SingleInstance", out bool isNew);
        if (!isNew) return;

        ApplicationConfiguration.Initialize();
        // 2026/06/25 - Arlen - 修「結束無法關閉、背景殘留」：Application.Run(ApplicationContext) 不會 Dispose context，
        // 用 using block 確保結束時呼叫 Dispose()，停掉 Kestrel/Timer/WebView2 並移除托盤圖示 - Start
        using (var context = new TrayApplicationContext())
        {
            Application.Run(context);
        }
        // 保證行程終結：即使 Kestrel(MCP) / WebView2 仍有殘留前景執行緒，也強制乾淨退出（含 msedgewebview2 子行程）
        Environment.Exit(0);
        // 2026/06/25 - Arlen - 修「結束無法關閉、背景殘留」- end
    }
}
