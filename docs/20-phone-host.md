# 20 — Điện thoại làm Host nhóm (H1–H3)

> Trạng thái: **H1–H3 xong** (2026-11-02), theo đề xuất (§6); kết quả ở §7. Nối tiếp [14 §1.1](14-android.md): bản đầu để "Host nhóm" lại vì điện thoại hay ngủ, hay đổi mạng.

## 1. Mục tiêu

| Làm | Không làm |
|---|---|
| Tạo nhóm trên điện thoại; điện thoại là Host như PC | Chuyển Host sang máy khác (PC cũng chưa có) |
| Duyệt / từ chối người xin vào, cả từ thông báo khi app ở nền | Bridge, Subnet Probe trên điện thoại |
| Mời, loại thành viên, đổi tên, "Chỉ nhắn khi Host online", đóng nhóm | Đổi cách nhóm hoạt động (giao thức giữ nguyên, [07](07-groups.md)) |
| Giữ điện thoại online đủ để nhóm không "Host offline" mãi | Hứa "luôn online" như PC: máy của hãng vẫn có thể đóng app |

## 2. Hiện nay

- **Lõi đã đủ.** `GroupService` (dùng chung) có tạo, duyệt, từ chối, mời, loại, đổi tên, `SetHostOnlyChatAsync`, đóng nhóm, ký danh sách, báo ngay cho thành viên. Presence của máy giới thiệu nhóm mình làm Host. Sao lưu danh tính ([ADR-013](decisions.md)) có cả nhóm, nên đổi điện thoại không mất nhóm.
- **Điện thoại thiếu giao diện.** Tab Nhóm chỉ có: xin vào, nhận lời mời, gửi file / chữ cho nhóm, chat, rời. Không có nút Tạo nhóm, và cũng không có chỗ duyệt, mời, loại, sửa, đóng. Nếu khôi phục bản sao lưu có nhóm do máy đó làm Host lên điện thoại, nhóm vẫn chạy nhưng không quản lý được.
- **Online.** Không bật "Chạy nền" thì khi app ở nền, Android dừng mạng (Doze): Host không trả lời. Bật thì `TransferService` (`connectedDevice` + `WifiLock` + `WakeLock`) giữ được qua Doze; máy OPPO cần thêm quyền chạy nền của hãng; Nubia (ZTE) còn chặn tự chạy lại sau khi khởi động máy ([14 A4](14-android.md)).

## 3. Host vắng thì sao

| Việc | Cần Host online? |
|---|---|
| Chat nhóm, gửi file trong nhóm | Không (trừ nhóm "Chỉ nhắn khi Host online") |
| Xin vào, được duyệt, nhận lời mời | **Có** |
| Rời nhóm | Không ở phía mình; người khác biết khi Host online |
| Người bị loại không nhắn / tải được nữa | Cần Host báo ([07 §4.1](07-groups.md)); thành viên chuyển tiếp cho nhau được |
| Thấy địa chỉ của nhau khác subnet | **Có** |

Nên điện thoại làm Host được khi nó online **phần lớn thời gian mọi người cần**: nhóm trong một phòng, nhóm tạm cho một buổi, người dùng không có PC.

## 4. Các bước

### H1 — Giữ Host online (Android)

- Tạo nhóm đòi "Chạy nền" đang bật (§6 Q1). Đang tắt thì hộp Tạo nhóm hỏi bật luôn, đi qua đúng đường bật hiện có (xin miễn tối ưu hóa pin, nhắc cài đặt của hãng).
- Đang làm Host mà tắt "Chạy nền": hỏi lại, nói rõ nhóm sẽ thường "Host offline" (§6 Q2).
- Thông báo thường trực ghi thêm "Host 2 nhóm" bên cạnh việc đang chuyển.
- Giới hạn số nhóm làm Host trên điện thoại (§6 Q5).
- Kiểm: thành viên PC vẫn gọi được Host là Nubia sau 30 phút Doze sâu (`dumpsys deviceidle force-idle`), sau khi tắt / bật Wi-Fi (đổi IP), sau khi Android đóng app rồi mở lại.

### H2 — Màn hình Host trên điện thoại

- Tab Nhóm: nút **Tạo nhóm** (tên, ô "Chỉ nhắn khi Host online", §6 Q3).
- Thẻ nhóm mình làm Host, như PC:
  - người xin vào: [Duyệt] [Từ chối], chấm đỏ ở tab Nhóm (như Tin nhắn);
  - [Mời…]: chọn người đang online chưa trong nhóm;
  - ⋮: **Sửa nhóm…** (tên, "Chỉ nhắn khi Host online"), **Đóng nhóm** (hỏi lại);
  - Thành viên: ⋮ của người đó → **Loại khỏi nhóm** (hỏi lại), cạnh "Đặt tên gợi nhớ…".
- Thông báo khi có người xin vào lúc app ở nền, có nút [Duyệt] [Từ chối] (§6 Q4); bấm vào thông báo thì mở tab Nhóm.
- Thẻ người ở Xóm: **Mời vào nhóm…** như PC (chỉ khi người đó online và còn nhóm để mời).
- Chữ dùng lại của PC; khóa mới thêm vào `en.json`.

### H3 — Thử thật

- Ba máy: **Nubia làm Host**, PC và OPPO (hay A07) làm thành viên.
- Tạo → PC xin vào → duyệt từ thông báo trên màn hình khóa → OPPO được mời từ Xóm → chat, gửi file trong nhóm.
- Loại OPPO → PC và OPPO thấy ngay; OPPO không tải được file nhóm nữa.
- Bật "Chỉ nhắn khi Host online", khóa màn hình Nubia 30 phút: thành viên vẫn nhắn được (Host vẫn online qua Doze).
- Tắt Wi-Fi của Nubia → thành viên thấy "Host offline" → bật lại → hết trong khoảng 15 giây.
- Đổi tên, đóng nhóm. Dọn nhóm thử sau khi xong.

## 5. Rủi ro

| Rủi ro | Cách xử lý |
|---|---|
| Máy của hãng đóng app dù có foreground service (ColorOS), hay chặn tự chạy sau khởi động (ZTE) | Nhắc cài đặt của hãng như "Chạy nền"; nhóm vẫn chạy được khi Host offline (chat, file), chỉ việc vào / ra phải chờ |
| Tốn pin: mỗi thành viên long-poll Host mỗi ~20 giây | Giới hạn số nhóm (§6 Q5); long-poll gần như không tốn CPU khi không có gì đổi. Đo pin 1 giờ với nhóm 3 người ở H3 |
| Điện thoại đổi IP | Thành viên tìm lại Host qua discovery như với PC; thử ở H1 |
| Mất điện thoại | Sao lưu danh tính có nhóm; không có sao lưu thì nhóm mất như PC mất ổ cứng |
| "Chỉ nhắn khi Host online" làm nhóm bị khóa mỗi khi điện thoại vắng | Mặc định tắt cho nhóm tạo trên điện thoại, ô có dòng giải thích (§6 Q3) |

## 6. Đã chốt (2026-11-02, theo đề xuất)

| # | Câu hỏi | Kết luận |
|---|---|---|
| Q1 | Giữ điện thoại online khi làm Host bằng gì? | **Đòi bật "Chạy nền"** khi tạo nhóm: một công tắc, một thông báo, đã có sẵn đường xin quyền pin. Cách khác: tự chạy service riêng khi đang làm Host (thêm một lý do chạy nền, người dùng khó hiểu vì sao có thông báo) |
| Q2 | Đang làm Host mà tắt "Chạy nền"? | **Hỏi lại rồi cho tắt**; nhóm vẫn còn, chỉ thường "Host offline" |
| Q3 | "Chỉ nhắn khi Host online" mặc định cho nhóm điện thoại tạo? | **Tắt** (như PC), ô có dòng "điện thoại ngủ thì nhóm không nhắn được" |
| Q4 | Có người xin vào lúc app ở nền? | **Thông báo có [Duyệt] [Từ chối]**, như thông báo file đến có [Tải] |
| Q5 | Điện thoại làm Host tối đa bao nhiêu nhóm? | **3 nhóm** (PC 10); mỗi nhóm vẫn tối đa 100 người |
| Q6 | Chuyển Host sang máy khác (vd. sang PC khi điện thoại không còn dùng)? | **Để sau**, làm riêng cho cả PC và điện thoại; cần thêm giao thức |

## 7. Đã làm (2026-11-02)

Chỉ app điện thoại đổi; Engine và giao thức giữ nguyên.

**Tab Nhóm** (`GroupsTabViewModel`, `GroupsPage`)
- **Tạo nhóm** cạnh "Nhóm của bạn". Đã làm Host 3 nhóm thì báo "Điện thoại làm Host tối đa 3 nhóm." (Q5). "Chạy nền" đang tắt thì hỏi "Bật Chạy nền?" trước, bật bằng đúng công tắc ở Cài đặt (kèm hỏi miễn tối ưu hóa pin) rồi mới hỏi tên (Q1).
- Hộp hỏi (`QuestionViewModel`) có thêm ô đánh dấu (dùng cho "Chỉ nhắn khi Host online", mặc định không tích, câu nói rõ điện thoại ngủ thì nhóm không nhắn được: Q3) và danh sách chọn (chọn nhóm khi mời từ Xóm).
- Thẻ nhóm mình làm Host: dòng xin vào với [Duyệt] [Từ chối], dòng "Đã mời …, chờ đồng ý"; nút [Mời…] mở hộp chọn người đang online chưa trong nhóm (`InviteViewModel`); ⋮ có **Sửa nhóm…** và **Đóng nhóm**; ⋮ của từng thành viên có **Loại khỏi nhóm**. Chữ và câu hỏi lại như PC.
- Chấm đỏ ở tab Nhóm khi có người xin vào hay có lời mời.
- Xóm: ⋮ của một người online có **Mời vào nhóm…** khi còn nhóm mình làm Host mà họ chưa vào.

**Chạy nền và thông báo** (`BackgroundTransfers`, `Notifications`, `TransferService`)
- Thông báo "… xin vào nhóm" (kênh "Xin vào nhóm") có [Duyệt] [Từ chối], chạy qua `TransferService` nên dùng được cả khi Android đã đóng app (Q4); tự mất khi yêu cầu được trả lời ở bất cứ đâu.
- Thông báo thường trực ghi thêm "· Host 1 nhóm".
- Tắt "Chạy nền" khi đang làm Host thì hỏi "Tắt Chạy nền?" (Q2); trong lúc hỏi và khi bấm Hủy, công tắc hiện lại "bật". Lần đầu chỉ báo lại giá trị cũ thì công tắc vẫn hiện tắt; nay công tắc giữ giá trị riêng và được đặt lại thật.

**Đã thử thật** (Nubia Z2577 làm Host, PC làm thành viên; OPPO không cắm nên chỉ 2 máy):
- Tạo nhóm khi Chạy nền tắt: hỏi bật → hộp miễn pin của hệ thống (đã từ chối) → hộp tên có ô "Chỉ nhắn khi Host online" → nhóm hiện "Host: bạn", PC thấy nhóm trong "Nhóm trong mạng".
- PC xin vào: Nubia hiện thông báo có [Duyệt] [Từ chối], dòng trong thẻ, chấm đỏ, dòng nhắc. Duyệt trong thẻ và duyệt từ khay thông báo đều cho PC vào (lần đầu chạm vào thân thông báo nổi, không trúng nút, nên chỉ mở app).
- Loại PC: PC báo ngay "Bạn không còn ở trong nhóm". Mời lại từ ⋮ ở Xóm (chọn nhóm) → PC có lời mời → Đồng ý → vào lại. [Mời…] khi không còn ai online ngoài thành viên: "Không có ai đang online…", nút Mời tắt.
- Sửa nhóm (đổi tên, bật "Chỉ nhắn khi Host online"): PC thấy tên mới.
- Ép Doze sâu (`dumpsys deviceidle force-idle`) 3 phút với Chạy nền bật: PC vẫn thấy Host online, gửi tin vào nhóm "Chỉ nhắn khi Host online" được, "Đã nhận 1/1". Đã trả lại (`unforce`, `battery reset`).
- Đóng nhóm: nhóm mất ở Nubia, PC báo không còn trong nhóm. Đã xóa hội thoại thử ở hai máy và tắt lại Chạy nền.
- Chưa thử: ba máy; Doze 30 phút và tắt màn hình thật (cần mở khóa); duyệt trên màn hình khóa; tắt / bật Wi-Fi; đo pin 1 giờ; khởi động lại máy (Nubia chặn tự chạy, [14 A4](14-android.md)).
