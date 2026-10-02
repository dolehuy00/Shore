using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
                ? new DiagnosticItem("Multicast / probe", $"Đang thấy trực tiếp {seen} máy · UDP {settings.Current.DiscoveryPort}", DiagnosticLevel.Ok)
                : new DiagnosticItem("Multicast / probe", $"Chưa nhận được tín hiệu từ máy nào · UDP {settings.Current.DiscoveryPort}", DiagnosticLevel.Info));
            Items.Add(api.Port > 0
                ? new DiagnosticItem("HTTPS (kết nối)", $"Đang nghe TCP {api.Port}", DiagnosticLevel.Ok)
                : new DiagnosticItem("HTTPS (kết nối)", "Không mở được cổng: máy khác sẽ không kết nối được tới bạn", DiagnosticLevel.Error));
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
            Items.Add(new DiagnosticItem("Làm cầu nối", $"Đang bật · {bridgeRegistry.Count} máy đăng ký", DiagnosticLevel.Ok));
        }

        IReadOnlyList<BridgeStatus> statuses = bridges.GetStatus();
        foreach (BridgeStatus bridge in statuses)
        {
            Items.Add(bridge switch
            {
                { IsConnected: true } => new DiagnosticItem("Cầu nối", $"{bridge.Name} ({bridge.Address}) · thấy {bridge.PeerCount} máy", DiagnosticLevel.Ok),
                { Error: { } error } => new DiagnosticItem("Cầu nối", $"{bridge.Name} ({bridge.Address}): {error}", DiagnosticLevel.Warning),
                _ => new DiagnosticItem("Cầu nối", $"{bridge.Name} ({bridge.Address}) · đang kết nối…", DiagnosticLevel.Info),
            });
        }

        if (statuses.Count == 0 && !bridgeRegistry.IsEnabled)
        {
            Items.Add(new DiagnosticItem("Cầu nối", "Chưa có. Máy ở subnet khác chỉ thấy nhau qua người đã kết nối.", DiagnosticLevel.Info));
        }

        if (settings.Current.ProbeSubnets.Count > 0)
        {
            Items.Add(new DiagnosticItem("Dò dải IP", string.Join(", ", settings.Current.ProbeSubnets), DiagnosticLevel.Info));
        }
    }

    private void AddInterfaces(IReadOnlyList<LocalInterface> interfaces)
    {
        if (!interfaces.Any(i => i.IsUsable))
        {
            Items.Add(new DiagnosticItem("Card mạng", "Không có card mạng nào dùng được", DiagnosticLevel.Error));
        }

        foreach (LocalInterface nic in interfaces)
        {
            Items.Add(nic.IsUsable
                ? new DiagnosticItem(nic.Name, $"{nic.Address}/{nic.PrefixLength} · đang dùng", DiagnosticLevel.Ok)
                : new DiagnosticItem(nic.Name, $"{nic.Address} · bỏ qua: {nic.ExcludedReason}", DiagnosticLevel.Info));
        }
    }

    private void AddFirewall(FirewallStatus? status)
    {
        if (status is null)
        {
            Items.Add(new DiagnosticItem("Firewall", "Không đọc được trạng thái", DiagnosticLevel.Info));
            return;
        }

        Items.Add(status.ActiveProfiles.HasFlag(NetworkProfiles.Public)
            ? new DiagnosticItem("Loại mạng Windows", "Public: Windows chặn kết nối từ máy khác. Hãy chuyển mạng sang Private.", DiagnosticLevel.Error)
            : new DiagnosticItem("Loại mạng Windows", status.ActiveProfiles.ToString(), DiagnosticLevel.Ok));

        Items.Add(status switch
        {
            { FirewallEnabled: false } => new DiagnosticItem("Firewall", "Đang tắt", DiagnosticLevel.Ok),
            { HasBlockRule: true } => new DiagnosticItem("Firewall", "Đang CHẶN Shorekeeper. Mở Windows Security → Firewall để cho phép.", DiagnosticLevel.Error),
            { HasAllowRule: true } => new DiagnosticItem("Firewall", "Đã cho phép Shorekeeper", DiagnosticLevel.Ok),
            _ => new DiagnosticItem("Firewall", "Chưa cho phép Shorekeeper: máy khác sẽ không thấy bạn.", DiagnosticLevel.Warning),
        });
    }
}
