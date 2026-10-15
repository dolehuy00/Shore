# Shorekeeper

Gửi file ngang hàng (P2P) trong mạng LAN công ty: thấy đồng nghiệp đang online, kéo thả file để gửi, người nhận tự quyết có tải hay không. Không cần server.

Thiết kế chi tiết: [docs/](docs/README.md).

## Yêu cầu

- Windows 10/11 x64
- .NET SDK 10 (xem `global.json`)

## Build, test, chạy

```bash
dotnet build Shorekeeper.sln
```

```bash
dotnet test --solution Shorekeeper.sln
```

Test Engine mặc định chạy với `MiniHttpServer`; đặt biến môi trường `SHOREKEEPER_TEST_SERVER=kestrel` để chạy lại với Kestrel.

```bash
dotnet run --project src/Shorekeeper.Desktop
```

Thêm `-- --background` để khởi động thẳng xuống khay hệ thống, hoặc `-- --send <file>…` để mở cửa sổ Gửi nhanh với các file đó (menu "Gửi bằng Shorekeeper…" của Explorer gọi như vậy).

Bản cho Linux/macOS (đang chuẩn bị, chưa thử giao diện trên máy thật; xem [docs/02-architecture.md §9](docs/02-architecture.md)):

```bash
dotnet publish src/Shorekeeper.Desktop -c Release -r linux-x64 --self-contained
```

Bộ cài MSI (per-machine, cần admin để cài) và template GPO: xem [docs/11-windows-deployment.md](docs/11-windows-deployment.md).

```bash
powershell -ExecutionPolicy Bypass -File deploy/build-msi.ps1
```

## Cấu trúc

| Project | Vai trò |
|---|---|
| `src/Shorekeeper.Core` | Domain, DeviceId, cấu hình, interface platform. Không I/O mạng, không phụ thuộc Windows |
| `src/Shorekeeper.Engine` | Khóa định danh, SQLite + migration, cấu hình, discovery, peer API (`PeerApi` + `MiniHttpServer`), gửi/nhận, nhóm. Không phụ thuộc ASP.NET Core |
| `src/Shorekeeper.Engine.Kestrel` | Peer API trên Kestrel cho desktop |
| `src/Shorekeeper.Platform.Windows` | DPAPI, đọc GPO từ registry, firewall, Mark of the Web, toast, phím tắt toàn cục, Active Directory |
| `src/Shorekeeper.Desktop` | App Avalonia: cửa sổ chính, khay hệ thống, chỉ chạy một instance |
| `tests/*` | xUnit v3 |
| `deploy/msi` | Bộ cài WiX v5 (không nằm trong solution) |
| `deploy/admx` | Template Group Policy (ADMX/ADML) |

## Dữ liệu trên máy

| Đường dẫn | Nội dung |
|---|---|
| `%LOCALAPPDATA%\Shorekeeper\identity.pfx.dpapi` | Khóa thiết bị (mã hóa DPAPI) |
| `%LOCALAPPDATA%\Shorekeeper\shorekeeper.db` | SQLite |
| `%LOCALAPPDATA%\Shorekeeper\settings.json` | Cài đặt người dùng |
| `%LOCALAPPDATA%\Shorekeeper\logs\` | Log, giữ 14 ngày |

Xóa `identity.pfx.dpapi` thì app sẽ tạo DeviceId mới (các liên hệ phải Kết nối lại). Trước khi cài lại Windows hoặc đổi máy, dùng Cài đặt → **Sao lưu danh tính…**, rồi **Khôi phục…** trên máy mới.
