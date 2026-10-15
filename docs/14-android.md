# 14 — Bản Android (kế hoạch)

> Trạng thái: **kế hoạch**, chưa làm. Các điểm đánh dấu 🔍 phải kiểm chứng ở giai đoạn A0 trước khi viết code thật.

## 1. Mục tiêu

Điện thoại Android trong cùng mạng Wi-Fi với các PC chạy Shorekeeper: thấy nhau, Kết nối, nhận file từ PC và gửi file (ảnh, tài liệu) lên PC. **Cùng giao thức** với bản desktop ([05](05-protocol.md)), không có server trung gian.

### 1.1 Phạm vi bản đầu (v1)

| Có | Không (để sau hoặc không làm) |
|---|---|
| Discovery (multicast + probe), danh sách "Xóm" | Host nhóm, Bridge, Subnet Probe (điện thoại hay ngủ, hay đổi mạng) |
| Kết nối bằng mã 6 số | AD, GPO (không có trên Android) |
| Hộp nhận: tải, tải tiếp khi rớt mạng, lưu vào Downloads | Kéo thả, phím tắt |
| Gửi: từ menu **Chia sẻ** của Android và từ trong app | Tự nhận file (có thể thêm sau) |
| Nhóm với vai trò **thành viên** (xin vào, nhận lời mời, gửi trong nhóm) | |
| Thông báo khi có file đến, sao lưu/khôi phục danh tính (đã có trong Engine, ADR-013) | |

## 2. Ràng buộc của nền tảng

| # | Ràng buộc | Ảnh hưởng | Nguồn |
|---|---|---|---|
| R1 | **Kestrel / ASP.NET Core không chạy trên Android** (không có runtime pack `Microsoft.AspNetCore.App` cho `android-*`; yêu cầu hỗ trợ vẫn còn mở) | Engine hiện tham chiếu ASP.NET Core (`ApiServer`, `PeerApiEndpoints`, `PeerAccess`, `OfferService`) nên **không dùng nguyên được** trên Android | [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/886545/maui-and-kestrel-webserver-on-android-arm-and-ios), dotnet/aspnetcore#35077 |
| R2 | `SslStream` trên Android chạy qua TLS của Android; mTLS (client cert, cert tự ký ECDSA, callback tự kiểm tra) ít tài liệu, có báo lỗi khi chuyển từ Xamarin sang .NET 🔍 | Danh tính dựa hẳn trên mTLS ([04](04-identity-security.md) §2) | [Microsoft Q&A](https://learn.microsoft.com/en-us/answers/questions/2115650/sslstream-authentication-failing-in-net-maui-andro) |
| R3 | Nhận multicast cần `WifiManager.MulticastLock` + quyền `CHANGE_WIFI_MULTICAST_STATE` | Thiếu thì không nghe được presence (probe unicast vẫn chạy) | Tài liệu Android |
| R4 | **Android 17 (targetSdk 37)** chặn truy cập mạng LAN mặc định, cần quyền runtime `ACCESS_LOCAL_NETWORK` (Android 16: tùy chọn, dùng `NEARBY_WIFI_DEVICES`) | Phải xin quyền lúc mở app lần đầu, xử lý khi người dùng từ chối | [Local network permission](https://developer.android.com/privacy-and-security/local-network-permission) |
| R5 | App ở nền bị Doze dừng mạng. Foreground service `dataSync` bị giới hạn **6 giờ / 24 giờ** khi ở nền (Android 15+); `connectedDevice` không bị giới hạn này nhưng có điều kiện riêng 🔍 | Không thể "luôn online" như PC mà không có thông báo thường trực | [FGS timeout](https://developer.android.com/develop/background-work/services/fgs/timeout), [FGS types](https://developer.android.com/develop/background-work/services/fg-service-types) |
| R6 | File chia sẻ đến là `content://` URI, không phải đường dẫn; Downloads ghi qua `MediaStore` | `OfferService` và Hộp nhận đang làm việc với đường dẫn file | Tài liệu Android |
| R7 | Wi-Fi công ty thường **cách ly client** (AP isolation) hoặc tách VLAN cho điện thoại | Điện thoại không tới được PC dù cùng tên Wi-Fi; app chỉ chẩn đoán được, không vượt qua được | — |
| R8 | Emulator nằm sau NAT (10.0.2.x), không thấy multicast của LAN | Thử thật phải dùng điện thoại thật cùng Wi-Fi với PC | — |

## 3. Kiến trúc

```text
Shorekeeper.Core                 (không đổi: giao thức, DeviceId, cài đặt)
Shorekeeper.Engine               (bỏ ASP.NET Core) discovery, trust, offer, inbox, nhóm, sao lưu
   └── Api/PeerApiHandlers       handler thuần: (caller, request) → kết quả; quyền hạn kiểm tra ở đây
Shorekeeper.Engine.Kestrel       (desktop) host Kestrel gọi PeerApiHandlers   ← code hiện tại chuyển sang đây
Shorekeeper.Engine.MiniHttp      (Android, dùng được cả desktop) HTTP/1.1 tối giản trên SslStream
Shorekeeper.Platform.Android     Keystore, MulticastLock, thông báo, content:// URI, MediaStore, foreground service
Shorekeeper.Android              app Avalonia.Android: view mobile, dùng lại ViewModel khi được
```

### 3.1 Server HTTPS không có Kestrel (R1)

API của Shorekeeper nhỏ (khoảng 25 endpoint, JSON, `Range` khi tải file, long-poll) và **chỉ app của mình gọi**, nên một server HTTP/1.1 tối giản là đủ:

- `TcpListener` → `SslStream` (server cert = khóa thiết bị, bắt buộc client cert) → đọc request line + header, `Content-Length` (không cần chunked phía nhận), keep-alive, `Range` một đoạn.
- Handler lấy ra khỏi minimal API thành `PeerApiHandlers` dùng chung, nên Kestrel (desktop) và MiniHttp (Android) cùng một logic quyền hạn.
- **Chạy toàn bộ test Engine trên cả hai host** để chắc MiniHttp đúng; sau đó cân nhắc dùng MiniHttp luôn cho desktop (một đường code, bỏ phụ thuộc ASP.NET Core).
- Nếu `SslStream` trên Android không làm được mTLS (R2): dùng TLS thuần .NET của BouncyCastle (`Org.BouncyCastle.Tls`) cho cả server lẫn client trên Android.

### 3.2 Những abstraction cần thêm

| Interface | Desktop | Android |
|---|---|---|
| `ISecretProtector` | DPAPI | Android Keystore (khóa AES không xuất được) |
| `IFileSource` (đọc file để gửi: tên, size, thời gian sửa, mở stream) | Đường dẫn | `content://` URI, giữ quyền đọc bằng `takePersistableUriPermission` |
| `IDownloadTarget` (nơi ghi file nhận) | Thư mục | `MediaStore.Downloads` (file `.skpart` trong cache app, xong mới chép sang) |
| `INotifier` | Toast | Notification channel + action (Tải / Bỏ qua) |
| `INetworkLocks` | — | `MulticastLock`, `WifiLock` khi đang truyền |

### 3.3 Chạy nền (R5)

- **Mặc định:** online khi app đang mở. Đóng app thì gửi `bye`, PC thấy điện thoại offline; offer gửi tới được giao khi điện thoại online lại (đã có cơ chế giao lại).
- **Đang truyền file:** foreground service + thông báo tiến độ, giữ `WifiLock`; xong thì dừng.
- **Tùy chọn "Luôn nhận file":** foreground service thường trực (thông báo cố định), tốn pin hơn. Loại service (`connectedDevice` hay `dataSync`) chốt ở A0 🔍.

### 3.4 Giao diện

| Lựa chọn | Ưu | Nhược |
|---|---|---|
| **Avalonia.Android** (đề xuất) | Cùng stack với desktop, dùng lại ViewModel, CommunityToolkit.Mvvm, ngôn ngữ C# | View phải làm lại cho màn hình nhỏ; cộng đồng Android nhỏ hơn |
| .NET MAUI | Control native, tài liệu Android nhiều hơn | Stack UI thứ hai phải duy trì |
| Kotlin native | Tốt nhất về nền tảng | Viết lại toàn bộ giao thức, discovery, mTLS: hai codebase |

Màn hình: Xóm (danh sách, chạm để gửi), Hộp nhận, Đã gửi, Nhóm, Cài đặt. Gửi từ app khác: **Chia sẻ → Shorekeeper** mở màn chọn người (giống cửa sổ Gửi nhanh).

## 4. Giai đoạn

### A0 — Spike trên điện thoại thật

Mỗi mục là một app thử nhỏ, không phải code sản phẩm:

1. `SslStream` server + client mTLS với cert ECDSA P-256 tự ký, callback tự kiểm tra DeviceId, giữa điện thoại và PC (cả hai chiều).
2. Nhận multicast 239.x với `MulticastLock`; gửi probe unicast.
3. SQLite + Dapper chạy trên Android (bản build Release có trimming).
4. Avalonia.Android hiện một danh sách, nhận Intent chia sẻ (`ACTION_SEND_MULTIPLE`).
5. Foreground service loại nào chạy được khi màn hình tắt 30 phút.

**Xong khi:** PC và điện thoại gọi `GET /hello` của nhau qua mTLS và nhận được presence của nhau. Nếu mục 1 thất bại: thử lại với BouncyCastle TLS trước khi đi tiếp.

**Kết quả (2026-10-15, ✅ xong):** điện thoại OPPO CPH2525, Android 13; laptop Windows 11, cùng Wi-Fi 5 GHz. Code thử ở `spikes/android-a0/` (A0Phone, A0Ui, A0Pc).

| # | Kết quả |
|---|---|
| 1 mTLS | ✅ `SslStream` của .NET chạy được cả hai vai: điện thoại làm server (bắt buộc client cert) và làm client gọi Shorekeeper thật trên PC. TLS 1.3, DeviceId lấy từ cert khớp. **Không cần BouncyCastle** (R2 đã gỡ) |
| 2 Multicast | ✅ Có `MulticastLock` thì nghe được heartbeat của PC; probe unicast được PC trả lời |
| 3 SQLite + Dapper | ✅ Bản Release |
| 4 Avalonia.Android | ✅ Giao diện + tiếng Việt. Activity của Avalonia là `AppCompatActivity` → phải đặt theme `Theme.AppCompat.*`. Nhận được `ACTION_SEND` từ Files (`content://`, đọc tên + size + stream); phải đọc cả `ClipData` lẫn `EXTRA_STREAM`. Avalonia chỉ có **một** giao diện chính: activity phải là `launchMode=singleTask`, nếu không chia sẻ lúc app đang mở sẽ tạo activity thứ hai và crash. Mở lại app từ danh sách gần đây thì Android **gửi lại intent chia sẻ cũ** (quyền đọc URI đã hết): bỏ qua intent có cờ `FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY`. File 50 MB từ Files đọc ở 137 MB/s |
| 5 Chạy nền | Server trong activity: màn hình tắt + Doze thì **không trả lời**. Foreground service `connectedDevice` + `WifiLock` (high perf) + `WakeLock` (partial): màn hình tắt + Doze sâu vẫn **14–16 MB/s**. Mới thử ~1 phút Doze, chưa thử 30 phút |

- Tốc độ điện thoại → PC (100 MB): TLS 7–16 MB/s qua Wi-Fi, 27 MB/s qua USB; TLS chỉ tốn ~15% so với TCP thường.
- Bài học về mạng (R7): trên một mạng mesh, mọi kết nối giữa hai máy chỉ đạt 0,1 MB/s, kể cả `nc` ngoài app; đổi Wi-Fi thì bình thường. Phần Chẩn đoán của app Android nên đo và báo trường hợp này.
- Rule firewall "Public" của Windows có thể có sẵn nếu người dùng đã bấm Allow ở hộp thoại của Windows; rule của MSI chỉ mở Domain/Private.
- Chia sẻ nhiều file (`ACTION_SEND_MULTIPLE`): 4 ảnh từ Files, đủ tên + size.
- Chưa thử: giữ service 30 phút, quyền mạng LAN của Android 17.

### A1 — Tách Engine khỏi ASP.NET Core

- `PeerApiHandlers` + `Shorekeeper.Engine.Kestrel` (desktop không đổi hành vi) + `Shorekeeper.Engine.MiniHttp`.
- `IFileSource`, `IDownloadTarget` cho `OfferService` / `InboxService` (desktop vẫn dùng đường dẫn).

**Xong khi:** toàn bộ test Engine pass với cả hai host; app desktop chạy như M6b.

### A2 — App Android tối thiểu

Thấy nhau, Kết nối (mã 6 số), Hộp nhận: tải về Downloads, tải tiếp khi rớt mạng. App ở foreground.

**Xong khi:** PC gửi 1 GB cho điện thoại, tắt Wi-Fi 20 giây giữa chừng vẫn tải tiếp; hash khớp.

### A3 — Gửi từ điện thoại

Chia sẻ từ Gallery / Files → chọn người → gửi; chọn file trong app. File đọc thẳng từ `content://` (không sao chép), hỗ trợ `Range`.

**Xong khi:** gửi 50 ảnh từ Gallery lên PC trong một offer.

### A4 — Chạy nền & thông báo

Thông báo có file đến (Tải / Bỏ qua), foreground service khi truyền, tùy chọn "Luôn nhận file", quyền `ACCESS_LOCAL_NETWORK` (Android 17) và màn hình giải thích khi bị từ chối.

**Xong khi:** màn hình tắt, PC gửi file → điện thoại hiện thông báo; tải xong khi máy vẫn khóa màn hình.

### A5 — Nhóm & hoàn thiện

Nhóm (thành viên), sao lưu/khôi phục danh tính (đổi điện thoại), chẩn đoán mạng (gợi ý khi Wi-Fi cách ly client, R7), tiếng Việt + tiếng Anh.

### A6 — Phát hành

APK ký bằng keystore riêng (miễn phí, không cần mua chứng chỉ như Authenticode), phát hành trên GitHub Releases. Play Store tùy chọn (phí đăng ký một lần, phải khai báo lý do dùng foreground service và quyền mạng LAN).

## 5. Quyết định

| Câu hỏi | Chốt (2026-10-15) |
|---|---|
| Giao diện | **Avalonia.Android** |
| Android tối thiểu | **Android 10 (API 29)**: bỏ được cách ghi file cũ trước scoped storage |
| Điện thoại để thử | **1 máy Android 13** cùng Wi-Fi với laptop. Android 13 cần xin quyền `POST_NOTIFICATIONS` lúc chạy; quyền mạng LAN của Android 17 (R4) chưa thử được trên máy này |
| Chạy nền mặc định | (đề xuất) Chỉ khi mở app + tùy chọn "Luôn nhận file" |
| Phân phối | (đề xuất) APK trên GitHub Releases trước |
| Desktop có chuyển sang MiniHttp không | Quyết định sau A1, dựa trên kết quả test và hiệu năng |

## 6. Rủi ro

| Rủi ro | Mức | Giảm thiểu |
|---|---|---|
| mTLS trên Android không chạy với `SslStream` (R2) | Cao | A0 làm đầu tiên; dự phòng BouncyCastle TLS |
| MiniHttp chậm hơn Kestrel, ảnh hưởng mục tiêu ≥ 90 MB/s | Trung bình | Điện thoại hiếm khi đạt tốc độ đó qua Wi-Fi; giữ Kestrel cho desktop nếu cần |
| Wi-Fi công ty cách ly điện thoại (R7) | Cao, ngoài tầm tay | Phát hiện và giải thích trong Chẩn đoán; IT phải cho phép |
| Chính sách nền của Android đổi theo phiên bản | Trung bình | Không dựa vào chạy nền cho chức năng chính; offer được giao lại khi app mở |
| Trimming/AOT làm hỏng Dapper, JSON reflection | Thấp | Đã dùng JSON source generator; kiểm tra ở A0 mục 3 |
