# 15 — Tin nhắn (kế hoạch)

> Trạng thái: **kế hoạch**, chưa làm. Đã chốt (2026-10-21): hội thoại **1-1 trước**, nhóm sau; trên điện thoại **gộp Hộp nhận + Đã gửi** thành tab "File" để có chỗ cho tab "Tin nhắn"; file gửi qua lại giữa hai người **hiện thành thẻ** trong hội thoại.

## 1. Mục tiêu

Hai người đã Kết nối nhắn cho nhau **văn bản** và **tin nhắn thoại** ngay trong Shorekeeper, trên máy tính và điện thoại. Cùng nguyên tắc với gửi file: không có server, chỉ gửi thẳng giữa hai máy, chỉ Trusted.

| Có | Không (để sau hoặc không làm) |
|---|---|
| Hội thoại 1-1 với liên hệ đã Kết nối | Chat nhóm (giai đoạn C5, sau C4) |
| Văn bản, tin nhắn thoại tối đa 5 phút | Gọi thoại/video trực tiếp |
| Chờ gửi khi máy kia offline, tự gửi khi máy kia online | Gửi qua máy trung gian (Bridge, Host) |
| Đã tới / đã xem | "Đang soạn…", sửa tin, thu hồi ở máy bên kia |
| Xóa tin / hội thoại trên máy mình | Đồng bộ lịch sử giữa các máy của cùng một người |
| File gửi với người đó hiện thành thẻ trong hội thoại | |

**Gửi văn bản** (offer có note, không file — [06](06-file-transfer.md)) giữ nguyên trong bản này; gộp nó vào tin nhắn hay không thì chốt sau C2.

## 2. Engine: `ChatService`

Tin nhắn **không đi qua offer**: offer là để người nhận chọn tải, có hạn, có thu hồi; tin nhắn cần thứ tự, đã xem và nhẹ.

- Mỗi tin có ID **ULID do người gửi tạo**. Người nhận đã có ID đó thì trả `200` và bỏ qua, nên gửi lại bao nhiêu lần cũng an toàn.
- Gửi: lưu tin với trạng thái `pending` rồi `POST /chat/messages`. Máy kia offline (`PeerUnreachableException`) thì để nguyên; khi `PeerDirectory` báo máy đó online thì gửi tiếp các tin `pending` theo thứ tự, giống `OfferService.DeliverPendingAsync`.
- Bị từ chối (`403`, máy kia đã Ngắt kết nối / chặn) thì `failed`, bấm vào để gửi lại.
- Thứ tự hiển thị theo giờ gửi của người gửi (`sentAt`), cùng giờ thì theo ID. Giờ hai máy lệch nhau không làm đảo tin của cùng một người.
- **Đã xem**: mở hội thoại (cửa sổ đang hiện, đúng hội thoại đó) thì gửi `POST /chat/receipts` với ID tin cuối đã đọc; máy kia đánh dấu ✓✓ mọi tin tới ID đó. Receipt cũng chờ gửi khi offline, nhưng chỉ giữ cái mới nhất.
- Presence thêm `caps: "chat"`. Máy không có `chat` (bản cũ) thì ô soạn khóa lại với dòng "Máy này chưa cập nhật để nhận tin nhắn", không gửi rồi nhận `404`.
- Giới hạn: văn bản 4.000 ký tự; thoại 5 phút; 20 tin/giây mỗi máy gửi (quá thì `429`).
- Bộ đếm Thống kê ([10](10-ux.md) §15) thêm số tin gửi/nhận (C4).

### 2.1 Trạng thái một tin (phía người gửi)

| Trạng thái | Hiện | Khi nào |
|---|---|---|
| `pending` | 🕓 | Chưa tới được máy kia |
| `delivered` | ✓ | Máy kia trả `200` |
| `read` | ✓✓ (màu nhấn) | Có receipt tới ID này hoặc sau |
| `failed` | ⚠ + "Bấm để gửi lại" | `403` / lỗi không thử lại được |

## 3. Tin nhắn thoại

- Ghi micro mono 48 kHz, Opus 24 kbps (Concentus, đã có), chứa trong **Ogg** để "Lưu file" ra mở được bằng app khác. 5 phút ≈ 0,9 MB.
- Lúc ghi tính luôn **48 vạch sóng âm** (đỉnh mỗi đoạn, 0–255), gửi kèm tin để bên nhận vẽ bong bóng ngay.
- Âm thanh đi **trong chính request gửi tin** (base64 trong JSON, dưới giới hạn 4 MB của `MiniHttpServer`), không tải riêng như file: người gửi tắt máy ngay sau khi gửi thì bên nhận vẫn đủ.
- Lưu ở `data/chat/voice/{id}.ogg`; xóa tin thì xóa file.
- Phát bằng `IAudioOutput` có sẵn trên cả hai nền tảng; một lúc chỉ phát một tin.
- Thu micro: Android dùng lại phần thu micro của "Nguồn tiếng: Micro" (`AndroidAudioCapture`); Windows thêm thu WASAPI từ micro mặc định (hiện chỉ có loopback). Interface mới `IMicrophone` trong Core, tách khỏi `IAudioCapture` (âm thanh của máy).

## 4. API

| Method | Path | Quyền | Ghi chú |
|---|---|---|---|
| `POST` | `/chat/messages` | `Trusted` | Một tin; trùng ID thì `200` không làm gì |
| `POST` | `/chat/receipts` | `Trusted` | `{ readUpTo }` |

```json
{
  "id": "01JB8Q4Z6V2N3K5M7P9R1T3W5Y",
  "sentAt": "2026-10-21T08:15:30Z",
  "kind": "text",
  "text": "Anh gửi lại file báo cáo nhé"
}
```

```json
{
  "id": "01JB8Q5A1C3E5G7J9L1N3Q5S7U",
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

- `ChatMessages`: `Id`, `PeerId`, `Outgoing`, `Kind`, `Text`, `VoicePath`, `DurationMs`, `Wave`, `SentAt`, `ReceivedAt`, `State`.
- `ChatConversations`: `PeerId`, `LastMessageId`, `LastAt`, `Unread`, `ReadUpTo` (mình đã đọc tới đâu), `TheirReadUpTo` (máy kia đã đọc tới đâu), `ReceiptPending`.
- Không tự xóa tin. Chữ rất nhẹ; tin thoại hiện dung lượng trong Thống kê.

## 6. Giao diện

### 6.1 Máy tính

- Trang mới **Tin nhắn** ở thanh bên, dưới Xóm; chấm đỏ khi có tin chưa đọc (kiểu indicator như Âm thanh).
- Trái: danh sách hội thoại (ảnh, tên, chấm online, dòng tin cuối, giờ, số chưa đọc), mới nhất lên trên, ô tìm theo tên.
- Phải: luồng tin. Bong bóng của mình bên phải màu nhấn, của người kia bên trái; ngăn ngày ("Hôm nay", "Hôm qua", "20/10"); giờ + trạng thái dưới tin cuối mỗi cụm.
- Ô soạn: Enter gửi, Shift+Enter xuống dòng; Ctrl+V ảnh/file thì thành offer gửi cho người này. 🎤 bấm để ghi (đồng hồ + mức âm), bấm ➤ để gửi, ✕ để hủy. 📎 chọn file → offer.
- Thẻ người ở Xóm thêm **Nhắn tin**; toast tin mới, bấm thì mở đúng hội thoại.

### 6.2 Điện thoại

- Tab: **Xóm · File · Tin nhắn · Nhóm · Âm thanh · Cài đặt**. Tab **File** = Hộp nhận và Đã gửi gộp lại, công tắc **Nhận / Đã gửi** ở trên; chọn nhiều, xóa giữ như hiện tại.
- Tab Tin nhắn: danh sách hội thoại; chạm một hội thoại thì mở màn hình riêng đè lên tab, nút Back của Android quay lại.
- 🎤 **giữ để ghi**, vuốt trái để hủy, thả tay để gửi (như Zalo/Messenger); rung nhẹ khi bắt đầu ghi.
- Thông báo kiểu tin nhắn (`MessagingStyle`), gộp theo người, có **Trả lời** ngay trên thông báo (`RemoteInput`) và **Đánh dấu đã đọc**.
- Widget: dòng trạng thái thêm "2 tin chưa đọc" (C4).

### 6.3 File trong hội thoại

Offer chỉ gửi cho đúng người đó (hoặc người đó gửi cho mình, không qua nhóm) hiện thành **thẻ** trong luồng tin theo giờ gửi: tên file / số file, dung lượng, tiến độ, [Tải] / [Mở], trạng thái như ở Hộp nhận. Hộp nhận và Đã gửi vẫn đủ mọi offer như cũ; thẻ chỉ là một cách xem khác, không lưu thêm gì.

## 7. Giai đoạn

| Giai đoạn | Nội dung | Xong khi |
|---|---|---|
| **C1** Engine văn bản | Migration, `ChatService`, 2 endpoint, chờ gửi / gửi tiếp khi online, chống trùng, đã tới / đã xem, `caps: "chat"` | Test Engine 2 máy loopback: gửi/nhận, offline → online gửi đúng thứ tự, gửi lại trùng ID, Unknown/chặn → `403`, receipt |
| **C2** Giao diện văn bản | Trang Tin nhắn (PC), tab File + tab Tin nhắn (điện thoại), chưa đọc, nút ở Xóm, toast, thông báo có Trả lời | Nhắn qua lại thật PC ↔ điện thoại, offline rồi online |
| **C3** Tin nhắn thoại | `IMicrophone` (WASAPI micro, Android micro), ghi Ogg Opus + sóng âm, gửi kèm tin, bong bóng phát | Test mã hóa ↔ giải mã Ogg; ghi và nghe thật trên cả hai máy |
| **C4** Hoàn thiện | Thẻ file trong hội thoại, xóa tin / hội thoại (nhấn giữ để chọn), chép chữ, link bấm được, Thống kê, widget, cập nhật 05/09/10/12 | Test + thử trên máy thật |
| **C5** Chat nhóm | Hội thoại theo Nhóm: gửi tỏa ra từng thành viên, ai offline nhận sau, đã xem theo từng người | Kế hoạch riêng khi tới |

## 8. Rủi ro

| Rủi ro | Cách xử lý |
|---|---|
| Hai máy không cùng online thì tin không tới | Hiện 🕓 rõ ràng; tin chờ gửi giữ mãi, không hết hạn |
| Giờ hai máy lệch | Chỉ dùng để xếp; giờ nhận lưu riêng; thứ tự tin của một người không đổi |
| Micro bị app khác giữ / chưa cấp quyền | Báo lỗi trong ô soạn, không gửi tin rỗng; Android xin quyền lần đầu bấm 🎤 |
| Gộp tab File làm đổi thói quen người dùng | Giữ nguyên nội dung và thao tác của hai danh sách, chỉ đổi chỗ vào |
