# 20 — Điện thoại làm Host nhóm (H1–H3)

> Trạng thái: **kế hoạch** (2026-11-02), chờ chốt §6. Nối tiếp [14 §1.1](14-android.md): bản đầu để "Host nhóm" lại vì điện thoại hay ngủ, hay đổi mạng.

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
  - người xin vào: [Duyệt] [Từ chối], badge ở tab Nhóm;
  - [Mời…]: chọn người đang online chưa trong nhóm;
  - ⋮: **Sửa nhóm…** (tên, "Chỉ nhắn khi Host online"), **Đóng nhóm** (hỏi lại);
  - Thành viên: nhấn giữ một người → **Loại khỏi nhóm** (hỏi lại).
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

## 6. Cần chốt

| # | Câu hỏi | Đề xuất |
|---|---|---|
| Q1 | Giữ điện thoại online khi làm Host bằng gì? | **Đòi bật "Chạy nền"** khi tạo nhóm: một công tắc, một thông báo, đã có sẵn đường xin quyền pin. Cách khác: tự chạy service riêng khi đang làm Host (thêm một lý do chạy nền, người dùng khó hiểu vì sao có thông báo) |
| Q2 | Đang làm Host mà tắt "Chạy nền"? | **Hỏi lại rồi cho tắt**; nhóm vẫn còn, chỉ thường "Host offline" |
| Q3 | "Chỉ nhắn khi Host online" mặc định cho nhóm điện thoại tạo? | **Tắt** (như PC), ô có dòng "điện thoại ngủ thì nhóm không nhắn được" |
| Q4 | Có người xin vào lúc app ở nền? | **Thông báo có [Duyệt] [Từ chối]**, như thông báo file đến có [Tải] |
| Q5 | Điện thoại làm Host tối đa bao nhiêu nhóm? | **3 nhóm** (PC 10); mỗi nhóm vẫn tối đa 100 người |
| Q6 | Chuyển Host sang máy khác (vd. sang PC khi điện thoại không còn dùng)? | **Để sau**, làm riêng cho cả PC và điện thoại; cần thêm giao thức |
