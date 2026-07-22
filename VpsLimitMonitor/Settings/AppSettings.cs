namespace VpsLimitMonitor.Settings;

public class AccountConfig
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "WhmcsCubeCloud";
    public string BaseUrl { get; set; } = "";
}

public class AppSettings
{
    public List<AccountConfig> Accounts { get; set; } = [];
    public int PollIntervalMinutes { get; set; } = 30;
    public double AlertRemainingPercent { get; set; } = 10;
    public string Theme { get; set; } = "System"; // System | Light | Dark
    public string UpdateCheckInterval { get; set; } = "Daily"; // Every6Hours | Daily | Weekly | None
}
