# 18 — Tin nhắn: tìm, trả lời, @nhắc tên, sửa, cảm xúc (C6)

> Trạng thái: **C6a xong** (2026-10-31); C6b–C6d theo kế hoạch đã chốt (§8). Nối tiếp [15](15-chat.md) (1-1) và [16](16-group-chat.md) (nhóm), lúc đó để sau: "@nhắc tên, cảm xúc, trả lời trích dẫn, sửa tin", và "tìm trong tin nhắn" ([17](17-ui-cleanup.md) U5a).

## 1. Mục tiêu

| Làm | Không làm (§7) |
|---|---|
| **Tìm theo nội dung tin nhắn**: Ctrl+K trên PC, ô tìm ở tab Tin nhắn trên điện thoại | Đồng bộ lịch sử giữa các máy của cùng một người |
| **Trả lời trích dẫn** một tin, 1-1 và nhóm | Tìm trong tin nhắn thoại |
| **@nhắc tên** thành viên trong nhóm | Lịch sử các lần sửa |
| **Sửa tin** chữ của mình | Cảm xúc tùy chọn ngoài bộ cố định |
| **Thả cảm xúc** (bộ 6 biểu tượng cố định), 1-1 và nhóm | Chuyển hộ tin của người đã rời nhóm ([ADR-016](decisions.md)) |

Thứ tự làm: **C6a tìm** (không đổi giao thức) → **C6b trả lời + @nhắc tên** (cùng đổi nội dung tin, đổi chữ ký nhóm một lần) → **C6c sửa** → **C6d cảm xúc**.

## 2. Tương thích bản cũ

- Presence thêm `caps: "chat2"`. Máy không có `chat2` vẫn nhắn được như hiện nay; những gì mới chỉ đi tới máy có `chat2`.
- **1-1**: máy cũ bỏ qua trường lạ ([15 §4](15-chat.md)), nên tin có `replyTo` vẫn tới, chỉ không hiện phần trích. Lệnh sửa và cảm xúc đi route mới, chỉ gửi cho máy có `chat2`; máy cũ vẫn thấy chữ cũ, không thấy cảm xúc.
- **Nhóm**: chữ ký là chuỗi byte cố định có tiền tố phiên bản ([16 §3.5](16-group-chat.md)). Tin có trường mới ký bằng `shorekeeper-group-chat/2`, gồm thêm `replyTo` và `mentions`; tin không có trường mới vẫn ký `/1`. Máy cũ không kiểm được `/2` nên sẽ bỏ tin. Vì vậy tin `/2` không gửi cho thành viên chưa có `chat2`, và với người đó tin mang trạng thái `old` ("Máy này chưa cập nhật"), như máy chưa có chat nhóm hiện nay.

## 3. C6a — Tìm theo nội dung tin nhắn ✅ (2026-10-31)

**Engine**
- `ChatService.FindAsync` và `GroupChatService.FindAsync`: đọc tin chữ từ mới tới cũ (`QueryUnbufferedAsync`), giữ tin có **mọi từ** của câu tìm theo đúng quy tắc của Ctrl+K (`TextSearch`: không phân biệt hoa thường, bỏ dấu, `đ` thành `d`), dừng khi đủ 50. Không thêm cột hay migration: lọc trong bộ nhớ, vài nghìn tin chỉ mất vài ms, và sửa tin (C6c) không phải cập nhật gì thêm. Đo lại nếu chậm (FTS5).
- Bỏ tin thoại, tin đã thu hồi; tin nhóm đã xóa (`Deleted`) cũng bỏ, tin 1-1 đã xóa thì không còn trong bảng.

**Chung cho hai app** (`ChatViewModel.FindAsync`, `Shorekeeper.UI`)
- Gộp tin 1-1, tin nhóm và **thẻ văn bản** (offer chỉ có chữ, gửi hay nhận, chưa thu hồi), mới nhất trước, tối đa 50.
- Mỗi kết quả (`ChatSearchHit`): tên hội thoại, giờ, và một dòng "Mai: …nộp Báo cáo tháng 9…": người viết, rồi đoạn chữ quanh chỗ khớp (`TransferText.Snippet`, tối đa 80 ký tự, cắt ở ranh giới từ). Phần khớp không tô đậm.
- `OpenAt`: mở hội thoại, rồi cuộn tới đúng tin (hay thẻ) và viền nó 2 giây (`ChatScroll.Reveal`, kiểu `Border.found`). Hội thoại tải cả lịch sử nên tin cũ cũng tới được.

**Giao diện**
- **PC**: Ctrl+K liệt kê thêm tối đa 8 tin nhắn (biểu tượng 💬) sau Người / Nhóm / File; chúng tới sau các mục kia một chút vì đọc từ database. Chọn thì sang trang Tin nhắn tại tin đó.
- **Điện thoại**: ô **Tìm trong tin nhắn** luôn nằm đầu danh sách hội thoại (thay cho nút 🔍 dự kiến). Có chữ thì danh sách kết quả hiện thay chỗ các hội thoại, "Không tìm thấy tin nhắn nào." nếu trống; ✕ hay nút Back xóa ô tìm. Chạm một kết quả thì mở hội thoại tại tin đó.

**Đã kiểm**: test Engine (có dấu / không dấu, nhiều từ không theo thứ tự, giới hạn số kết quả, bỏ tin thoại / đã thu hồi / đã xóa, tin nhóm); test UI cho đoạn trích. Thử thật (2026-10-31):
- PC: Ctrl+K "u5 nubia" ra thẻ "Bạn: U5 thu tren Nubia…" trong hội thoại với Z2577; Enter mở hội thoại và viền thẻ đó.
- Nubia: gõ "nubia" ra 2 kết quả (tin nhóm và thẻ văn bản); chạm thẻ thì mở hội thoại với PC và viền nó.

## 4. C6b — Trả lời trích dẫn và @nhắc tên

**Trả lời trích dẫn**
- Tin mang `replyTo`: id của tin được trả lời, trong cùng hội thoại. Phần trích **không** gửi kèm: máy nhận tự tìm tin đó trong hội thoại của mình, nên không ai giả được nội dung trích.
- Bong bóng hiện khung trích phía trên: tên người viết và dòng đầu, "Tin nhắn thoại 0:12", "Ảnh"… Bấm vào khung thì cuộn tới tin gốc và viền nó.
  - Tin gốc đã thu hồi: "Tin nhắn đã được thu hồi".
  - Máy mình không có tin gốc (đã xóa, hay người mới vào nhóm không có lịch sử đó): "Tin nhắn không còn".
- Cách bắt đầu trả lời:
  - PC: mục **Trả lời** khi chuột phải;
  - điện thoại: mục **Trả lời** khi nhấn giữ, và vuốt bong bóng sang phải.
  - Ô soạn hiện dải "Trả lời Mai: …" với nút ✕.
- Trả lời được tin chữ, tin thoại và thẻ file. Không trả lời được tin đã thu hồi.

**@nhắc tên** (chỉ trong nhóm)
- Gõ `@` trong ô soạn thì hiện danh sách thành viên, lọc theo chữ gõ tiếp (không dấu), cùng mục **@mọi người**. Chọn một người thì chèn "@Tên " vào ô soạn.
- Tin mang `mentions`: danh sách DeviceId (hoặc `"all"`), nằm trong phần được ký. Chữ vẫn là "@Tên" như người gửi thấy lúc gửi; máy nhận tô các đoạn "@Tên" của những người có trong `mentions`.
- Người được nhắc:
  - tên mình được tô màu nhấn trong bong bóng;
  - hội thoại có dấu **@** trong danh sách cho tới khi đọc;
  - **có thông báo kể cả khi đã Tắt thông báo nhóm** (§8 Q3).
- Xóa chữ "@Tên" trong ô soạn trước khi gửi thì người đó không còn trong `mentions`.

**Engine và giao thức**
- `ChatMessageBody` và `GroupChatMessageBody` thêm `replyTo?` và `mentions?` (`mentions` chỉ có ở tin nhóm).
- Chữ ký nhóm `/2` (§2). Kiểm thêm: `mentions` không quá số thành viên, và mỗi DeviceId phải là thành viên theo danh sách của máy nhận (lạ thì bỏ khỏi `mentions`, không bỏ cả tin).
- Migration thêm cột `ReplyTo` và `Mentions` cho hai bảng tin.

**Xong khi**:
- Test Engine: `replyTo` qua cả gửi thẳng lẫn chuyển hộ; ký `/2` và người chuyển sửa `mentions` thì tin bị bỏ; máy chưa có `chat2` nhận trạng thái `old`; nhắc người đã rời thì bị bỏ khỏi `mentions`.
- Test UI: khung trích khi tin gốc còn, đã thu hồi hay không có.
- Thử thật 3 máy: trả lời và nhắc tên; người bị nhắc vẫn có thông báo khi nhóm đã Tắt thông báo.

## 5. C6c — Sửa tin

- Chỉ sửa được **tin chữ của mình**, trong **15 phút** sau khi tới được người nhận (§8 Q1). Tin còn chờ gửi thì sửa lúc nào cũng được (như Hủy gửi): tin đi với chữ mới, không có dấu "đã sửa". Không sửa được tin thoại hay thẻ file.
- Bong bóng ghi "· Đã sửa" cạnh giờ. Chỉ giữ chữ mới nhất, không giữ lịch sử các lần sửa.
- **1-1**: `POST /chat/edits` `{ id, text, editedAt }`. Chờ gửi như thu hồi (`ChatEdits`, mới nhất thắng); chỉ gửi cho máy có `chat2`.
- **Nhóm**:
  - Lệnh sửa ký `shorekeeper-group-edit/1` (`groupId`, `id`, `authorId`, `sentAt` của tin, `text`, `editedAt`).
  - Gửi tới các thành viên như thu hồi, và **được chuyển hộ**: sửa thì `Seq` của tin tăng, nên `index` / `fetch` đưa bản mới kèm lệnh sửa đã ký. Máy offline lúc sửa vẫn nhận được chữ mới.
  - Chỉ tác giả sửa được: chữ ký phải khớp `authorId` của tin gốc.
- Thu hồi sau khi sửa được, sửa sau khi thu hồi thì không. Phần trích (C6b) hiện chữ mới nhất của tin gốc. `SearchText` (C6a) đổi theo.
- **Xong khi**: test Engine (1-1, nhóm, chuyển hộ bản sửa, người khác giả lệnh sửa thì bị bỏ, quá 15 phút thì từ chối, sửa khi còn chờ gửi); test UI "Đã sửa"; thử thật PC ↔ điện thoại.

## 6. C6d — Thả cảm xúc

- Bộ cố định 6 biểu tượng: 👍 ❤️ 😂 😮 😢 🙏 (§8 Q2). Mỗi người một cảm xúc cho mỗi tin: chọn cái khác thì thay, chọn lại cái đang thả thì bỏ.
- Hiện dưới bong bóng thành các chip "👍 2 ❤️ 1"; chip có cảm xúc của mình thì tô màu nhấn. Rê chuột hay chạm vào chip thì xem ai đã thả.
- Cách thả:
  - PC: nút 🙂 hiện cạnh bong bóng khi rê chuột, hoặc mục **Thả cảm xúc** khi chuột phải;
  - điện thoại: hàng biểu tượng ở đầu menu khi nhấn giữ.
- **Thông báo, gộp theo từng tin** (§8 Q5): ai đó thả cảm xúc vào tin của mình thì có **một** thông báo cho tin đó.
  - Người đầu tiên: "Mai đã thả ❤️ vào tin của bạn: "họp lúc 3 giờ"".
  - Thêm người thả vào **cùng tin** trong lúc thông báo đó chưa được xem: **sửa** thông báo cũ thay vì gửi cái mới ("Mai, Tuấn và 2 người khác đã thả cảm xúc vào tin của bạn: …"), và không kêu hay rung lại.
  - Một người đổi cảm xúc nhiều lần (thả 👍, đổi ❤️, đổi 😂…) chỉ tính **một** lần; thông báo hiện cảm xúc cuối của họ. Bỏ cảm xúc thì không báo gì.
  - Đã xem thông báo (bấm vào, hay mở hội thoại đó) thì lần thả sau tạo thông báo mới.
  - Như tin nhắn: không báo khi đang mở đúng hội thoại đó, khi đang "Bận", hay khi nhóm đã Tắt thông báo.
  - PC: toast Windows có `Tag` theo tin, toast mới cùng tag thay toast cũ. Điện thoại: thông báo cùng id theo tin, `setOnlyAlertOnce`. `INotifier` thêm khóa để thay thông báo cũ.
  - Danh sách hội thoại ghi "Mai đã thả ❤️" nếu đó là việc mới nhất.
- **1-1**: `POST /chat/reactions` `{ id, emoji?, at }` (`emoji` rỗng nghĩa là bỏ). Chờ gửi theo từng tin, mới nhất thắng.
- **Nhóm**:
  - Ký `shorekeeper-group-reaction/1` (`groupId`, `id` tin, `authorId` người thả, `emoji`, `at`).
  - Gửi tới các thành viên, chờ gửi theo từng người như tin.
  - **Không chuyển hộ**: người mới vào không thấy cảm xúc trên tin cũ.
  - Chỉ nhận cảm xúc trên tin mình có, từ người đang là thành viên.
- Lưu ở bảng `ChatReactions` (hội thoại, `MessageId`, `ReactorId`, `Emoji`, `At`). Tin bị thu hồi thì xóa luôn cảm xúc của nó.
- **Xong khi**: test Engine (thay, bỏ, mới nhất thắng khi tới lệch thứ tự, chữ ký nhóm, người ngoài nhóm thì bị bỏ, thu hồi xóa cảm xúc); test UI các chip và chữ của thông báo gộp (1 người, nhiều người, một người đổi nhiều lần); thử thật 3 máy: hai người thả vào cùng một tin chỉ ra một thông báo được sửa lại.

## 7. Không làm

**Đồng bộ lịch sử giữa các máy của cùng một người.** Mỗi cài đặt Shorekeeper là một **thiết bị** với khóa và DeviceId riêng ([04](04-identity-security.md)). App không có khái niệm "một người có nhiều máy".
- Nếu Huy có PC và điện thoại, người khác thấy đó là **hai** máy, phải Kết nối riêng từng máy.
- Tin Mai gửi cho PC của Huy chỉ nằm trên PC; điện thoại của Huy không thấy, và ngược lại.
- Sao lưu danh tính ([ADR-013](decisions.md)) chỉ chép **một** danh tính sang máy mới để thay máy cũ, không gộp hai máy.

Muốn đồng bộ thì phải có:
1. Khái niệm **người dùng** đứng trên các thiết bị: một khóa chính ký danh sách thiết bị của mình, và liên kết máy mới bằng mã QR.
2. Người khác nhận ra các máy đó là một người: Kết nối một lần cho cả nhóm thiết bị.
3. Mỗi tin gửi tới **mọi** máy của người nhận, kể cả tin mình gửi đi, để máy kia của mình cũng thấy.
4. Chép lịch sử cũ sang máy vừa liên kết.

Việc này đụng tới danh tính, Kết nối, quyền và mọi luồng chat, tức là gần như một hệ thống mới (như "thiết bị liên kết" của Signal). Không làm trong đợt này; nếu cần thì lên kế hoạch riêng.

**Khác**:
- Tìm trong tin thoại (cần chuyển giọng nói thành chữ).
- Lịch sử các lần sửa.
- Cảm xúc tùy chọn hay emoji tự do.
- Chuyển hộ tin của người đã rời nhóm ([ADR-016](decisions.md)).

## 8. Cần chốt

Chốt ngày 2026-10-31.

| | Câu hỏi | Chốt |
|---|---|---|
| Q1 | Sửa tin trong bao lâu? | **15 phút** sau khi tới (lâu hơn thu hồi 5 phút vì sửa không làm mất tin); còn chờ gửi thì lúc nào cũng được |
| Q2 | Bộ cảm xúc | **6 cái cố định** 👍 ❤️ 😂 😮 😢 🙏 |
| Q3 | Bị @nhắc thì có thông báo khi đã Tắt thông báo nhóm? | **Có**, như Messenger / Zalo |
| Q4 | Ai được dùng **@mọi người**? | **Mọi thành viên** (nhóm nhỏ trong LAN); có thể giới hạn cho Host sau |
| Q5 | Thả cảm xúc vào tin của mình thì có thông báo? | **Có, một thông báo cho mỗi tin**: thêm người thả thì sửa thông báo đó khi chưa xem, một người đổi nhiều lần chỉ tính một (§6) |
| Q6 | Thứ tự làm | C6a → C6b → C6c → C6d như §1 |

## 9. Rủi ro

| Rủi ro | Cách xử lý |
|---|---|
| Máy bản cũ trong nhóm bỏ tin ký `/2` | Không gửi tin `/2` cho máy chưa có `chat2`; người gửi thấy trạng thái `old` với người đó (§2) |
| Người chuyển hộ sửa `replyTo` / `mentions` | Nằm trong phần được ký `/2` |
| Giả lệnh sửa, cảm xúc hay nhắc tên của người khác | Đều ký bằng khóa tác giả; lệnh sửa phải khớp `authorId` của tin gốc |
| Spam @mọi người hay cảm xúc | Chung giới hạn 20 tin/giây mỗi máy ([16 §3.1](16-group-chat.md)); Tắt thông báo vẫn chặn được @mọi người nếu Q4 đổi thành chỉ Host |
| Tìm chậm khi lịch sử dài | Bắt đầu bằng `LIKE` trên cột đã chuẩn hóa; đo, chuyển FTS5 nếu cần |
| Trích dẫn tin quá cũ, máy mình không còn | Hiện "Tin nhắn không còn" thay vì để trống |
