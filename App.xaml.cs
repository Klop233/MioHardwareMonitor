using System.IO;
using System.Text.Json;
using System.Windows;
using SideScreenHUD.Services;

namespace SideScreenHUD;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private AppController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.Equals("--software-wpf", StringComparison.OrdinalIgnoreCase)))
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        }

        if (e.Args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            await RunSelfTestAsync();
            Shutdown(0);
            return;
        }

        _singleInstanceMutex = new Mutex(true, "SideScreenHUD_SingleInstance_Mutex_v1", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show("SideScreen HUD 已经在运行中（请检查系统托盘或控制窗口）。", "SideScreen HUD", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _controller = new AppController();
        _controller.Start();
    }

    private static async Task RunSelfTestAsync()
    {
        var outPath = Path.Combine(AppContext.BaseDirectory, "self-test-report.json");
        var dm = new DisplayManager();
        var monitors = dm.GetMonitors();
        var directTargets = DirectDisplayService.EnumerateTargets();

        using var hw = new HardwareMonitorService();
        hw.Collect();
        await Task.Delay(1200);
        var telemetry = hw.Collect();

        using var netIp = new NetworkIpService();
        var ipSnap = await netIp.DetectAsync();

        using var updater = new UpdateService();
        var updateSnap = await updater.CheckForUpdatesAsync("");

        using var market = new MarketDataService();
        var symbols = market.GetAllSymbols(null);
        var quotes = await market.RefreshQuotesAsync(symbols, "binance", "futures", "all", true);
        var klineBnFut = await market.FetchKlineAsync(symbols.First(s => s.Id == "BTC"), "15m", "binance", "futures", true);
        var klineOkxSpot = await market.FetchKlineAsync(symbols.First(s => s.Id == "BTC"), "15m", "okx", "spot", true);
        var klineBybitFut = await market.FetchKlineAsync(symbols.First(s => s.Id == "ETH"), "15m", "bybit", "futures", true);
        var klineBitgetFut = await market.FetchKlineAsync(symbols.First(s => s.Id == "SOL"), "15m", "bitget", "futures", true);
        var klineGateFut = await market.FetchKlineAsync(symbols.First(s => s.Id == "BTC"), "15m", "gate", "futures", true);
        var klineCn = await market.FetchKlineAsync(symbols.First(s => s.Id == "CN_600519"), "15m", "binance", "futures", true);
        var klineUs = await market.FetchKlineAsync(symbols.First(s => s.Id == "US_NVDA"), "15m", "binance", "futures", true);
        var klineFx = await market.FetchKlineAsync(symbols.First(s => s.Id == "USDCNH"), "15m", "binance", "futures", true);
        var fxRates = await market.FetchFxSpotRatesAsync();

        var report = new
        {
            monitors,
            directTargets,
            telemetry,
            networkIp = ipSnap,
            updateCheck = updateSnap,
            quotesOk = quotes.Count(q => q.Price > 0),
            quotesTotal = quotes.Count,
            klineBnFut = $"{klineBnFut.Candles.Count} ({klineBnFut.Source}, FR={klineBnFut.FundingRate})",
            klineOkxSpot = $"{klineOkxSpot.Candles.Count} ({klineOkxSpot.Source})",
            klineBybitFut = $"{klineBybitFut.Candles.Count} ({klineBybitFut.Source})",
            klineBitgetFut = $"{klineBitgetFut.Candles.Count} ({klineBitgetFut.Source})",
            klineGateFut = $"{klineGateFut.Candles.Count} ({klineGateFut.Source})",
            klineCn = $"{klineCn.Candles.Count} ({klineCn.Source}, last={klineCn.LastPrice})",
            klineUs = $"{klineUs.Candles.Count} ({klineUs.Source}, last={klineUs.LastPrice})",
            klineFx = $"{klineFx.Candles.Count} ({klineFx.Source}, last={klineFx.LastPrice})",
            fxRatesCount = fxRates.Rates.Count
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        await File.WriteAllTextAsync(outPath, json);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _controller?.Dispose(); } catch { }
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
