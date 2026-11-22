namespace SideScreenHUD.Models;

public sealed class DonateHealthResult
{
    public bool Ok { get; set; }
    public bool Configured { get; set; }
    public string MerchantName { get; set; } = "星澪科技";
    public List<string> Missing { get; set; } = new();
    public string ErrorMessage { get; set; } = "";
}

public sealed class DonateOrderResult
{
    public bool Ok { get; set; }
    public string OrderId { get; set; } = "";
    public int AmountFen { get; set; }
    public string CodeUrl { get; set; } = "";
    public DateTimeOffset? ExpireAt { get; set; }
    public string MerchantName { get; set; } = "星澪科技";
    public string ErrorMessage { get; set; } = "";
}

public sealed class DonateStatusResult
{
    public bool Ok { get; set; }
    public string OrderId { get; set; } = "";
    public string TradeState { get; set; } = "NOTPAY"; // NOTPAY | USERPAYING | SUCCESS | CLOSED | PAYERROR
    public string TradeStateDesc { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string SuccessTime { get; set; } = "";
    public int AmountFen { get; set; }
    public string ErrorMessage { get; set; } = "";
}
