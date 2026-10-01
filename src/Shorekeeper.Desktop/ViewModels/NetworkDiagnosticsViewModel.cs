using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Shorekeeper.Core.Platform;
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
public sealed partial class NetworkDiagnosticsViewModel(IFirewallInspector firewall, PeerDirectory directory, SettingsService settings)
    : ObservableObject
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

            int seen = directory.Snapshot().Count;
            Items.Add(seen > 0
                ? new DiagnosticItem("Multicast", $"Đang thấy {seen} máy · UDP {settings.Current.DiscoveryPort}", DiagnosticLevel.Ok)
                : new DiagnosticItem("Multicast", $"Chưa nhận được tín hiệu từ máy nào · UDP {settings.Current.DiscoveryPort}", DiagnosticLevel.Info));
        }
        finally
        {
            IsRefreshing = false;
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
