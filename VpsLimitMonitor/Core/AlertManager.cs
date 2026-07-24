using JeekTools;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using VpsLimitMonitor.Settings;
using ZLogger;

namespace VpsLimitMonitor.Core;

/// <summary>低流量与会话失效的 Toast 报警。同一状态只报一次，恢复后解除。</summary>
public class AlertManager
{
    private static readonly ILogger Log = LogManager.CreateLogger(nameof(AlertManager));

    /// <summary>到期前多少天开始每天提醒续费。</summary>
    public const int RenewalReminderDays = 7;

    /// <summary>最近报警记录（供状态面板与 MCP 调试接口查询）。</summary>
    public List<string> RecentAlerts { get; } = [];

    public void Evaluate(AccountState account)
    {
        var threshold = SettingsManager.Settings.AlertRemainingPercent;

        foreach (var svc in account.Services)
        {
            EvaluateRenewal(account, svc);

            if (svc.Traffic is not { } traffic)
                continue;

            var pct = traffic.RemainingPercent;
            if (pct < threshold && !svc.Alerted)
            {
                svc.Alerted = true;
                ShowToast(
                    "VPS 流量警报",
                    $"{account.Config.Name} {svc.Service.Label} 剩余流量仅 {traffic.RemainingGB:F1} GB"
                        + $"（{pct:F1}%），低于阈值 {threshold:F0}%"
                );
            }
            else if (pct >= threshold + 5)
            {
                // 留 5% 回滞，避免在阈值附近反复报警
                svc.Alerted = false;
            }
        }
    }

    private void EvaluateRenewal(AccountState account, ServiceState svc)
    {
        if (svc.Service.DueDate is not { } due)
            return;

        var today = DateOnly.FromDateTime(DateTime.Now);
        var days = due.DayNumber - today.DayNumber;
        if (days > RenewalReminderDays || svc.RenewalRemindedOn == today)
            return;

        svc.RenewalRemindedOn = today;
        var when = days switch
        {
            < 0 => $"已于 {due:yyyy-MM-dd} 到期",
            0 => $"今天（{due:yyyy-MM-dd}）到期",
            _ => $"将于 {due:yyyy-MM-dd} 到期（还剩 {days} 天）",
        };
        ShowToast("VPS 续费提醒", $"{account.Config.Name} {svc.Service.Label} {when}，请及时续费");
    }

    public void NotifyLoginNeeded(AccountState account)
    {
        if (account.LoginNotified)
            return;

        account.LoginNotified = true;
        ShowToast(
            "VPS 流量监视",
            $"{account.Config.Name} 登录已失效，请从托盘菜单打开网站重新登录"
        );
    }

    public void ShowToast(string title, string message)
    {
        var record = $"[{DateTime.Now:MM-dd HH:mm:ss}] {title}: {message}";
        RecentAlerts.Add(record);
        if (RecentAlerts.Count > 50)
            RecentAlerts.RemoveAt(0);

        Log.ZLogInformation($"Toast: {title} - {message}");
        try
        {
            new ToastContentBuilder().AddText(title).AddText(message).Show();
        }
        catch (Exception ex)
        {
            Log.ZLogError($"Failed to show toast: {ex.Message}");
        }
    }
}
