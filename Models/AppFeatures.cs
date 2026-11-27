namespace SideScreenHUD.Models;

/// <summary>
/// Feature switches for surfaces that are temporarily hidden from the product UI.
/// The implementation stays in the codebase; flipping a flag restores the tab/menu without rewrites.
/// Declared as static readonly (not const) so the surrounding code paths still compile without
/// "unreachable code" warnings.
/// </summary>
public static class AppFeatures
{
    /// <summary>赞助支持（微信支付）面板与托盘入口。暂时隐藏，代码保留。</summary>
    public static readonly bool DonationEnabled = false;

    /// <summary>行情 / K 线 / 汇率换算：监控窗口、管理面板与托盘入口。暂时隐藏，代码保留。</summary>
    public static readonly bool MarketEnabled = false;
}
