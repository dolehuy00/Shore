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

```bash
dotnet run --project src/Shorekeeper.Desktop
```

Thêm `-- --background` để khởi động thẳng xuống khay hệ thống, hoặc `-- --send <file>…` để mở cửa sổ Gửi nhanh với các file đó (menu "Gửi bằng Shorekeeper…" của Explorer gọi như vậy).

Bộ cài MSI (per-machine, cần admin để cài) và template GPO: xem [docs/11-windows-deployment.md](docs/11-windows-deployment.md).

```bash
powershell -ExecutionPolicy Bypass -File deploy/build-msi.ps1
```

## Cấu trúc

| Project | Vai trò |
|---|---|
| `src/Shorekeeper.Core` | Domain, DeviceId, cấu hình, interface platform. Không I/O mạng, không phụ thuộc Windows |
| `src/Shorekeeper.Engine` | Khóa định danh, SQLite + migration, cấu hình, hosting (sau này: discovery, API, gửi/nhận, nhóm) |
| `src/Shorekeeper.Platform.Windows` | DPAPI, đọc GPO từ registry, firewall, Mark of the Web, toast, phím tắt toàn cục |
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

Xóa `identity.pfx.dpapi` thì app sẽ tạo DeviceId mới (các liên hệ phải Kết nối lại).
