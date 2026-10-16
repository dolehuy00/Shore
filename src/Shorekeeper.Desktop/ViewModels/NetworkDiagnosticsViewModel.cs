using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Localization;
using Shorekeeper.Core.Platform;
using Shorekeeper.Engine.Api;
using Shorekeeper.Engine.Discovery;
using Shorekeeper.Engine.Settings;

namespace Shorekeeper.Desktop.ViewModels;

public enum DiagnosticLevel
{
    Ok,
    Info,
    Warning,
    Error,
}

public sealed record DiagnosticItem(string Label, string Value, DiagnosticLevel Level)
{
    public bool IsOk => Level == DiagnosticLevel.Ok;

    public bool IsWarning => Level == DiagnosticLevel.Warning;

    public bool IsError => Level == DiagnosticLevel.Error;
}

/// <summary>"Cài đặt → Mạng": explains why peers may not be visible (docs/10-ux.md §8).</summary>
public sealed partial class NetworkDiagnosticsViewModel(
    IFirewallInspector firewall,
    PeerDirectory directory,
    SettingsService settings,
    ApiServer api,
    BridgeRegistry bridgeRegistry,
    BridgeClient bridges) : ObservableObject
{
    public ObservableCollection<DiagnosticItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            // Enumerating firewall rules through COM takes a moment; keep the UI responsive.
            (IReadOnlyList<LocalInterface> interfaces, FirewallStatus? firewallStatus) = await Task.Run(() =>
                (LocalInterfaces.GetAll(), firewall.Inspect(Environment.ProcessPath ?? "")));

            Items.Clear();
            AddInterfaces(interfaces);
            AddFirewall(firewallStatus);

            int seen = directory.Snapshot().Count(p => p.Source == PeerSource.Direct);
            Items.Add(seen > 0
                ? new DiagnosticItem("Multicast / probe", L.F("Đang thấy trực tiếp {0} máy · UDP {1}", seen, settings.Current.DiscoveryPort), DiagnosticLevel.Ok)
                : new DiagnosticItem("Multicast / probe", L.F("Chưa nhận được tín hiệu từ máy nào · UDP {0}", settings.Current.DiscoveryPort), DiagnosticLevel.Info));
            Items.Add(api.Port > 0
                ? new DiagnosticItem(L.T("HTTPS (kết nối)"), L.F("Đang nghe TCP {0}", api.Port), DiagnosticLevel.Ok)
                : new DiagnosticItem(L.T("HTTPS (kết nối)"), L.T("Không mở được cổng: máy khác sẽ không kết nối được tới bạn"), DiagnosticLevel.Error));
            AddBridges();
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void AddBridges()
    {
        if (bridgeRegistry.IsEnabled)
        {
            Items.Add(new DiagnosticItem(L.T("Làm cầu nối"), L.F("Đang bật · {0} máy đăng ký", bridgeRegistry.Count), DiagnosticLevel.Ok));
        }

        IReadOnlyList<BridgeStatus> statuses = bridges.GetStatus();
        foreach (BridgeStatus bridge in statuses)
        {
            Items.Add(bridge switch
            {
                { IsConnected: true } => new DiagnosticItem(L.T("Cầu nối"), L.F("{0} ({1}) · thấy {2} máy", bridge.Name, bridge.Address, bridge.PeerCount), DiagnosticLevel.Ok),
                { Error: { } error } => new DiagnosticItem(L.T("Cầu nối"), $"{bridge.Name} ({bridge.Address}): {error}", DiagnosticLevel.Warning),
                _ => new DiagnosticItem(L.T("Cầu nối"), L.F("{0} ({1}) · đang kết nối…", bridge.Name, bridge.Address), DiagnosticLevel.Info),
            });
        }

        if (statuses.Count == 0 && !bridgeRegistry.IsEnabled)
        {
            Items.Add(new DiagnosticItem(L.T("Cầu nối"), L.T("Chưa có. Máy ở subnet khác chỉ thấy nhau qua người đã kết nối."), DiagnosticLevel.Info));
        }

        if (settings.Current.ProbeSubnets.Count > 0)
        {
            Items.Add(new DiagnosticItem(L.T("Dò dải IP"), string.Join(", ", settings.Current.ProbeSubnets), DiagnosticLevel.Info));
        }
    }

    private void AddInterfaces(IReadOnlyList<LocalInterface> interfaces)
    {
        if (!interfaces.Any(i => i.IsUsable))
        {
            Items.Add(new DiagnosticItem(L.T("Card mạng"), L.T("Không có card mạng nào dùng được"), DiagnosticLevel.Error));
        }

        foreach (LocalInterface nic in interfaces)
        {
            Items.Add(nic.IsUsable
                ? new DiagnosticItem(nic.Name, L.F("{0}/{1} · đang dùng", nic.Address, nic.PrefixLength), DiagnosticLevel.Ok)
                : new DiagnosticItem(nic.Name, L.F("{0} · bỏ qua: {1}", nic.Address, nic.ExcludedReason), DiagnosticLevel.Info));
        }
    }

    private void AddFirewall(FirewallStatus? status)
    {
        if (status is null)
        {
            Items.Add(new DiagnosticItem("Firewall", L.T("Không đọc được trạng thái"), DiagnosticLevel.Info));
            return;
        }

        // Public only matters when nothing lets Shorekeeper in there (the MSI opens Domain/Private only; the Windows
        // prompt can add a Public rule).
        bool onPublic = status.ActiveProfiles.HasFlag(NetworkProfiles.Public);
        bool openOnPublic = !status.FirewallEnabled || (status.AllowedProfiles.HasFlag(NetworkProfiles.Public) && !status.HasBlockRule);
        Items.Add(!onPublic
            ? new DiagnosticItem(L.T("Loại mạng Windows"), status.ActiveProfiles.ToString(), DiagnosticLevel.Ok)
            : openOnPublic
                ? new DiagnosticItem(L.T("Loại mạng Windows"), L.F("{0} · Shorekeeper được phép nhận kết nối trên mạng Public", status.ActiveProfiles), DiagnosticLevel.Ok)
                : new DiagnosticItem(
                    L.T("Loại mạng Windows"),
                    L.T("Public: Windows chặn máy khác kết nối tới Shorekeeper trên mạng này. Chuyển mạng sang Private, hoặc cho phép Shorekeeper trên mạng Public trong Windows Security → Firewall."),
                    status.ActiveProfiles == NetworkProfiles.Public ? DiagnosticLevel.Error : DiagnosticLevel.Warning));

        Items.Add(status switch
        {
            { FirewallEnabled: false } => new DiagnosticItem("Firewall", L.T("Đang tắt"), DiagnosticLevel.Ok),
            { HasBlockRule: true } => new DiagnosticItem("Firewall", L.T("Đang CHẶN Shorekeeper. Mở Windows Security → Firewall để cho phép."), DiagnosticLevel.Error),
            { HasAllowRule: true } => new DiagnosticItem("Firewall", L.T("Đã cho phép Shorekeeper"), DiagnosticLevel.Ok),
            _ => new DiagnosticItem("Firewall", L.T("Chưa cho phép Shorekeeper: máy khác sẽ không thấy bạn."), DiagnosticLevel.Warning),
        });
    }
}
