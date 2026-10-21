# 15 — Tin nhắn

> Trạng thái: **C1–C4 xong** (2026-10-21: văn bản, tin nhắn thoại, thẻ file, xóa, trên máy tính và điện thoại), C5 kế hoạch. Đã chốt (2026-10-21): hội thoại **1-1 trước**, nhóm sau; trên điện thoại **gộp Hộp nhận + Đã gửi** thành tab "File" để có chỗ cho tab "Tin nhắn"; file gửi qua lại giữa hai người **hiện thành thẻ** trong hội thoại.

## 1. Mục tiêu

Hai người đã Kết nối nhắn cho nhau **văn bản** và **tin nhắn thoại** ngay trong Shorekeeper, trên máy tính và điện thoại. Cùng nguyên tắc với gửi file: không có server, chỉ gửi thẳng giữa hai máy, chỉ Trusted.

| Có | Không (để sau hoặc không làm) |
|---|---|
| Hội thoại 1-1 với liên hệ đã Kết nối | Chat nhóm (giai đoạn C5, sau C4) |
| Văn bản, tin nhắn thoại tối đa 5 phút | Gọi thoại/video trực tiếp |
| Chờ gửi khi máy kia offline, tự gửi khi máy kia online | Gửi qua máy trung gian (Bridge, Host) |
| Đã tới / đã xem, thu hồi tin của mình | "Đang soạn…", sửa tin |
| Xóa tin / hội thoại trên máy mình | Đồng bộ lịch sử giữa các máy của cùng một người |
| File gửi với người đó hiện thành thẻ trong hội thoại; ảnh hiện thẳng trong thẻ | |

**Gửi văn bản** (offer có note, không file — [06](06-file-transfer.md)) giữ nguyên trong bản này; gộp nó vào tin nhắn hay không thì chốt sau C2.

## 2. Engine: `ChatService`

Tin nhắn **không đi qua offer**: offer là để người nhận chọn tải, có hạn, có thu hồi; tin nhắn cần thứ tự, đã xem và nhẹ.

- Mỗi tin có ID **do người gửi tạo**: UUIDv7, 32 chữ số hex, xếp theo thời gian. Người nhận đã có ID đó thì vẫn trả `204` và bỏ qua, nên gửi lại bao nhiêu lần cũng an toàn.
- Gửi: lưu tin với trạng thái `pending` rồi `POST /chat/messages`. Máy kia offline (`PeerUnreachableException`), bận (`429`) hay lỗi máy chủ (`5xx`) thì để nguyên; khi `PeerDirectory` báo máy đó online thì gửi tiếp các tin `pending` theo thứ tự, giống `OfferService.DeliverPendingAsync`, và cứ 30 giây thử lại với máy đang online. Mỗi máy chỉ một luồng gửi, nên thứ tự giữ nguyên.
- Bị từ chối (`403`: máy kia đã Ngắt kết nối / chặn, hoặc là bản chưa có chat; `400`) thì `failed`, bấm vào để gửi lại.
- Thứ tự hiển thị theo giờ gửi của người gửi (`sentAt`), cùng giờ thì theo ID. Giờ hai máy lệch nhau không làm đảo tin của cùng một người. Mỗi tin gửi đi có `sentAt` sau tin trước nó ít nhất 1 ms: UUIDv7 ngẫu nhiên trong cùng một mili giây, nên hai tin gửi liền nhau có thể đảo chỗ nếu chỉ dựa vào ID.
- **Đã xem**: mở hội thoại (cửa sổ đang hiện, đúng hội thoại đó) thì gửi `POST /chat/receipts` với ID tin cuối đã đọc; máy kia đánh dấu "Đã xem" mọi tin tới ID đó. Receipt cũng chờ gửi khi offline, nhưng chỉ giữ cái mới nhất.
- Presence thêm `caps: "chat"` (`PeerInfo.CanChat`). Máy nghe trực tiếp mà không có `chat` (bản cũ) thì ô soạn khóa lại với dòng "Máy này chưa cập nhật để nhận tin nhắn", vì bản cũ trả `403` cho route lạ. Máy chỉ thấy qua Bridge hay thêm bằng địa chỉ thì không biết `caps` (Bridge không chuyển), nên vẫn cho gửi.
- Giới hạn: văn bản 4.000 ký tự; thoại 5 phút; 20 tin/giây mỗi máy gửi (quá thì `429`).
- Thống kê ([10](10-ux.md) §15) đếm tin gửi (khi máy kia nhận) và tin nhận (lần đầu tới), theo ngày và theo máy.
- **Xóa** chỉ trên máy mình: từng tin (`DeleteAsync`, cả file thoại; tin đang chờ gửi thì không gửi nữa) hoặc cả hội thoại (`DeleteConversationAsync`, kèm "Đã xem" chưa báo). Máy kia vẫn giữ bản của nó.
- **Thu hồi** (`RecallAsync`) tin chữ hoặc thoại của mình, bất kỳ lúc nào: ở cả hai máy tin đó thành dòng ghi chú in nghiêng "Tin nhắn đã được thu hồi" (`kind` `recalled`, giữ chỗ và giờ gửi), chữ, sóng âm và file thoại bị xóa. Báo cho máy kia bằng `POST /chat/recalls`, chờ gửi như receipt khi máy kia offline (bảng `ChatRecalls`). Tin chưa đi thì không bao giờ đi nữa. Máy kia chỉ đổi tin **của người gọi** gửi cho nó; bản chưa có thu hồi từ chối thì thôi, tin ở đó còn nguyên. Thu hồi là nhờ máy kia xóa chứ không ép được: ai đã chép, chụp hay sửa app thì vẫn giữ.

### 2.1 Trạng thái một tin (phía người gửi)

Hiện dưới bong bóng, sau giờ gửi ("09:29 · Đã xem"): "Chờ gửi" và "Không gửi được" trên mọi tin như thế, "Đã nhận" / "Đã xem" chỉ dưới tin mới nhất của mình (tin trước đó tất nhiên cũng đã tới).

| Trạng thái | Hiện | Khi nào |
|---|---|---|
| `pending` | Chờ gửi | Chưa tới được máy kia |
| `delivered` | Đã nhận | Máy kia trả `204` |
| `read` | Đã xem | Có receipt tới ID này hoặc sau |
| `failed` | Không gửi được + [Gửi lại] | `403` / lỗi không thử lại được |

## 3. Tin nhắn thoại

- Ghi micro (khung 10 ms 48 kHz stereo như `IAudioCapture`, trộn về mono), Opus "VoIP" 24 kbps, gói 20 ms (Concentus, đã có), chứa trong **Ogg** (RFC 7845, `OggOpus` tự viết trong Core: một luồng, trang tối đa 1 giây) để mở được bằng app khác. Khoảng 3 KB mỗi giây; 5 phút ≈ 0,9 MB. Tới 5 phút thì tự dừng và gửi.
- Lúc ghi tính luôn **48 vạch sóng âm** (đỉnh mỗi đoạn, chia theo đỉnh lớn nhất, 0–255), gửi kèm tin để bên nhận vẽ bong bóng ngay. Dưới 0,5 giây thì không gửi ("ngắn quá").
- Âm thanh đi **trong chính request gửi tin** (base64 trong JSON, dưới giới hạn 4 MB của `MiniHttpServer`), không tải riêng như file: người gửi tắt máy ngay sau khi gửi thì bên nhận vẫn đủ.
- Lưu ở `data/chat/voice/{id}.ogg` (cả tin gửi lẫn nhận; bên nhận ghi file trước rồi mới lưu tin); xóa tin thì xóa file. File của tin chờ gửi mất thì tin đó thành lỗi.
- Phát: `IVoicePlayer` trong Core. Windows dùng chính `WindowsAudioOutput` (loa mặc định); Android dùng một `AudioTrack` riêng (âm lượng media, nội dung "speech"), **không** dùng `IAudioOutput` vì cái đó bật service + thông báo "Đang nghe" và khi dừng sẽ tắt luôn "Nghe máy tính" đang chạy. Một lúc chỉ phát một tin; ghi âm hay đổi hội thoại thì dừng phát.
- Thu micro: `IMicrophone` trong Core, tách khỏi `IAudioCapture` (âm thanh của máy). Android dùng lại phần thu micro của "Nguồn tiếng: Micro" (`AndroidAudioCapture`, có khử ồn nếu máy hỗ trợ); Windows dùng WASAPI từ micro mặc định (`LoopbackCapture` ở chế độ micro, Windows tự đổi sang 48 kHz stereo). Không có micro hay Windows chặn quyền micro thì báo lỗi trên ô soạn.

## 4. API

| Method | Path | Quyền | Ghi chú |
|---|---|---|---|
| `POST` | `/chat/messages` | `Trusted` | Một tin → `204`; trùng ID thì vẫn `204`, không làm gì |
| `POST` | `/chat/receipts` | `Trusted` | `{ readUpTo }` |
| `POST` | `/chat/recalls` | `Trusted` | `{ ids }`: tin của người gọi cần thu hồi, tối đa 500 |

```json
{
  "id": "019a05f1c2d87b3e9a41f6c0d2e5b7a9",
  "sentAt": "2026-10-21T08:15:30Z",
  "kind": "text",
  "text": "Anh gửi lại file báo cáo nhé"
}
```

```json
{
  "id": "019a05f2401c7d5f8e23a9b4c6d8e0f1",
  "sentAt": "2026-10-21T08:16:02Z",
  "kind": "voice",
  "durationMs": 12400,
  "wave": "base64 48 byte",
  "audio": "base64 Ogg Opus"
}
```

Trường lạ thì bỏ qua, `kind` lạ thì hiện "Tin nhắn này cần bản Shorekeeper mới hơn", để thêm loại tin sau không làm hỏng bản cũ.

## 5. Lưu trữ

Migration `0006_chat.sql` ([09](09-data-storage.md)):

- `ChatMessages` (khóa `PeerId` + `Id`): `Outgoing`, `Kind`, `Text`, `VoicePath`, `DurationMs`, `Wave`, `SentAt`, `ReceivedAt`, `State`. Tin nhận: `unread` → `read`; tin gửi theo §2.1. Danh sách hội thoại, tin cuối và số chưa đọc tính từ bảng này.
- `ChatReceipts`: `PeerId`, `ReadUpTo`: "Đã xem" chưa báo được cho máy kia, xóa khi máy kia đã trả lời.
- `ChatRecalls` (migration `0007_chat_recalls.sql`): `PeerId`, `Id`: thu hồi chưa báo được cho máy kia, xóa khi máy kia đã trả lời.
- Không tự xóa tin. Chữ rất nhẹ; tin thoại hiện dung lượng trong Thống kê.

## 6. Giao diện

### 6.1 Máy tính

- Trang mới **Tin nhắn** ở thanh bên, dưới Xóm; chấm đỏ khi có tin chưa đọc (kiểu indicator như Âm thanh).
- Trái: danh sách hội thoại (máy tính / điện thoại, chấm online, tên, dòng tin cuối, giờ, số chưa đọc; tin chưa đọc in đậm), mới nhất lên trên; sau đó các liên hệ chưa nhắn lần nào ("Chưa có tin nhắn"), theo tên, để bắt đầu nhắn ngay từ đây. Không có ô tìm riêng: liên hệ trong LAN thường ít, và đã có `Ctrl+K`.
- Phải: luồng tin. Bong bóng của mình bên phải màu nhấn, của người kia bên trái; ngăn ngày ("Hôm nay", "Hôm qua", "20/10"); giờ + trạng thái dưới tin cuối mỗi cụm.
- Phải: tên + trạng thái, bong bóng (của mình bên phải màu nhấn), ngăn ngày, ô soạn dưới cùng. Không viết được thì ô soạn khóa và nói vì sao (chưa kết nối, bản cũ); máy kia offline thì vẫn viết được, kèm dòng "tin nhắn sẽ được gửi khi máy online".
- Ô soạn: Enter gửi, Shift+Enter xuống dòng. 🎤 bên trái ô soạn bấm để ghi; khi ghi, ô soạn thành [✕] ● 0:07 [mức âm] [➤]: ✕ hủy, ➤ gửi.
- Gửi file cho người này ngay trong hội thoại: 📎 (chọn file), **Ctrl+V** trong ô soạn (file đã copy, hoặc ảnh copy dạng ảnh như ở Xóm; chữ thì dán như thường) hoặc **kéo thả** vào vùng tin nhắn (viền xanh khi đang kéo qua). Một offer như gửi từ Xóm, nhưng không chuyển sang "Đã gửi": file hiện thành thẻ trong hội thoại (§6.3). Được cả với máy bản cũ, vì file thì luôn gửi được; chỉ cần đã Kết nối.
- Chuột phải một tin: **Sao chép** (tin chữ), **Thu hồi** (tin của mình), **Chọn nhiều**, **Xóa tin nhắn**. Chọn nhiều: mỗi tin có ô chọn, click cả hàng để chọn/bỏ (hàng được chọn nền xanh nhạt), thanh trên cùng [☐ Chọn tất cả] "Đã chọn N" [Xóa] [Xong]; xóa từ 2 tin trở lên thì hỏi trước. Địa chỉ web trong tin (tối đa 3) hiện thành dòng gạch chân ngay dưới, bấm thì mở trình duyệt. ⋮ trên đầu hội thoại: **Xóa hội thoại…** (hỏi trước).
- Bong bóng thoại: ▶/⏸ trên nền tròn, 48 vạch sóng (phần đã phát tô đậm), thời lượng.
- Thẻ người ở Xóm thêm nút 💬 và mục **Nhắn tin** (liên hệ). Toast tin mới (trừ khi "Bận", hoặc đang mở đúng hội thoại đó và cửa sổ ở trước), bấm thì mở đúng hội thoại; tắt toast thì chỉ còn số chưa đọc trên "Tin nhắn".
- Mở hội thoại thì hiện ngay tin mới nhất (cuộn tới cuối trước khi vẽ khung đầu tiên, không thì hội thoại dài hiện chỗ cũ một khung rồi nhảy), và giữ ở cuối khi ảnh tải xong làm thẻ cao lên; kéo lên đọc tin cũ thì thôi giữ. Điện thoại cũng vậy (`ChatScroll`, dùng chung).
- Tin đọc khi trang Tin nhắn đang mở đúng hội thoại **và** cửa sổ đang ở trước (thu nhỏ, ở khay hay sau cửa sổ khác thì chưa).

### 6.2 Điện thoại

- Tab: **Xóm · File · Tin nhắn · Nhóm · Âm thanh · Cài đặt**. Tab **File** = Hộp nhận và Đã gửi gộp lại, công tắc **Nhận / Đã gửi** ở trên; chọn nhiều, xóa giữ như hiện tại.
- Tab Tin nhắn (chấm đỏ khi có tin chưa đọc): danh sách như máy tính; chạm một người thì hội thoại thay chỗ danh sách, ← hoặc nút Back của Android quay lại danh sách.
- 📎 bên trái ô soạn: trình chọn file của hệ thống (như "Gửi file…" ở Xóm), gửi cho người này và ở lại hội thoại; file hiện thành thẻ.
- Bàn phím hiện thì nội dung được đẩy lên trên nó (ô soạn ngay trên bàn phím) và thanh tab ẩn đi; app không tự co lại khi bàn phím mở (Avalonia `InputPane`).
- Ô soạn trống thì nút tròn bên phải là 🎤 thay cho ➤: **giữ để ghi** (nút phồng lên, rung nhẹ), ô soạn thành "● 0:07 ‹ Vuốt sang trái để hủy"; vuốt trái quá ~90 dp thì nút đỏ "Thả tay để hủy"; thả tay để gửi. Chạm nhanh (thả trước khi micro mở) thì không ghi, nhắc "Giữ nút micrô để ghi". Lần đầu giữ thì Android hỏi quyền micrô; hộp thoại lấy mất ngón tay nên lần đó không ghi, cấp quyền xong giữ lại là được.
- Chấm đỏ nhấp nháy chỉ gắn khi đang ghi: animation lặp mãi làm app vẽ lại liên tục, và trên điện thoại khiến bộ đếm tiến độ phát (`DispatcherTimer` ưu tiên Background) không bao giờ chạy.
- Thông báo kiểu tin nhắn (`MessagingStyle`, kênh "Tin nhắn"), một thông báo mỗi người với tối đa 8 tin chưa đọc, có **Trả lời** ngay trên thông báo (`RemoteInput`) và **Đánh dấu đã đọc**; chạm thì mở đúng hội thoại. Trả lời là đã đọc: app gỡ thông báo. ColorOS vẫn giữ thẻ đã trả lời (kèm "Bạn: …" do Android thêm) trong khung thông báo tới khi vuốt bỏ, dù hệ thống đã gỡ nó.
- Không có thông báo khi đang mở đúng hội thoại đó và app ở trước.
- Nhấn giữ một tin: menu **Sao chép** / **Thu hồi** (tin của mình) / **Chọn nhiều** / **Xóa tin nhắn** (rung nhẹ). Đang chọn thì chạm hoặc nhấn giữ một tin để chọn/bỏ; thanh trên cùng [☐ Chọn tất cả] "Đã chọn N" [✕] [Xóa], như ở tab File; link dưới tin chạm để mở; ⋮ trên đầu hội thoại: **Xóa hội thoại…** (hỏi trước).
- Widget: dòng trạng thái bắt đầu bằng "2 tin chưa đọc · " khi có.

### 6.3 File trong hội thoại

Offer mình gửi cho người đó (không qua nhóm) và mọi offer người đó gửi cho mình (Hộp nhận không giữ nhóm của offer) hiện thành **thẻ** trong luồng tin theo **giờ gửi của người gửi**, như tin nhắn, bên của người gửi. Offer nhận lấy `createdAt` trong manifest (không muộn hơn lúc tới, cột `InboxOffers.SentAt`), nên offer giao trễ vì máy nhận đang tắt vẫn nằm đúng chỗ giữa các tin nhắn, giống nhau ở hai máy; offer nhận trước migration 0008 không có giờ đó và xếp theo lúc tới. Thẻ có tên file ("app.zip + 2 mục") hoặc chữ của "Gửi văn bản", rồi giờ (như dưới tin nhắn), dung lượng, trạng thái (lời như ở Hộp nhận / Đã gửi), thanh tiến độ khi đang tải, [Tải] với offer mới và [Xem] mở tab Hộp nhận / Đã gửi (điện thoại: File → Nhận / Đã gửi), nơi làm được mọi việc khác. Hộp nhận và Đã gửi vẫn đủ mọi offer như cũ; thẻ chỉ là một cách xem khác, không lưu thêm gì. Trong danh sách hội thoại, offer mới hơn tin nhắn cuối thì thay nó: giờ của offer, dòng "[Ảnh]" / "[3 ảnh]" / chữ của "Gửi văn bản" / "app.zip + 2 mục" (của mình thì thêm "Bạn: "), và xếp theo giờ đó; người chỉ mới gửi file cũng lên danh sách. Offer không tính vào số chưa đọc.

Chuột phải (điện thoại: nhấn giữ) một thẻ: **Thu hồi** offer mình gửi còn mở (hỏi trước, như ở Đã gửi: người nhận không tải được nữa) và **Xem**.

**Ảnh hiện thẳng**: offer chỉ gồm ảnh (theo đuôi file, như nhóm "Ảnh" ở Thống kê) hiện tối đa 4 ảnh thay cho tên file, thêm "+N" nếu nhiều hơn; bấm một ảnh thì mở bằng trình xem của hệ thống (điện thoại: tab File). Ảnh được thu nhỏ còn rộng 480 px khi đọc, ngoài luồng giao diện.

- Ảnh mình gửi đọc từ file gốc (hoặc snapshot). Trên điện thoại, file chọn bằng trình chọn của hệ thống (📎, "Gửi file…", widget) là `content://`; app giữ quyền đọc lâu dài (`TakePersistableUriPermission`) nên mở lại app ảnh vẫn hiện. Android chỉ giữ một số quyền như thế cho mỗi app (512 từ Android 11) và tự bỏ cái cũ nhất. File chia sẻ từ app khác thì không giữ được: mở lại app, hay file đã bị xóa, thì thẻ quay về dạng tên file.
- Ảnh người kia gửi hiện khi đã tải xong và file còn đó. Để khỏi phải bấm Tải, **Cài đặt → "Tự tải ảnh máy đã kết nối gửi"** (mặc định bật; `chatAutoDownloadPictures`): offer từ liên hệ đã Kết nối, không qua nhóm, chỉ gồm ảnh và tổng tối đa **20 MB** thì tự tải về thư mục nhận như "Tự nhận file". Tắt thì ảnh chờ bấm Tải như file khác; bật "Tự nhận file" cho máy đó thì giới hạn của nó vẫn áp dụng như cũ.

## 7. Giai đoạn

| Giai đoạn | Nội dung | Xong khi |
|---|---|---|
| **C1** Engine văn bản | Migration, `ChatService`, 2 endpoint, chờ gửi / gửi tiếp khi online, chống trùng, đã tới / đã xem, `caps: "chat"` | Test Engine 2 máy loopback: gửi/nhận, offline → online gửi đúng thứ tự, gửi lại trùng ID, Unknown/chặn → `403`, receipt |
| **C2** Giao diện văn bản | Trang Tin nhắn (PC), tab File + tab Tin nhắn (điện thoại), chưa đọc, nút ở Xóm, toast, thông báo có Trả lời | Nhắn qua lại thật PC ↔ điện thoại, offline rồi online |
| **C3** Tin nhắn thoại | `IMicrophone` (WASAPI micro, Android micro), ghi Ogg Opus + sóng âm, gửi kèm tin, bong bóng phát | Test mã hóa ↔ giải mã Ogg; ghi và nghe thật trên cả hai máy |
| **C4** Hoàn thiện ✅ | Thẻ file trong hội thoại, xóa tin (menu chuột phải / nhấn giữ, chọn nhiều) và hội thoại (⋮), chép chữ, link bấm được, Thống kê, widget; sau đó gửi file ngay trong hội thoại, ảnh hiện thẳng, thu hồi tin và file | Test engine (xóa, đếm, thu hồi, tự tải ảnh) + thử trên máy thật |
| **C5** Chat nhóm | Hội thoại theo Nhóm: gửi tỏa ra từng thành viên, ai offline nhận sau, đã xem theo từng người | Kế hoạch riêng khi tới |

## 8. Rủi ro

| Rủi ro | Cách xử lý |
|---|---|
| Hai máy không cùng online thì tin không tới | Hiện "Chờ gửi" rõ ràng; tin chờ gửi giữ mãi, không hết hạn |
| Tin tới lúc app Android ở nền bị đo bố cục sai (cùng lỗi header cao 0, [14](14-android.md)) | Thấy lỗi đó thì đo lại cả màn hình, không chỉ header và tab |
| Giờ hai máy lệch | Chỉ dùng để xếp; giờ nhận lưu riêng; thứ tự tin của một người không đổi |
| Micro bị app khác giữ / chưa cấp quyền | Báo lỗi trong ô soạn, không gửi tin rỗng; Android xin quyền lần đầu giữ 🎤 |
| Điện thoại thử nghiệm (OPPO, Android 13) tự khởi động lại hai lần trong lúc làm app | Không do app: log nhân lần trước (trong báo cáo lỗi, "LAST KMSG") ghi dm-verity thấy khối hỏng trên phân vùng chỉ-đọc `my_product` của ColorOS rồi panic (lỗi bộ nhớ hoặc firmware). Gặp lại thì tạo báo cáo lỗi để đọc |
| Gộp tab File làm đổi thói quen người dùng | Giữ nguyên nội dung và thao tác của hai danh sách, chỉ đổi chỗ vào |
