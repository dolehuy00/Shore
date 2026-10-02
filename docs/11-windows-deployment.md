# 11 — Triển khai trên Windows

## 1. Yêu cầu hệ thống

| Hạng mục | Tối thiểu |
|---|---|
| OS | Windows 10 22H2 / Windows 11, x64 |
| Runtime | Self-contained .NET 10, không cần cài .NET |
| Ổ đĩa | ~150 MB |
| Mạng | IPv4, profile **Domain** hoặc **Private** |

## 2. Gói cài đặt

Một gói duy nhất: **`Shorekeeper-<version>-x64.msi`** (WiX v5, ADR-006), **per-machine**: cài vào `C:\Program Files\Shorekeeper`, cần admin. Không có bản per-user.

```bash
msiexec /i Shorekeeper-0.1.0-x64.msi /qn AUTOSTART=1 BRIDGE_ADDRESSES="PC-BUILD-01" DOWNLOAD_DIR="D:\Shorekeeper"
```

| Thuộc tính | Mặc định | Ý nghĩa |
|---|---|---|
| `AUTOSTART` | 1 | Khởi động cùng Windows cho mọi người dùng (§6) |
| `FIREWALL_RULES` | 1 | Tạo rule firewall (§3). `0` nếu IT tự đẩy rule |
| `DOWNLOAD_DIR` | (trống = Downloads\Shorekeeper) | Thư mục nhận. Ghi như GPO `DownloadDirectory`: người dùng không đổi được |
| `BRIDGE_ADDRESSES` | (trống) | Bridge biết trước, nhiều máy cách nhau bằng `;`. Ghi như GPO `BridgeAddresses` |

- Thuộc tính **không được nhớ**: khi nâng cấp phải truyền lại (IT thường dùng lại cùng lệnh).
- App chạy theo từng người dùng, dữ liệu ở `%LOCALAPPDATA%\Shorekeeper` ([09](09-data-storage.md)). Gỡ cài đặt không xóa dữ liệu này.
- Nâng cấp / gỡ: Shorekeeper đang chạy (của mọi người dùng trên máy) bị đóng để thay file. Lượt tải đang dở phải tải lại (ADR-007).
- Shortcut trong Start menu cho mọi người dùng.

### 2.1 Build

```bash
powershell -ExecutionPolicy Bypass -File deploy/build-msi.ps1
```

Script `dotnet publish` app self-contained `win-x64` vào `artifacts/publish/win-x64`, rồi đóng gói thư mục đó bằng `deploy/msi/Shorekeeper.Installer.wixproj` thành `artifacts/msi/`. Mọi file trong thư mục publish đều vào MSI. Project installer không nằm trong `Shorekeeper.sln`, vì nó cần bước publish chạy trước.

## 3. Firewall

Nguyên nhân số 1 của "không thấy nhau".

| Rule | Hướng | Giao thức / cổng | Chương trình | Profile |
|---|---|---|---|---|
| Shorekeeper (Domain) | Inbound | Mọi giao thức, mọi cổng | `C:\Program Files\Shorekeeper\Shorekeeper.exe` | Domain |
| Shorekeeper (Private) | Inbound | Mọi giao thức, mọi cổng | như trên | Private |

- Rule gắn theo **đường dẫn chương trình**, không theo cổng: cổng động (phiên người dùng thứ 2) và cổng đổi qua GPO vẫn được phép. Một rule cho mỗi profile vì WiX chỉ nhận một profile mỗi rule.
- **Không** mở cho Public. Mạng hiện tại là Public thì app cảnh báo và hướng dẫn. App **không tự đổi** cài đặt hệ thống.
- `IFirewallInspector` đọc trạng thái qua COM `INetFwPolicy2` để hiển thị trong màn hình Chẩn đoán.
- IT có thể tự đẩy rule qua GPO (`FIREWALL_RULES=0`).

## 4. Chính sách nhóm (ADMX)

Template nằm ở `deploy/admx/`: `Shorekeeper.admx` + `en-US\Shorekeeper.adml` + `vi-VN\Shorekeeper.adml`. Chép vào `C:\Windows\PolicyDefinitions` (hoặc central store của domain). Mọi chính sách có ở cả Computer và User Configuration. Registry: `HKLM\Software\Policies\Shorekeeper` (ưu tiên) và `HKCU\...`.

| Chính sách | Kiểu | Mô tả | Trạng thái |
|---|---|---|---|
| `DownloadDirectory` | string (expand) | Thư mục nhận, khóa với người dùng | ✅ |
| `MaxOfferLifetimeHours` | dword 1–720 | Trần thời hạn offer mở | ✅ |
| `ReconnectWindowSeconds` | dword 15–300 | Cửa sổ kết nối lại khi rớt mạng | ✅ |
| `ApplyMarkOfTheWeb` | dword | `0` = không gắn Mark of the Web | ✅ |
| `AllowBridge` | dword | `0` = người dùng không bật được Bridge | ✅ |
| `EnableBridge` | dword | `1` luôn bật / `0` luôn tắt Bridge trên máy này | ✅ |
| `BridgeAddresses` | multi-string | Bridge biết trước | ✅ |
| `ProbeSubnets` | multi-string | Dải CIDR để quét | ✅ |
| `DiscoveryPort` / `ApiPort` | dword | Đổi cổng (đồng bộ toàn mạng); một mục "Cổng mạng" trong ADMX | ✅ |
| `MaxOfferSizeGB` | dword | | Chưa làm, chưa có trong ADMX |
| `DisableGroups` | dword | `1` = tắt nhóm (không tạo/vào, chỉ nhận file từ liên hệ) | ✅ |
| `AllowedSubnets` | multi-string | Chỉ giao tiếp trong các dải này | Chưa làm |
| `AuditLog` | dword | | Chưa làm |

Thiết lập bị chính sách khóa thì hiển thị xám kèm 🔒 *"Do quản trị viên đặt"*.

## 5. Nhiều phiên người dùng trên một máy

- Mỗi phiên (RDP / fast user switching) là một instance riêng với **DeviceId riêng**.
- UDP 47470 bind với `ExclusiveAddressUse = false` để nhiều tiến trình cùng nhận multicast. Unicast chỉ tới một socket, nên các phiên sau bổ sung địa chỉ qua PEX/Bridge.
- TCP: phiên đầu dùng 47471, phiên sau dùng cổng động (quảng bá trong presence).

## 6. Khởi động cùng Windows

MSI ghi `HKLM\Software\Microsoft\Windows\CurrentVersion\Run\Shorekeeper = "C:\Program Files\Shorekeeper\Shorekeeper.exe" --background` (khi `AUTOSTART=1`) → mọi người dùng đăng nhập đều có app chạy thẳng xuống tray. Từng người tắt được trong Task Manager → Startup apps.

## 7. Ký số & cập nhật

- **Hiện chưa ký** (dự án cá nhân mã nguồn mở, chưa có chứng chỉ). Hệ quả: SmartScreen cảnh báo "Unknown publisher" khi mở MSI tải về; AppLocker/WDAC phải whitelist theo hash hoặc đường dẫn thay vì publisher.
- Khi cần: ký **Authenticode** cho exe/dll/msi. Dự án mã nguồn mở có thể xin chứng chỉ miễn phí qua SignPath Foundation; thêm bước ký vào `deploy/build-msi.ps1` (ký file trong thư mục publish trước, rồi ký MSI).
- Cập nhật: phát hành MSI mới qua GPO/Intune (WiX Major Upgrade). App báo khi thấy peer dùng bản mới hơn: *"Có phiên bản mới 0.3.0 trong mạng"*.

## 8. Tương thích bảo mật doanh nghiệp

| Hệ thống | Lưu ý |
|---|---|
| Antivirus / EDR | Whitelist theo chữ ký nếu bị cảnh báo khi nghe cổng / ghi file lớn |
| AppLocker / WDAC | Rule theo publisher |
| Proxy | `HttpClient` tắt proxy hệ thống (`UseProxy = false`), mọi kết nối đều trong LAN |
| IDS/IPS | Subnet Probe tắt mặc định, có giới hạn tốc độ |
| DLP | `AuditLog` ghi ai gửi gì cho ai (tên, size, SHA-256) |
