# 08 — Vai trò Host & Bridge ("ai cũng làm Node")

## 1. Nguyên tắc

Không có máy chủ riêng. **Mọi client chạy cùng một app** và nhận thêm vai trò tùy hành động:

| Vai trò | Ai | Khi nào | Làm gì |
|---|---|---|---|
| **Người gửi** | Ai gửi file | Khi offer còn mở | Giao offer cho người nhận đã chọn, phục vụ file cho họ tải, kiểm tra người tải có trong danh sách |
| **Group Host** | Người tạo nhóm | Khi nhóm tồn tại và app đang chạy | Duyệt/loại thành viên, phát sự kiện, cho thành viên biết địa chỉ của nhau. **Không giữ file** |
| **Bridge** | Client nào tự bật (tùy chọn) | Tùy chọn | Registry presence công khai giúp các subnet thấy nhau tự động |

Vai trò Người gửi và Group Host **tự động** theo hành động của người dùng. Bridge là **bật thủ công** (hoặc IT bật bằng GPO).

## 2. Người gửi

- Offer mở tối đa 24 giờ (cấu hình được), lưu trong DB nên vẫn còn sau khi khởi động lại app.
- Đóng cửa sổ thì app xuống tray và vẫn phục vụ. **Thoát app** thì người nhận thấy "Người gửi offline" cho đến khi mở lại.
- Khi thoát, nếu còn người đang tải hoặc còn offer mở, UI cảnh báo: *"3 người chưa tải xong file bạn gửi. Thoát bây giờ?"*

## 3. Group Host

- Chạy trong Engine (`GroupHostService`) khi máy có ít nhất một nhóm do mình tạo.
- Mỗi thành viên giữ 1 kết nối SSE. 100 thành viên ≈ 100 kết nối nhàn rỗi, không đáng kể.
- Host offline thì thành viên vẫn gửi file cho nhau theo danh sách đã cache ([07 §4](07-groups.md)).

## 4. Bridge

### 4.1 Khi nào cần

Không bắt buộc. Nên bật khi có nhiều subnet và muốn mọi người **tự thấy nhau ngay từ lần đầu**, không phải nhập hostname.

### 4.2 Hoạt động

- Registry presence **trong RAM**: DeviceId, tên, `addrs`, `observedAddr`, status, nhóm Host. Lease 60 giây, client renew mỗi 20 giây.
- Chỉ chứa thông tin **vốn đã công khai** qua presence. Bridge **không cấp quyền gì**: máy lạ thấy nhau qua Bridge vẫn phải Kết nối mới gửi file được.
- Client long-poll thay đổi (`GET /bridge/peers?since=`), gộp lô mỗi 1 giây.
- Client biết Bridge qua multicast (`caps: ["bridge"]`), PEX (`bridges`), GPO `BridgeAddresses`, hoặc DNS SRV.
- Có thể có nhiều Bridge. Client đăng ký tối đa 3. Bridge tắt thì client vẫn còn Known Endpoints + PEX.

### 4.3 Chọn máy làm Bridge

- Máy hay bật, dây LAN, mạng Domain/Private.
- Tùy chọn "**Làm cầu nối cho mạng**" (bị khóa nếu GPO `AllowBridge = 0`).
- Tải: 300 client ≈ 15 request/giây (renew) + 300 long-poll đang chờ.
- Client ở trạng thái **Ẩn** không đăng ký Bridge.
