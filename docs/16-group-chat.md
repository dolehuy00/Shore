# 16 — Chat nhóm (C5)

> Trạng thái: **C5a–C5d xong** (2026-10-21: engine gửi thẳng, ký, đã nhận / đã xem theo người, thu hồi; chuyển hộ và tin cũ; giao diện máy tính và điện thoại, thông báo), C5e kế hoạch. Nối tiếp [15](15-chat.md) (C1–C4, hội thoại 1-1) trên nền Nhóm ở [07](07-groups.md).
>
> Đã chốt (2026-10-21), chi tiết ở §9:
> - thành viên **chuyển tin hộ** khi người gửi offline;
> - **chỉ người gửi** thấy ai đã xem tin của mình;
> - **tắt thông báo** được theo từng nhóm;
> - **người mới vào thấy tin cũ**.

## 1. Mục tiêu

Thành viên một **Nhóm** nhắn cho cả nhóm văn bản và tin nhắn thoại, trên máy tính và điện thoại. Không có server: tin đi thẳng giữa các máy thành viên, và thành viên nào đang có tin thì chuyển được cho người còn thiếu.

| Có | Không (để sau hoặc không làm) |
|---|---|
| Mỗi Nhóm mình là Host hay thành viên có một hội thoại nhóm | Nhóm chat tự lập (chọn vài người), tách khỏi Nhóm |
| Văn bản, thoại tối đa 5 phút, như 1-1 | @nhắc tên, cảm xúc, trả lời trích dẫn, sửa tin |
| Người gửi offline thì thành viên khác chuyển hộ; tin ký bằng khóa của người gửi | Chuyển hộ file (file chỉ tải từ người gửi, như hiện nay) |
| Đã nhận / đã xem theo từng người, chỉ người gửi thấy | Đồng bộ lịch sử giữa các máy của cùng một người |
| Người mới vào thấy tin 30 ngày gần nhất | |
| Thu hồi tin và file của mình trong 5 phút; xóa trên máy mình | |
| Tắt thông báo theo nhóm | |
| File gửi cho nhóm hiện thành thẻ trong hội thoại nhóm | |

## 2. Nguyên tắc

- Hội thoại nhóm gắn với `GroupId`. Danh sách thành viên là danh sách mỗi máy đang giữ (`GroupStore`, long-poll tới Host, [07 §4](07-groups.md)).
- **Host không phải server**: Host chỉ là một thành viên như mọi người. Host offline thì thành viên vẫn nhắn cho nhau, dựa trên danh sách đã lưu, như gửi file.
- Nhắn trong nhóm **không cần Kết nối từng cặp**. Mọi route chat nhóm chỉ trả lời người gọi đang là thành viên của đúng nhóm đó, theo danh sách của máy được gọi (`GroupStore.IsMember(groupId, caller)`). Người đã rời, bị loại, hay nhóm đã đóng thì bị từ chối.
- **Tin không phụ thuộc vào ai chuyển tới**: mỗi tin mang chữ ký của tác giả (§3.5). Máy nhận kiểm chữ ký, nên một thành viên chuyển hộ không sửa hay giả được tin của người khác.
- GPO `DisableGroups = 1` tắt luôn chat nhóm: không gửi, các route trả `404`, hội thoại nhóm không hiện.

## 3. Engine

### 3.1 Gửi

- **Tác giả gửi thẳng tới từng thành viên** (fan-out). Mỗi tin có ID do tác giả tạo (UUIDv7, như 1-1), lưu một lần trong `ChatGroupMessages`. Mỗi thành viên khác (theo danh sách lúc gửi) có một dòng `ChatGroupDeliveries` ở trạng thái `pending`.
- Hàng đợi **theo từng máy nhận** (`GroupChatService`, riêng với tin 1-1): tin tới một máy vẫn đúng thứ tự, và một người offline không chặn người khác. Máy online thì gửi ngay; offline thì gửi khi `PeerDirectory` báo máy đó online; cứ 30 giây thử lại với máy đang online. Như mọi lần gọi máy khác, `PeerClient` thử cả những địa chỉ từng kết nối được.
- Người nhận đang offline và tác giả tắt máy trước khi họ online: thành viên khác chuyển hộ (§3.6).
- Thoại: âm thanh đi trong request như 1-1. Nhóm lớn thì một tin thoại 2 MB nhân với số người; chấp nhận được trong LAN, gửi tối đa 4 người cùng lúc.
- **Danh sách đổi khi tin còn chờ**:
  - Tin chờ cho người đã rời hay bị loại thì bỏ.
  - Nhóm đóng thì bỏ hết, và hội thoại chỉ còn đọc.
- **Danh sách các máy lệch nhau một lúc.** Người nhận chưa biết tác giả vừa vào nhóm thì trả `403` mã `not_member`. Mã này được thử lại trong 2 phút (`RefusedAt`), vì long-poll cập nhật danh sách trong vòng 20 giây; quá 2 phút mới thành `failed` với người đó.
- **Bản cũ**:
  - Presence thêm `caps: "chat-group"`. Máy nghe trực tiếp mà không có cap này thì không gửi, và hiện "Bản cũ" với người đó.
  - Máy không biết `caps` (qua Bridge hay probe) thì vẫn gửi. Bản cũ trả route lạ như người lạ (`403` mã `not_trusted`), nên mã đó thành `old`. Vì vậy route nhóm mở cho mọi máy không bị chặn và tự kiểm thành viên, để trả `not_member` riêng.
- **Giới hạn**: 20 tin/giây mỗi máy gửi, tính chung tin 1-1 và tin nhóm.

### 3.2 Thứ tự

- Hiển thị theo giờ gửi của tác giả (`sentAt`), cùng giờ thì theo ID, như 1-1.
- Trong nhóm, tin của nhiều người xen nhau, nên đồng hồ lệch làm câu trả lời có thể nằm trên câu hỏi. Vì vậy `sentAt` của tin mới **luôn sau tin mới nhất đã thấy trong nhóm ít nhất 1 ms**, kể cả khi đồng hồ máy mình chậm hơn. Mọi máy xếp giống nhau.
- `sentAt` nằm trong phần được ký, nên người chuyển hộ không đổi được. Máy nhận **không** kẹp `sentAt` như 1-1, vì kẹp thì mỗi máy xếp một kiểu. Thay vào đó, máy nhận bỏ tin có `sentAt` muộn hơn giờ của nó quá 1 ngày (đồng hồ hỏng hay cố tình đẩy tin lên đầu).

### 3.3 Đã nhận / đã xem theo từng người

- Phía tác giả, mỗi tin có trạng thái riêng với từng thành viên: `pending`, `delivered`, `read`, `failed`, `old` (bản cũ).
- Trạng thái chung của tin dưới bong bóng:
  - "Chờ gửi" khi chưa tới ai;
  - "Đã nhận 4/7" khi đã tới một phần hoặc tất cả;
  - "Đã xem 3/7" khi đã có người xem.
- `delivered` là điều tác giả **biết**. Tin tới qua người chuyển hộ thì tác giả chỉ biết khi gặp lại người đó: lần gửi thẳng sau đó nhận `204` (đã có), hoặc nhận receipt.
- **Đã xem**: mở hội thoại nhóm (đúng hội thoại, cửa sổ ở trước, như 1-1) thì gửi `POST /chat/groups/{id}/receipts { readUpTo }` **thẳng cho tác giả** của những tin vừa đọc, mỗi tác giả một receipt với tin cuối của họ đã đọc.
  - Chỉ tác giả biết ai đã xem tin của mình.
  - Receipt không được chuyển hộ; chờ gửi khi offline, giữ cái mới nhất cho mỗi cặp (nhóm, tác giả).

### 3.4 Thu hồi, xóa, thống kê

- **Thu hồi**: 5 phút như 1-1 (tin còn chờ với mọi người thì lúc nào cũng được).
  - Tác giả ký một **lệnh thu hồi** (§3.5) và gửi thẳng cho mọi thành viên bằng `POST /chat/groups/{id}/recalls`, chờ gửi khi offline.
  - Lệnh thu hồi cũng được chuyển hộ như tin (§3.6). Không vậy thì người chuyển hộ có thể mang một tin đã thu hồi tới người chưa biết.
  - Máy đã thu hồi một tin thì giữ dòng "đã thu hồi" kèm chữ ký lệnh, không bao giờ nhận lại nội dung tin đó.
- **Xóa**: từng tin hoặc cả hội thoại nhóm, chỉ trên máy mình.
  - Tin đã xóa để lại một dấu xóa (ID, không nội dung), để chuyển hộ hay tin cũ không đưa nó về lại.
  - Xóa hội thoại ẩn các thẻ file tới lúc đó, như 1-1 (`ChatCleared`), và bỏ mọi tin có `sentAt` từ lúc đó trở về trước khi chuyển hộ tới.
  - Máy đã xóa không chuyển hộ tin đã xóa.
- **Thống kê**: tin nhóm gửi đi đếm một lần, không nhân số người; tin nhận đếm như 1-1, kể cả tin nhận qua người chuyển hộ.

### 3.5 Chữ ký

- Khóa thiết bị là ECDSA P-256, và `DeviceId` là SHA-256 của khóa công khai (`DeviceId.FromSubjectPublicKeyInfo`). Mỗi tin nhóm mang `authorKey` (khóa công khai của tác giả) và `sig`.
- Máy nhận kiểm:
  1. `DeviceId.FromSubjectPublicKeyInfo(authorKey)` đúng bằng `authorId`;
  2. `sig` đúng với `authorKey`.

  Như vậy không cần đã Kết nối hay từng gặp tác giả.
- Phần được ký là chuỗi byte cố định: tiền tố phiên bản (`shorekeeper-group-chat/1`), rồi lần lượt `groupId`, `id`, `authorId`, `sentAt` (ms), `kind`, `text`, `durationMs`, `wave`, SHA-256 của `audio`. Mỗi trường có độ dài đi trước, để không ghép nhầm hai trường thành một.
- **Lệnh thu hồi** ký `shorekeeper-group-recall/1`, `groupId`, `id`, `authorId`, `sentAt` của tin: người chuyển hộ không đổi được tin đã thu hồi nằm ở đâu.
- Tin ký sai hay khóa không khớp thì bỏ, và ghi log người chuyển tới.

### 3.6 Chuyển hộ và tin cũ

Một cơ chế cho cả hai việc: **thành viên kéo tin còn thiếu từ thành viên khác** (pull). Nhờ vậy máy chỉ nhận những tin nó chưa có, và người mới vào lấy được tin cũ.

- Mỗi máy đánh số thứ tự các tin nhóm nó lưu (`Seq`, một dãy chung cho mọi nhóm, tăng mỗi khi thêm tin hay thu hồi).
- `GET /chat/groups/{id}/index?after={seq}` trả danh sách gọn, tối đa 500 mục: `seq`, `id`, `authorId`, `sentAt`, đã thu hồi hay chưa. Chỉ có tin trong cửa sổ lịch sử: **30 ngày** và **1.000 tin** gần nhất mỗi nhóm.
- Máy kéo so với những gì nó có rồi gọi `POST /chat/groups/{id}/fetch { ids }`. Nó bỏ qua tin đã có, tin đã xóa ở đây (dấu xóa), tin của chính mình, và tin từ trước lúc "Xóa hội thoại". Tin đang hiện mà ở người kia đã thu hồi thì vẫn hỏi, để lấy lệnh thu hồi.
- `fetch` trả tối đa 50 tin, nguyên chữ ký, kèm âm thanh thoại; tin đã thu hồi thì chỉ có lệnh thu hồi đã ký (kèm tác giả, khóa, giờ gửi). Quá 2 MB âm thanh thì dừng; máy kéo hỏi lại phần còn thiếu, tới khi người kia không đưa thêm được gì (tin đã xóa, file ghi âm mất).
- Máy kéo nhớ con trỏ `after` cho mỗi cặp (nhóm, thành viên), chỉ tiến lên khi đã lấy xong trang đó. Lần sau chỉ hỏi phần mới, thường là trống. `next` vượt qua cả những tin không được đưa (đã xóa, ngoài cửa sổ).
- **Khi nào kéo** (`PullAllAsync`, một luồng mỗi thành viên như gửi):
  - khi app mở, và 5 phút một lần: từ mọi thành viên đang online;
  - khi một thành viên vừa online (`PeerDirectory`);
  - khi danh sách nhóm đổi, trong đó có lúc mình vừa được duyệt vào nhóm.

  Với mỗi thành viên, các nhóm chung được kéo lần lượt; không gọi được thì thôi người đó tới lần sau.
- **Người mới vào nhóm**: con trỏ bắt đầu từ 0, nên lần kéo đầu lấy cả cửa sổ lịch sử. Kéo lần lượt từ từng thành viên online; thành viên sau thường chỉ còn vài tin thiếu.
- **Tin cũ hay tin bị lỡ**: lần kéo đầu tiên của một nhóm ghi lại giờ đó (`ChatGroups.HistoryBefore`). Tin chuyển hộ tới có `sentAt` trước giờ đó là tin cũ: đã đọc, không thông báo. Tin sau giờ đó (tác giả offline khi gửi) thì như tin gửi thẳng: chưa đọc, có thông báo.
- **Ai được nhận qua chuyển hộ**:
  - Người chuyển (người gọi) phải đang là thành viên.
  - Tin phải ký đúng.
  - Tác giả có thể đã rời nhóm: tin cũ của họ vẫn hiện, như ở máy những người có mặt lúc đó.
- Người chuyển hộ chỉ đưa tin nó đã nhận và kiểm được, nên không tạo được tin giả. Tin trong lịch sử vẫn hiện tên tác giả lúc gửi (lưu kèm tin), kể cả khi người đó đã rời.

## 4. API

| Method | Path | Quyền | Ghi chú |
|---|---|---|---|
| `POST` | `/chat/groups/{groupId}/messages` | Thành viên `groupId` | Một tin đã ký → `204`; đã có ID đó (hay dấu xóa, từ C5b) thì vẫn `204`, không làm gì; ký sai `400`. Người gọi là tác giả. Không phải thành viên: `403` `not_member` |
| `POST` | `/chat/groups/{groupId}/receipts` | Thành viên `groupId` | `{ readUpTo }`: tin **của máy này** mà người gọi đã đọc tới |
| `POST` | `/chat/groups/{groupId}/recalls` | Thành viên `groupId` | `{ recalls: [{ id, sig }] }`, lệnh thu hồi đã ký, tối đa 500 |
| `GET` | `/chat/groups/{groupId}/index?after=` | Thành viên `groupId` | Danh sách gọn (§3.6), `{ entries, next, more }` |
| `POST` | `/chat/groups/{groupId}/fetch` | Thành viên `groupId` | `{ ids }`, tối đa 50 → `{ messages, recalls }` đã ký |

Tin nhóm như tin 1-1, thêm các trường ký:

```json
{
  "id": "019a05f1c2d87b3e9a41f6c0d2e5b7a9",
  "authorId": "qkcdhcrvov4iqfr5bpuvgzah6zkuytiaxye35b36tmqqztfr3uwq",
  "authorName": "PC · MSI",
  "authorKey": "base64 SubjectPublicKeyInfo",
  "sentAt": "2026-10-21T08:15:30.123Z",
  "kind": "text",
  "text": "Chiều nay họp lúc 3 giờ nhé",
  "sig": "base64 ECDSA P-256"
}
```

- Dùng **route mới** chứ không thêm `groupId` vào `/chat/messages`: bản cũ bỏ qua trường lạ, sẽ hiểu tin nhóm thành tin 1-1. Với route mới, bản cũ trả `403` `not_trusted` như với mọi route lạ.
- `authorName` không nằm trong phần ký, chỉ để hiện tên khi chưa biết người đó. Tên trong danh sách thành viên được ưu tiên.
- Quyền: route mở cho mọi máy không bị chặn (`AnyPeer`); handler kiểm người gọi là thành viên của đúng nhóm đó (`GroupStore.IsMember`) và trả `not_member` nếu không. Trusted thôi thì không đủ.
- Giới hạn: `index` và `fetch` tối đa 10 lần/giây mỗi người gọi.

## 5. Lưu trữ

Migration `0011_group_chat.sql` cho C5a–C5c (chưa phát hành nên C5b, C5c sửa thẳng vào đó). Từ C5c nó đã chạy trên máy thật, nên C5d–C5e thêm migration của mình ([09](09-data-storage.md)):

- `ChatGroups` (`GroupId`, `Name`, `ClearedAt`, `HistoryBefore`, `Muted`): giữ tên nhóm cho hội thoại sau khi rời, bị loại hay nhóm đóng (bảng `Groups` khi đó đã xóa nhóm), lúc "Xóa hội thoại", lúc kéo nhóm lần đầu (§3.6), và "Tắt thông báo".
- `ChatGroupMessages` (khóa `GroupId` + `Id`):
  - nội dung như `ChatMessages`: `AuthorId`, `AuthorName`, `Outgoing`, `Kind`, `Text`, `VoicePath`, `DurationMs`, `Wave`, `SentAt`, `ReceivedAt`, `State` (tin nhận: `unread` → `read`);
  - thêm `AuthorKey`, `Sig`, `RecallSig`, `Deleted` (dấu xóa) và `Seq` (§3.6).
- `ChatGroupDeliveries` (khóa `GroupId` + `Id` + `PeerId`): `State` của tin mình gửi với từng người.
- `ChatGroupReceipts` (`GroupId`, `AuthorId`, `ReadUpTo`) và `ChatGroupRecalls` (`GroupId`, `PeerId`, `Id`): còn chờ gửi thẳng.
- `ChatGroupCursors` (`GroupId`, `PeerId`, `After`): đã kéo tới đâu ở mỗi thành viên.
- `InboxOffers.GroupId`: offer nhận qua nhóm hiện trong hội thoại nhóm thay vì hội thoại 1-1 với người gửi. Offer nhận trước migration không có giá trị này, nên vẫn ở 1-1.
- Thẻ file của nhóm ẩn sau "Xóa hội thoại" theo `ChatGroups.ClearedAt` (C5e).
- Ngoài cửa sổ lịch sử (30 ngày / 1.000 tin) thì tin vẫn ở máy mình, chỉ không đưa cho người khác kéo.

## 6. Giao diện

### 6.1 Máy tính

- **Danh sách hội thoại**:
  - Nhóm nằm xen giữa người, theo giờ tin cuối; biểu tượng nhóm; dòng cuối "Minh: …" / "Bạn: …"; số chưa đọc. Nhóm tắt thông báo có biểu tượng chuông gạch.
  - Nhóm chưa nhắn lần nào nằm cùng các liên hệ chưa nhắn ("Chưa có tin nhắn").
- **Đầu hội thoại**: tên nhóm, "12 thành viên · 5 online" (kể cả mình), không có chấm online. ⋮ gồm **Thành viên** (bảng nổi góc trên bên phải: tên, Online/Offline, Host), **Tắt thông báo** / **Bật thông báo** và **Xóa hội thoại…**. Nhóm tắt thông báo có chuông gạch cạnh giờ trong danh sách; số chưa đọc vẫn tính vào "Tin nhắn".
- **Bong bóng**:
  - Tin người khác có tên người gửi (màu riêng theo `DeviceId`) trên tin đầu mỗi cụm, và một vòng tròn chữ cái đầu bên trái.
  - Tin của mình như 1-1, dưới tin cuối hiện trạng thái chung (§3.3).
- **Chuột phải** tin của mình có thêm **Chi tiết** (cùng bảng nổi đó): từng thành viên với Đã xem / Đã nhận / Chờ gửi / Bản cũ / Không gửi được, kèm [Gửi lại] cho người lỗi. [Gửi lại] dưới bong bóng gửi lại cho mọi người lỗi.
- Vào nhóm mà đang kéo tin cũ thì đầu hội thoại có dòng "Đang lấy tin cũ…".
- **Ô soạn**:
  - Như 1-1. 📎 / Ctrl+V / kéo thả gửi một offer cho cả nhóm (mọi thành viên khác, có `groupId`), hiện thành thẻ trong hội thoại nhóm (C5e; tới lúc đó 📎 tắt trong nhóm).
  - Ô soạn khóa khi rời, bị loại, nhóm đóng ("Bạn không còn trong nhóm này"), hoặc nhóm chỉ có mình.
- **Trang Nhóm**: thêm nút **Nhắn tin** trên mỗi nhóm.
- **Toast**: "Minh · Build QA: …", trừ nhóm đã tắt thông báo.

### 6.2 Điện thoại

- Như máy tính, trong tab Tin nhắn (cùng `ChatViewModel`, `ShowsGroups`). Nhấn giữ tin của mình có **Chi tiết**; "Chi tiết" / "Thành viên" là thẻ nổi trên đầu hội thoại, nút Back đóng nó trước rồi mới đóng hội thoại. Thẻ nhóm ở tab Nhóm có nút 💬 cạnh [Gửi file…].
- **Thông báo** (`ChatNotifications`, mỗi nhóm một thông báo, tag `group:{id}`):
  - Kiểu tin nhắn nhóm (`MessagingStyle` với `setGroupConversation`): tên nhóm là tiêu đề, mỗi dòng có tên người gửi (tên liên hệ, hay tên trong danh sách thành viên).
  - **Trả lời** trên thông báo gửi vào nhóm (nếu mình còn trong nhóm và còn người khác); trả lời hay **Đánh dấu đã đọc** là đã đọc, thông báo tự đi. Chạm thì mở đúng hội thoại nhóm.
  - Nhóm đã tắt thông báo thì chỉ còn số chưa đọc. Đang mở đúng nhóm đó thì không báo.
  - Tin cũ kéo về khi mới vào nhóm không báo, và không tính chưa đọc.
- Widget đếm cả tin nhóm chưa đọc.
- Điện thoại chỉ làm thành viên, không làm Host ([14](14-android.md)); chat nhóm không cần Host nên điện thoại nhắn, chuyển hộ và cho kéo tin như máy tính.

### 6.3 File trong hội thoại nhóm

- Offer có `groupId` (gửi từ trang Nhóm, từ Chia sẻ, hay từ hội thoại nhóm) hiện thành thẻ trong hội thoại nhóm, cả bên gửi lẫn bên nhận, như [15 §6.3](15-chat.md).
- Thẻ file **không** được chuyển hộ hay kéo về như tin cũ: file chỉ tải được từ người gửi, nên thẻ chỉ có ở người nhận được offer.
- Thu hồi thẻ báo cho mọi thành viên.
- **Ảnh tự tải** chỉ với người gửi là liên hệ đã Kết nối. Thành viên chưa Kết nối thì bấm Tải, giống "Tự nhận file" ([07 §7](07-groups.md)).

## 7. Giai đoạn

| Giai đoạn | Nội dung | Xong khi |
|---|---|---|
| **C5a** Engine gửi thẳng ✅ | Migration, ký và kiểm chữ ký, route `messages` / `receipts` / `recalls`, fan-out, chờ gửi theo từng người, chống trùng, thứ tự (§3.2), đã nhận / đã xem theo người, thu hồi, `caps: "chat-group"`, `not_member` thử lại 2 phút | Test engine 3 máy loopback (bảng dưới, `GroupChatTests`), cả MiniHttpServer lẫn Kestrel |
| **C5b** Chuyển hộ & tin cũ ✅ | `Seq`, `index` / `fetch`, con trỏ, kéo khi online / khi vào nhóm, cửa sổ 30 ngày / 1.000 tin, dấu xóa, thu hồi qua chuyển hộ | Test engine (bảng dưới, `GroupChatTests`), cả MiniHttpServer lẫn Kestrel |
| **C5c** Máy tính ✅ | Danh sách + hội thoại nhóm, tên người gửi, Chi tiết, Tắt thông báo, nút ở trang Nhóm, toast | PC (Host) gửi tới điện thoại (thành viên), Thành viên, Chi tiết, tắt / bật thông báo, thu hồi; tin nhóm đến PC (tên, màu, toast) thử ở C5d, khi điện thoại gửi được |
| **C5d** Điện thoại ✅ | Như C5c + thông báo nhóm có Trả lời | Nhắn thật PC ↔ điện thoại: tên, màu, chữ cái đầu, "Đã xem 1/1", Chi tiết, Thành viên, tắt / bật thông báo (không còn thông báo), thông báo nhóm và Trả lời trên đó, toast nhóm trên PC, nút 💬 ở tab Nhóm |
| **C5e** File | `InboxOffers.GroupId`, gửi file từ hội thoại nhóm, thẻ, thu hồi offer nhóm | Test engine + thử ảnh / file thật |

Test engine của C5a:

- A gửi, B nhận ngay; C offline rồi online thì A gửi tiếp, đủ và đúng thứ tự.
- C bị loại: tin chờ cho C bị bỏ, và C nhắn vào thì nhận `403`.
- Người ngoài nhóm, kể cả liên hệ Trusted, gửi vào nhận `403`. Nhóm đã đóng thì cũng `403`.
- Tin có chữ ký sai, hay `authorKey` không khớp `authorId`, nhận `400`.
- Máy không có cap `chat-group` không được gửi, và có trạng thái `old`.
- Receipt từ B và C cập nhật đúng trạng thái của từng người.
- Đồng hồ A nhanh 2 phút mà B trả lời vẫn nằm sau tin của A.

Test engine của C5b:

- A gửi khi C offline rồi A tắt; C online thì kéo tin đó từ B.
- Chữ ký vẫn đúng sau khi qua tay B. B sửa chữ trong tin thì C bỏ tin.
- A thu hồi khi C offline: C kéo từ B thấy dòng "đã thu hồi", không bao giờ thấy nội dung.
- D mới vào nhóm: kéo được tin trong 30 ngày, kể cả tin của người đã rời; không có tin ngoài cửa sổ.
- C xóa một tin rồi kéo lại từ người khác thì tin không quay về.
- Kéo lần hai từ cùng người chỉ lấy phần mới.

Thử thật chỉ có 2 máy (PC làm Host, điện thoại); chuyển hộ và 3 người trở lên chỉ có trong test engine.

## 8. Rủi ro

| Rủi ro | Cách xử lý |
|---|---|
| Một thành viên cùng phe với người đã bị loại, đưa tin mới của người đó vào nhóm (chữ ký vẫn đúng) | Chấp nhận: người đó vốn đã đọc được nhóm và tự chép lại được. Máy nhận ghi log người chuyển. Muốn chặn hẳn thì cần danh sách thành viên có chữ ký của Host, để sau |
| Lịch sử chỉ có khi còn ai đó online giữ nó | Cửa sổ 30 ngày / 1.000 tin trên mọi máy thành viên, nên chỉ cần một người online |
| Kéo lịch sử từ nhiều người tốn băng thông | `index` gọn trước, `fetch` chỉ tin thiếu; kéo lần lượt từng người, không song song |
| Danh sách thành viên các máy lệch nhau ngay sau khi có người vào hay rời | `403` thử lại 2 phút (§3.1); tin chờ cho người đã rời thì bỏ |
| Nhóm đông, tin thoại tốn băng thông | Gửi tối đa 4 người cùng lúc; trong LAN 100 × 2 MB vẫn chấp nhận được |
| Nhóm đông nhiều thông báo | Tắt thông báo theo nhóm |
| Đồng hồ các máy lệch làm đảo câu hỏi / trả lời | `sentAt` luôn sau tin mới nhất đã thấy (§3.2); `sentAt` quá 1 ngày trong tương lai thì bỏ |
| "Đã nhận" với tin chuyển hộ tới muộn hơn thực tế | Tác giả chỉ biết khi gặp lại người đó (§3.3); Chi tiết ghi đúng điều tác giả biết |
| Chỉ có 2 máy thật để thử | Chuyển hộ và 3 người trở lên kiểm bằng test engine loopback |

## 9. Đã chốt (2026-10-21)

| | Câu hỏi | Chốt |
|---|---|---|
| D1 | Tác giả offline thì có ai chuyển tin hộ không? | **Có**: thành viên kéo tin thiếu từ nhau (§3.6). Mỗi tin ký bằng khóa thiết bị của tác giả (§3.5), nên người chuyển không giả hay sửa được |
| D2 | Ai thấy ai đã xem? | **Chỉ tác giả**, như 1-1; receipt gửi thẳng cho tác giả, không chuyển hộ |
| D3 | Tắt thông báo theo từng nhóm? | **Có**, trong ⋮ của hội thoại nhóm |
| D4 | Người mới vào thấy tin cũ? | **Có**: tin 30 ngày / 1.000 tin gần nhất, kéo từ thành viên đang online (§3.6) |
