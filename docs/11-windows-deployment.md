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
- Menu chuột phải **"Gửi bằng Shorekeeper…"** cho file và thư mục, cho mọi người dùng (Windows 11: trong "Show more options"; ADR-012). Gỡ cài đặt thì menu mất theo.
- Toast: app tự đăng ký tên + icon dưới `HKCU\Software\Classes\AppUserModelId\Shorekeeper` lần đầu cần hiện thông báo. Người dùng tắt/bật trong Settings → System → Notifications.

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
- **Không** mở cho Public. Mạng hiện tại là Public mà không rule nào cho Shorekeeper vào trên Public thì Chẩn đoán báo lỗi (đỏ nếu chỉ có mạng Public, vàng nếu còn mạng khác) và hướng dẫn. Người dùng đã cho phép Public (hộp thoại firewall của Windows tạo thêm rule Public) thì báo xanh. App **không tự đổi** cài đặt hệ thống.
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
| `AuditLog` | dword | `1` = ghi nhật ký kiểm tra vào Windows Event Log (§9) | ✅ |

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
- Phát hành: push tag `v<Version>` thì workflow `.github/workflows/release.yml` build MSI + APK và tạo GitHub Release (cách làm: [14 A6](14-android.md)).
- Cập nhật: phát hành MSI mới qua GPO/Intune (WiX Major Upgrade). App báo khi thấy peer dùng bản mới hơn: *"Có phiên bản mới 0.3.0 trong mạng"*.

## 8. Tương thích bảo mật doanh nghiệp

| Hệ thống | Lưu ý |
|---|---|
| Antivirus / EDR | Whitelist theo chữ ký nếu bị cảnh báo khi nghe cổng / ghi file lớn |
| AppLocker / WDAC | Rule theo publisher |
| Proxy | `HttpClient` tắt proxy hệ thống (`UseProxy = false`), mọi kết nối đều trong LAN |
| IDS/IPS | Subnet Probe tắt mặc định, có giới hạn tốc độ |
| DLP | `AuditLog` ghi ai gửi gì cho ai (tên, size, SHA-256), xem §9 |

## 9. Nhật ký kiểm tra (audit log)

Cho bộ phận bảo mật (Q7, 2026-10-23). **Tắt mặc định**; IT bật bằng chính sách `AuditLog` = `1`, người dùng không tắt được. Mọi phiên người dùng trên máy đều ghi.

- **Ghi ở đâu:** Windows Event Log, log **Application**, nguồn **`Shorekeeper`**. Người dùng thường không xóa được log này, và IT gom về một chỗ như mọi sự kiện khác (Windows Event Forwarding, Splunk, Wazuh…). Đăng ký nguồn cần quyền admin nên MSI làm (§2); bản không cài bằng MSI không ghi được, app chỉ ghi lỗi vào log của mình.
- **Mỗi sự kiện** là một dòng JSON: `event`, `at` (UTC), `user` (tài khoản Windows `DOMAIN\user`), `device` (DeviceId máy này), rồi chi tiết. Người được nhắc tới ghi `{ id, name }` (DeviceId và tên: tên liên hệ, tên trong nhóm hoặc tên nó quảng bá); nhóm ghi `{ id, name }`.
- **Không bao giờ ghi nội dung tin nhắn**, kể cả chữ gửi bằng "Gửi văn bản" (chỉ ghi `text: true`).
- Android không có GPO nên không ghi.

| Event ID | `event` | Máy ghi | Chi tiết |
|---|---|---|---|
| 1001 | `OfferSent` | Người gửi | `offer`, `group`, `to` (danh sách người nhận), `files`, `bytes`, `text`, `list` (tối đa 100 file: `path`, `size`) |
| 1002 | `FileSent` | Người gửi | Người nhận đã đọc hết một file: `offer`, `group`, `to`, `file`, `size`, `sha256` |
| 2001 | `OfferReceived` | Người nhận | `offer`, `group`, `from`, `files`, `bytes`, `text` |
| 2002 | `FileReceived` | Người nhận | Đã tải xong và khớp checksum: `offer`, `group`, `from`, `file`, `savedTo`, `size`, `sha256` |
| 2003 | `OfferDeclined` | Người nhận | Từ chối, hoặc xóa khỏi Hộp nhận khi chưa trả lời: `offer`, `from` |
| 3001 | `ContactAdded` | Mỗi bên | Kết nối xong (hoặc khôi phục từ bản sao lưu): `peer`, `host` |
| 3002 | `ContactBlocked` | Người chặn | `peer`, `host` |
| 3003 | `ContactRemoved` | Người ngắt | Ngắt kết nối / bỏ chặn: `peer`, `was` (`contact` / `blocked`) |
| 4001 | `GroupJoined` | Mỗi máy | Tạo nhóm hoặc được cho vào: `group`, `role` (`host` / `member`), `host`, `members` |
| 4002 | `GroupLeft` | Mỗi máy | Rời, bị loại, hoặc nhóm đóng: `group` |
| 4003 | `GroupMemberAdded` | Mỗi thành viên | Ai đó vào nhóm: `group`, `member` |
| 4004 | `GroupMemberRemoved` | Mỗi thành viên | Ai đó rời hoặc bị loại: `group`, `member` |

Một file đi qua mạng nên có ở **cả hai máy**: `FileSent` bên gửi, `FileReceived` bên nhận, cùng `offer` và `sha256`. Chỉ thấy một bên thì máy kia không bật nhật ký, hoặc không cài bằng MSI.

Thử thật (2026-10-23, PC dev, nguồn đăng ký bằng `New-EventLog`, `AuditLog` = 1 ở HKLM): gửi một file từ PC sang OPPO bằng **Gửi nhanh** → Application log có 1001 `OfferSent` ngay khi gửi; điện thoại bấm Tải → 1002 `FileSent` với `sha256` đúng của file. Nội dung hiện đủ trong Event Viewer / `Get-WinEvent`.

Ví dụ (2002):

```json
{"event":"FileReceived","at":"2026-10-23T03:15:42.118Z","user":"CORP\\mai.tran","device":"k7q2ab…","offer":"5f0c…","group":null,"from":{"id":"m3xadf…","name":"Huy Lê"},"file":"Báo cáo/Q3.xlsx","savedTo":"D:\\Nhan\\Báo cáo\\Q3.xlsx","size":482113,"sha256":"9b1d…"}
```
