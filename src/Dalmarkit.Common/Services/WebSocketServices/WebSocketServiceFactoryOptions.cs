using System.ComponentModel.DataAnnotations;

namespace Dalmarkit.Common.Services.WebSocketServices;

public class WebSocketServiceFactoryOptions
{
    public const string SectionName = "WebSocketServiceFactoryOptions";

    /// <summary>
    /// Bounds downstream resource exposure: socket FDs, memory, exchange-side connection budgets
    /// </summary>
    public const int MaxConcurrentUsersLimit = 2000;

    [Range(1, MaxConcurrentUsersLimit)]
    public int MaxConcurrentUsers { get; set; } = 1000;
}
