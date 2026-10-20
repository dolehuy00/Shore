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
Shorekeeper.Android              app Avalonia.Android: view mobile; Platform/ (Keystore, logcat, sau này thông báo,
                                 content:// URI, foreground service), tách project khi cần dùng chung
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
| `IExternalFileSource` (A3: file không có đường dẫn — tên, size, thời gian sửa, mở stream seek được) | Không dùng | `content://` URI qua `ParcelFileDescriptor` |
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

Màn hình: Xóm (danh sách, chạm để gửi), Hộp nhận, Đã gửi, Nhóm, Cài đặt. Gửi từ app khác: **Chia sẻ → Shorekeeper** mở màn chọn người (giống cửa sổ Gửi nhanh). Nút + ở Xóm: **Quét mã QR** trên máy tính (kết nối ngay, docs/04-identity-security.md §4.4; trình quét của Google Play services, gói `Xamarin.GooglePlayServices.Code.Scanner`, không cần quyền camera) hoặc nhập tên/IP. **Gửi văn bản…** (liên hệ, nhóm) điền sẵn chữ đang copy trên điện thoại (chỉ chữ thuần, không quá giới hạn gửi); Android báo app đã đọc bảng nhớ tạm.

### 3.5 Nghe âm thanh

- Foreground service riêng loại `mediaPlayback` (`FOREGROUND_SERVICE_MEDIA_PLAYBACK`) với thông báo "Đang nghe âm thanh từ …" [Dừng], nên tắt màn hình vẫn nghe. Máy phát cho điều khiển (máy tính) thì thông báo là [Phát/Dừng] [Bài sau] [Dừng nghe], và tab Âm thanh có thẻ "Điều khiển …"; nút gửi phím media qua `audio/listeners/{sessionId}/keys`.
- `AudioTrack` chế độ low latency, bộ đệm 3 khung (30 ms; 2 khung thỉnh thoảng cạn). Luồng phát ở ưu tiên `UrgentAudio`.
- Trong lúc nghe giữ `WifiLock` low latency (chỉ có hiệu lực khi app ở trước và màn hình sáng) và high perf cho lúc còn lại, cùng một partial wake lock.
- Đo trên OPPO Reno10 (điện thoại phát hotspot cho PC, đồng thời bắt Wi-Fi nhà): giật đến từ chính lần quét Wi-Fi của điện thoại (khoảng 10 phút một lần, mỗi lần khoảng 5 giây, trễ 55–140 ms); bộ đệm tự giãn biến chúng thành vài khung âm đoán thay vì mất tiếng.
- AES-GCM của .NET trên Android hủy tiến trình khi đầu vào rỗng: keep-alive luôn niêm phong 1 byte.

### 3.6 Phát âm thanh

- **Nguồn: micro** ("dùng điện thoại làm micro"): `AudioRecord` nguồn `Mic`, 48 kHz mono (chép ra hai kênh), bật `NoiseSuppressor` nếu máy có; chỉ cần `RECORD_AUDIO`, không cần media projection. Foreground service cùng `AudioCaptureService` nhưng loại `microphone` (`FOREGROUND_SERVICE_MICROPHONE`, API 30+; Android 10 không có loại này), khởi động lúc app ở trước như Android yêu cầu. Thông báo "Đang phát tiếng micro của điện thoại" [Dừng].
- Thu bằng `AudioPlaybackCapture` (API 29+): cần quyền `RECORD_AUDIO` và, mỗi lần bật, sự đồng ý cho `MediaProjection` (hộp "ghi hoặc truyền màn hình"; Android 14+ xin cả màn hình, vì chia sẻ một app chỉ thu tiếng app đó).
- Projection do foreground service loại `mediaProjection` (`FOREGROUND_SERVICE_MEDIA_PROJECTION`) giữ suốt lúc Phát âm thanh bật, với thông báo "Đang phát âm thanh của điện thoại" [Dừng]. Tắt công tắc thì trả projection; người dùng dừng từ thông báo hay ô truyền màn hình của hệ thống thì công tắc tắt theo.
- Chỉ thu usage media, game và unknown; app có thể cấm thu, cuộc gọi không bao giờ thu được. Bỏ qua chính Shorekeeper (`excludeUid`), để điện thoại vừa nghe vừa phát không gửi lại tiếng đang nghe.
- `AudioRecord` PCM 16-bit 48 kHz stereo, chỉ mở khi có người nghe; luồng thu ở ưu tiên `UrgentAudio`, giữ cùng các khóa Wi-Fi và wake lock như khi nghe.

### 3.7 Tự gửi ảnh

- `MediaStoreLibrary` đọc `MediaStore.Files` (ảnh và video, bỏ mục `is_pending`); mốc là `_id`, tăng theo mục mới. Máy ảnh là `relative_path` DCIM/Camera, ảnh chụp màn hình là thư mục có "Screenshots". File gửi bằng content URI của mục.
- Quyền `READ_MEDIA_IMAGES` + `READ_MEDIA_VIDEO` (API 33+) hoặc `READ_EXTERNAL_STORAGE` (API 29–32), xin lúc bật công tắc. Bị thu hồi thì không gửi gì đến khi cho lại.
- `ContentObserver` báo thư viện đổi; engine chạy nền khi bật Luôn nhận file.

### 3.8 Widget màn hình chính

- `HomeWidget` (`AppWidgetProvider`, tên Java cố định `dev.shorekeeper.app.HomeWidget` để widget đã đặt không mất khi build lại): dòng trạng thái và ba ô Gửi file / Gửi chữ đã chép / Nghe máy tính. Android 12+ chọn bố cục theo cỡ: cao 2 ô thì icon trên nhãn (`layout/widget.xml`), cao 1 ô thì icon cạnh nhãn ngắn (`layout/widget_compact.xml`). Icon là đường vẽ của `Icons.axaml`; màu theo sáng/tối của hệ thống (`values-night`).
- Không cập nhật định kỳ: engine đẩy mỗi lần danh bạ, máy online hay việc nghe đổi. Thêm widget hay khởi động lại máy thì `APPWIDGET_UPDATE` mở tiến trình, và engine chạy theo `Application.OnCreate`.
- Gửi file và Gửi chữ mở activity với extra `widget`: `ACTION_OPEN_DOCUMENT` (chọn nhiều) rồi "Gửi … mục cho…"; hoặc "Gửi văn bản cho…" với clipboard, đọc lúc cửa sổ có focus (Android 10+ chỉ cho đọc clipboard khi đó).
- Nghe máy tính là broadcast tới `WidgetActions` (không export): đang nghe thì dừng, không thì nghe liên hệ đang Phát âm thanh, máy tính trước điện thoại (tiến trình vừa mở chờ tối đa 4 giây để thấy máy phát), dùng mật khẩu đã nhớ. Foreground service phát nhạc được mở từ nền vì người dùng vừa bấm widget. Lỗi báo bằng Toast.

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

**Kết quả (2026-10-15, ✅ xong, ADR-015):**

- `Shorekeeper.Engine` không còn tham chiếu ASP.NET Core. Route + quyền hạn nằm ở `PeerApi` (`PeerRequest` → `PeerResponse`), dùng chung cho hai server:
  - `MiniHttpServer` (trong Engine): HTTP/1.1 trên `SslStream`, keep-alive, body `Content-Length` hoặc chunked (≤ 4 MB), một `Range` + `If-Range` cho file, tối đa 256 kết nối, timeout handshake/header/keep-alive. Mặc định của `AddPeerApi()`.
  - `Shorekeeper.Engine.Kestrel` (`AddKestrelPeerApi()`): Kestrel chỉ chuyển request sang `PeerApi`. Desktop vẫn dùng Kestrel.
- Test Engine chạy cả hai server (`SHOREKEEPER_TEST_SERVER=kestrel`): 129 test pass cả hai; MiniHttp còn pass trên Linux (WSL). Thêm test Range quá cuối file (416), body quá lớn (413 / đóng kết nối).
- Tốc độ tải 1 GB qua TLS trên loopback (`Throughput_over_loopback`, chạy khi gọi riêng): MiniHttp 325–354 MB/s, Kestrel 247–276 MB/s.
- Bộ test với MiniHttp chạy ~25 giây thay vì ~2 phút: Kestrel chờ các long-poll của nhóm khi dừng.
- Sửa một test nhóm hay chập chờn (thông báo "đã vào nhóm" đến muộn).
- **Dời sang A2/A3:** `IFileSource` / `IDownloadTarget`. Hiện chỉ có một cách hiện thực (đường dẫn); thiết kế interface khi có `content://` và MediaStore thật.
- Còn mở: desktop có chuyển hẳn sang MiniHttp (bỏ Kestrel, app nhẹ hơn) không.

### A2 — App Android tối thiểu

Thấy nhau, Kết nối (mã 6 số), Hộp nhận: tải về Downloads, tải tiếp khi rớt mạng. App ở foreground.

**Xong khi:** PC gửi 1 GB cho điện thoại, tắt Wi-Fi 20 giây giữa chừng vẫn tải tiếp; hash khớp.

**Kết quả (2026-10-15, ✅ xong):** `src/Shorekeeper.Android` (không nằm trong `Shorekeeper.sln`, cần workload `android`).

- Engine chạy nguyên bộ dịch vụ của desktop với `MiniHttpServer`. Dữ liệu trong bộ nhớ riêng của app, khóa thiết bị mã hóa bằng khóa AES trong Android Keystore (`KeystoreSecretProtector`), file nhận vào `Download/Shorekeeper` bằng đường dẫn (Android 11+; Android 10 dùng `requestLegacyExternalStorage`). Log ra logcat, tag `Shorekeeper`.
- Tên máy lấy từ `Settings.Global.device_name` (Engine cho đặt `LocalDevice.HostName` và `SettingsService.DefaultDisplayName`, vì trên Android tên máy là "localhost"). Presence báo `os: "android"`.
- Một màn hình: tab Xóm (liên hệ + người đang online, [Kết nối]), tab Hộp nhận ([Tải] [Bỏ qua], tiến độ), lớp phủ so mã khi kết nối. ViewModel riêng cho mobile; `TransferText` dùng chung với desktop.
- Thử thật: OPPO Reno10 5G (Android 13) phát Wi-Fi, laptop bắt Wi-Fi đó. Kết nối bằng mã từ desktop; PC gửi 1 GB → điện thoại tải ~25 MB/s, SHA-256 khớp. Lần hai: ngắt Wi-Fi laptop 21 giây khi đã tải ~735 MB → app đứng chờ, ~10 giây sau khi có mạng lại tự tải tiếp, xong, SHA-256 khớp.
- Lỗi tìm ra nhờ thử thật, đã sửa trong Engine: kiểm tra dung lượng trống trên phân vùng `/` (653 MB, đầy) thay vì phân vùng chứa Download (185 GB). Giờ chọn điểm mount dài nhất chứa thư mục nhận (cũng đúng cho Linux/macOS), có test.
- Khác kế hoạch: phần riêng của Android (Keystore, logcat) nằm trong thư mục `Platform/` của app, chưa tách project `Shorekeeper.Platform.Android`; `IDownloadTarget` chưa cần vì ghi bằng đường dẫn vào Download chạy được.
- Chưa có: thông báo, chạy nền (A4), gửi từ điện thoại (A3), cài đặt (đổi tên hiển thị), icon riêng.

### A3 — Gửi từ điện thoại

Chia sẻ từ Gallery / Files → chọn người → gửi; chọn file trong app. File đọc thẳng từ `content://` (không sao chép), hỗ trợ `Range`.

**Xong khi:** gửi 50 ảnh từ Gallery lên PC trong một offer.

**Kết quả (2026-10-15, ✅ xong):**

- Engine: `IExternalFileSource` (Core) cho file không có đường dẫn. `OfferService.Prepare` nhận cả đường dẫn lẫn vị trí của nguồn ngoài; khi phục vụ, kiểm tra "file đã đổi" và mở stream qua nguồn đó; `FileHasher` tính hash cũng vậy. Không có bản sao tạm cho file loại này. Desktop không đổi (nguồn rỗng). Test: gửi và tải về đúng, tên trùng được đổi, `Range` và 412 khi file đổi — chạy trên cả hai server.
- Android: `ContentUriFiles` đọc tên/size/thời gian sửa từ provider (`_display_name`, `_size`, `last_modified` hoặc `date_modified`), mở file qua `ParcelFileDescriptor` → `FileStream` seek được. App nhận `ACTION_SEND` / `ACTION_SEND_MULTIPLE` (bỏ qua intent mở lại từ Gần đây), hiện "Gửi … mục cho…" để chọn liên hệ; nút "Gửi file…" trên thẻ liên hệ mở trình chọn file; tab "Đã gửi" (trạng thái từng người, Thu hồi).
- Thử thật: chia sẻ 48 ảnh từ Gallery → PC trong một offer (27,9 MB, ~26 giây); cả 48 file trên PC khớp SHA-256 với ảnh gốc trên điện thoại.
- Quyền đọc `content://` từ menu Chia sẻ là quyền tạm, gắn với activity nhận; sau khi app bị Android đóng, người nhận chưa tải có thể gặp lỗi (chưa kiểm chứng). Chạy nền ở A4 giảm việc này.
- Chưa thử trên máy thật: nút "Gửi file…" (trình chọn file trong app).

### A4 — Chạy nền & thông báo

Thông báo có file đến (Tải / Bỏ qua), foreground service khi truyền, tùy chọn "Luôn nhận file", quyền `ACCESS_LOCAL_NETWORK` (Android 17) và màn hình giải thích khi bị từ chối.

**Xong khi:** màn hình tắt, PC gửi file → điện thoại hiện thông báo; tải xong khi máy vẫn khóa màn hình.

**Kết quả (2026-10-15, ✅ xong):**

- `TransferService`: foreground service `connectedDevice` + `WifiLock` (high perf) + `WakeLock` (partial), chạy khi đang nhận, khi còn người có thể tải file mình gửi (offer mở, người nhận chưa xong), hoặc luôn luôn với "Luôn nhận file" (lưu trong SharedPreferences). Hết lý do thì tự dừng. Thông báo thường trực nói đang làm gì.
- Thông báo "… gửi cho bạn" (kênh quan trọng cao) với [Tải] [Bỏ qua]: nút là `PendingIntent.GetForegroundService` mang sẵn offer (URI riêng cho mỗi offer), nên chạy được cả khi Android đã đóng app; bấm vào thông báo mở tab Hộp nhận; thông báo tự gỡ khi offer đã xử lý trong app. Xin `POST_NOTIFICATIONS` khi mở app.
- Thử thật (OPPO, Android 13, giả lập chạy pin + Doze sâu bằng `dumpsys`): lần 1 app bị **ColorOS (Athena/guardelf) đóng cưỡng bức** sau ~5 phút dù có foreground service. Thêm: bật "Luôn nhận file" thì xin miễn tối ưu hóa pin (`REQUEST_IGNORE_BATTERY_OPTIMIZATIONS`) + nhắc bật "hoạt động nền" trong cài đặt pin của hãng. Lần 2 (đã miễn + cho phép hoạt động nền): app sống > 13 phút, PC vẫn gọi được; PC gửi 1 GB → thông báo trên màn hình khóa → bấm Tải không mở khóa → xong ~20 giây, SHA-256 khớp.
- Bài học: trên máy OPPO/Xiaomi/Vivo… foreground service **không đủ**; người dùng phải tự cho phép chạy nền trong cài đặt của hãng (app chỉ nhắc được). Xem dontkillmyapp.com.
- Chưa làm: quyền mạng LAN của Android 17 (app đang target API 36 nên chưa bị áp), tự chạy lại sau khi khởi động lại máy, giữ service đủ 30 phút liên tục (đã thử ~13 phút).

### A5 — Nhóm & hoàn thiện

Nhóm (thành viên), sao lưu/khôi phục danh tính (đổi điện thoại), chẩn đoán mạng (gợi ý khi Wi-Fi cách ly client, R7), tiếng Việt + tiếng Anh.

Chia làm A5a (Android) và A5b (tiếng Anh cho cả desktop lẫn Android, chuỗi tách ra resource) — quyết định 2026-10-15.

**A5a — kết quả (2026-10-15, ✅ xong):**

- Tab **Nhóm** (điện thoại chỉ làm thành viên, không làm Host): nhóm của mình (thành viên, online, Host offline), "Nhóm trong mạng" → [Xin vào], lời mời [Đồng ý] [Từ chối], [Gửi file cho nhóm…] (mọi thành viên khác, offer mang `groupId`), [Rời nhóm] / [Hủy yêu cầu], thông báo gần đây. Màn hình chọn người khi Chia sẻ có thêm người cùng nhóm; mỗi nhóm một offer, như cửa sổ Gửi nhanh của desktop.
- Tab **Cài đặt**: đổi tên hiển thị, "Luôn nhận file" (chuyển từ đầu màn hình), **sao lưu** danh tính vào `Download/Shorekeeper` và **khôi phục** (chọn file `content://`, mật khẩu, xem tóm tắt, app tự khởi động lại để engine áp dụng — ADR-013), **mạng**: địa chỉ của điện thoại, cổng, số máy đang thấy, gợi ý khi Wi-Fi cách ly thiết bị, **thêm máy bằng địa chỉ** (dùng lại `ManualPeerFinder`, rồi Kết nối).
- Thử thật với desktop bản dev: desktop tạo nhóm "Thử A5" → điện thoại [Xin vào] → desktop duyệt → điện thoại gửi 6 ảnh cho nhóm → desktop tải xong, 6/6 khớp SHA-256 (desktop thêm " (1)" vì trùng tên ảnh đã nhận trước đó). Sao lưu tạo được file `.skbackup`; mở lại file đó với mật khẩu hiện đúng tóm tắt (DeviceId, liên hệ, nhóm).
- Chưa kiểm chứng trên máy thật: bấm khôi phục đến bước khởi động lại, thêm máy bằng địa chỉ.

**A5b — kết quả (2026-10-15, ✅ xong):**

- Lớp `L` trong Core: câu tiếng Việt là khóa, bản tiếng Anh trong `en.json` nhúng vào assembly (382 câu: Engine, desktop, Android). Chọn theo `CurrentUICulture` (Windows, hoặc ngôn ngữ máy/ngôn ngữ riêng của app trên Android 13); `SHOREKEEPER_LANGUAGE` để ép. Số và ngày theo `L.Culture` ("1,5 GB" / "1.5 GB").
- AXAML dùng `{l:T '…'}` (`TExtension`, Android link cùng file). Thiếu bản dịch thì hiện tiếng Việt; test `LocalizationTests` quét `src/` để bắt câu chưa dịch và kiểm placeholder `{0}` khớp.
- Thử trên điện thoại: máy vi-VN hiện tiếng Việt; đặt ngôn ngữ riêng cho app là en-US (`cmd locale set-app-locales`) thì tab, Cài đặt, thẻ người và thông báo nền đều tiếng Anh. Đã trả lại ngôn ngữ mặc định.
- Còn tiếng Việt: chữ "Gửi bằng Shorekeeper…" trong menu chuột phải của Explorer (MSI ghi cố định vào registry) và file ADMX chỉ có `vi-VN`.

### A6 — Phát hành

APK ký bằng keystore riêng (miễn phí, không cần mua chứng chỉ như Authenticode), phát hành trên GitHub Releases. Play Store tùy chọn (phí đăng ký một lần, phải khai báo lý do dùng foreground service và quyền mạng LAN).

Quyết định (2026-10-16): build bằng script trên máy và bằng workflow khi push tag; một Release gồm cả APK và MSI.

**A6 — kết quả (2026-10-16):**

- **Keystore phát hành** `shorekeeper-release.keystore` (PKCS12, RSA 4096, alias `shorekeeper`, hạn đến 2054), chứng chỉ SHA-256 `96:3A:5C:4A:C4:2A:5E:A0:0C:84:82:27:F1:AC:8C:EB:99:D7:95:03:48:34:D7:03:00:D5:F0:75:11:DE:40:26` (in trong ghi chú Release để người tải kiểm tra). Keystore và mật khẩu **không nằm trong repo** (`.gitignore` chặn `*.keystore`, `*.jks`). **Mất keystore là không cập nhật được app nữa**: Android chỉ cho cài đè bản ký cùng khóa, người dùng phải gỡ app (mất danh tính nếu chưa sao lưu). Giữ một bản sao keystore + mật khẩu ở nơi an toàn ngoài máy build.
- **Version**: `ApplicationDisplayVersion` lấy `Version` trong `Directory.Build.props`; versionCode = major·10000 + minor·100 + patch (0.1.0 → 100), luôn tăng khi tăng version.
- **`deploy/build-apk.ps1`**: `dotnet publish` có ký, mật khẩu đọc qua `env:SHOREKEEPER_KEYSTORE_PASSWORD` (không lộ trên dòng lệnh), ra `artifacts/apk/Shorekeeper-<version>.apk` (≈ 28 MB, chỉ arm64-v8a cho điện thoại — bản Debug vẫn có x86_64 cho máy ảo; biên dịch AOT toàn bộ để mở app nhanh hơn; chữ ký APK v3).
- **`.github/workflows/release.yml`**: push tag `v<Version>` (sai version thì dừng) → build MSI + APK trên `windows-latest`, `SHA256SUMS.txt`, `gh release create` với `deploy/release-notes.md`. Chạy tay (workflow_dispatch) thì chỉ build và đính kèm file vào lần chạy, không tạo Release. Cần hai secret: `SHOREKEEPER_KEYSTORE_BASE64`, `SHOREKEEPER_KEYSTORE_PASSWORD`.
- Thử trên máy: script ra APK, `apksigner verify` đúng chứng chỉ trên, versionCode 100; MSI 75 MB; bước tính checksum chạy được. Điện thoại: gỡ bản ký debug (khác khóa nên không cài đè được), cài bản phát hành, app tạo danh tính mới và chạy.
- Chưa kiểm chứng: workflow trên GitHub (cần thêm secret rồi chạy tay hoặc push tag).

Phát hành một bản mới: tăng `Version` trong `Directory.Build.props` → sửa `deploy/release-notes.md` nếu cần → commit, push → `git tag v<Version>` và push tag.

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
