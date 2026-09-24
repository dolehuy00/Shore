# 11 — Triển khai trên Windows

## 1. Yêu cầu hệ thống

| Hạng mục | Tối thiểu |
|---|---|
| OS | Windows 10 22H2 / Windows 11, x64 |
| Runtime | Self-contained .NET 10, không cần cài .NET |
| Ổ đĩa | ~150 MB |
| Mạng | IPv4, profile **Domain** hoặc **Private** |

## 2. Gói cài đặt

Một gói duy nhất: **`Shorekeeper.msi`** (WiX), per-machine. Cần admin để tạo rule firewall.

```bash
msiexec /i Shorekeeper.msi /qn AUTOSTART=1 BRIDGE_ADDRESSES="PC-BUILD-01" DOWNLOAD_DIR="D:\Shorekeeper"
```

| Thuộc tính | Mặc định | Ý nghĩa |
|---|---|---|
| `AUTOSTART` | 1 | Khởi động cùng Windows |
| `DOWNLOAD_DIR` | (trống = Downloads\Shorekeeper) | Thư mục nhận |
| `BRIDGE_ADDRESSES` | (trống) | Bridge biết trước (nếu IT chỉ định) |
| `FIREWALL_RULES` | 1 | Tạo rule firewall |

Có bản **per-user** (không cần admin) cho máy không có quyền. Khi đó Windows sẽ tự hỏi "Allow access" lần đầu.

## 3. Firewall

Nguyên nhân số 1 của "không thấy nhau".

| Rule | Hướng | Cổng | Chương trình | Profile |
|---|---|---|---|---|
| Shorekeeper Discovery | Inbound | UDP 47470 | `Shorekeeper.exe` | Domain, Private |
| Shorekeeper API | Inbound | TCP (mọi cổng của chương trình) | `Shorekeeper.exe` | Domain, Private |

- Rule gắn theo **đường dẫn chương trình** để cổng động (phiên người dùng thứ 2) vẫn được phép.
- **Không** mở cho Public. Mạng hiện tại là Public thì app cảnh báo và hướng dẫn. App **không tự đổi** cài đặt hệ thống.
- `IFirewallInspector` đọc trạng thái qua COM `INetFwPolicy2` để hiển thị trong màn hình Chẩn đoán.
- IT có thể tự đẩy rule qua GPO (`FIREWALL_RULES=0`).

## 4. Chính sách nhóm (ADMX)

Cung cấp `Shorekeeper.admx` + `vi-VN\Shorekeeper.adml` + `en-US\Shorekeeper.adml`. Registry: `HKLM\Software\Policies\Shorekeeper` (ưu tiên) và `HKCU\...`.

| Chính sách | Kiểu | Mô tả |
|---|---|---|
| `MaxOfferSizeGB` | dword | |
| `MaxOfferLifetimeHours` | dword | Trần thời hạn offer mở |
| `ReconnectWindowSeconds` | dword | Cửa sổ kết nối lại khi rớt mạng |
| `ApplyMarkOfTheWeb` | dword | |
| `DisableGroups` | dword | |
| `AllowBridge` | dword | Cho phép người dùng bật Bridge |
| `EnableBridge` | dword | Bắt buộc bật Bridge trên máy này (IT chỉ định) |
| `BridgeAddresses` | multi-string | Bridge biết trước |
| `ProbeSubnets` | multi-string | Dải CIDR để quét |
| `AllowedSubnets` | multi-string | Chỉ giao tiếp trong các dải này |
| `DownloadDirectory` | string | |
| `AuditLog` | dword | |
| `DiscoveryPort` / `ApiPort` | dword | Đổi cổng (đồng bộ toàn mạng) |

Thiết lập bị chính sách khóa thì hiển thị xám kèm 🔒 *"Do quản trị viên đặt"*.

## 5. Nhiều phiên người dùng trên một máy

- Mỗi phiên (RDP / fast user switching) là một instance riêng với **DeviceId riêng**.
- UDP 47470 bind với `ExclusiveAddressUse = false` để nhiều tiến trình cùng nhận multicast. Unicast chỉ tới một socket, nên các phiên sau bổ sung địa chỉ qua PEX/Bridge.
- TCP: phiên đầu dùng 47471, phiên sau dùng cổng động (quảng bá trong presence).

## 6. Khởi động cùng Windows

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Shorekeeper = "...\Shorekeeper.exe" --background` → vào thẳng tray. Người dùng tắt được (trừ khi GPO khóa).

## 7. Ký số & cập nhật

- Ký **Authenticode** cho exe/dll/msi để tránh SmartScreen và cho phép AppLocker/WDAC whitelist theo publisher.
- Cập nhật: phát hành MSI mới qua GPO/Intune (WiX Major Upgrade). App báo khi thấy peer dùng bản mới hơn: *"Có phiên bản mới 0.3.0 trong mạng"*.

## 8. Tương thích bảo mật doanh nghiệp

| Hệ thống | Lưu ý |
|---|---|
| Antivirus / EDR | Whitelist theo chữ ký nếu bị cảnh báo khi nghe cổng / ghi file lớn |
| AppLocker / WDAC | Rule theo publisher |
| Proxy | `HttpClient` tắt proxy hệ thống (`UseProxy = false`), mọi kết nối đều trong LAN |
| IDS/IPS | Subnet Probe tắt mặc định, có giới hạn tốc độ |
| DLP | `AuditLog` ghi ai gửi gì cho ai (tên, size, SHA-256) |
