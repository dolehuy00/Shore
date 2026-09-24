# 01 — Tổng quan & Yêu cầu

## 1. Bối cảnh

Trong công ty, chuyển file giữa các máy (build, backup, log, installer, tài liệu) hiện đi qua USB, Zalo/Teams/email, hoặc SMB share tạo tay. Các cách này chậm, bị giới hạn dung lượng, hoặc phiền phức.

**Shorekeeper** là một app nhỏ cài trên từng máy:

- Mở lên là thấy ai đang online trong LAN, kể cả **khác subnet**.
- **Kéo thả file vào một hoặc nhiều người** → họ thấy file trong Hộp nhận → ai muốn thì tải → file được copy thẳng sang máy họ với tốc độ LAN.
- **Nhóm** để gửi nhanh cho nhiều người: ai muốn vào thì xin, người tạo nhóm duyệt.

**Trọng tâm của sản phẩm là trải nghiệm gửi file: nhanh, rõ ràng, ít bước, an toàn mặc định.**

## 2. Mục tiêu

| # | Mục tiêu |
|---|---|
| G1 | Gửi file/thư mục trong LAN **nhanh, ít thao tác**. |
| G2 | Thấy ngay đồng nghiệp đang online, không cần biết IP. |
| G3 | Hoạt động giữa **các subnet khác nhau**, **không cần server**. |
| G4 | **Người gửi quyết định gửi cho ai; người nhận quyết định có nhận không.** |
| G5 | **Chặt mặc định:** máy chưa được xác nhận tin cậy thì không gửi file, không xem được gì ngoài thông tin công khai. |
| G6 | Rớt mạng ngắn không làm hỏng việc gửi. |
| G7 | Triển khai dễ trong doanh nghiệp Windows (MSI, GPO). |

## 3. Ngoài phạm vi

- Tải tiếp sau khi thoát app / khởi động lại máy. Chỉ nối lại được trong cùng phiên chạy, với rớt mạng ngắn.
- Tự xóa file ở máy nhận. File nhận được là **bản sao vĩnh viễn**.
- Server trung tâm; kho file chung; nhóm chứa file.
- Đồng bộ thư mục, quản lý phiên bản, chat.
- Internet / ngoài mạng công ty; mobile; Linux/macOS (kiến trúc sẵn sàng, chưa phát hành).

## 4. Use case chính

| ID | Use case | Mô tả ngắn |
|---|---|---|
| UC-01 | Xem ai đang online | Mở app → "Xóm" hiển thị máy online, trạng thái, subnet. |
| UC-02 | Kết nối | A bấm [Kết nối] trên thẻ B → B chấp nhận → hai người tin cậy nhau. |
| UC-03 | **Gửi file** | Kéo thả file vào 1 hoặc nhiều người (Trusted hoặc cùng nhóm) → họ nhận thông báo. |
| UC-04 | **Nhận file** | Mở Hộp nhận → thấy file ai gửi, bao nhiêu → [Tải] / chọn file / [Bỏ qua] / để sau. |
| UC-05 | Rớt mạng | Wi-Fi rớt 10 giây → "Đang thử lại…" → tải tiếp. Rớt quá 60 giây → Lỗi → [Tải lại]. |
| UC-06 | Thu hồi | Người gửi thu hồi offer → ai chưa tải thì không tải được nữa. |
| UC-07 | Thêm máy khác subnet | Nhập hostname/IP một lần → app nhớ, và thấy thêm người khác qua người đó. |
| UC-08 | Tạo nhóm | Đặt tên → nhóm hiện trong "Nhóm trong mạng". |
| UC-09 | Vào nhóm | [Xin vào] → Host duyệt. Hoặc Host mời → đồng ý. |
| UC-10 | Gửi trong nhóm | Gửi file → chọn "cả nhóm" hoặc tick vài thành viên. |
| UC-11 | IT cấu hình | Chính sách qua GPO. |

## 5. Yêu cầu chức năng

### 5.1 Discovery & Presence
| ID | Yêu cầu |
|---|---|
| FR-01 | Tự phát hiện peer cùng subnet (UDP multicast). |
| FR-02 | Phát hiện peer khác subnet không cần server: nhập tay, ghi nhớ địa chỉ, PEX, Host nhóm, Bridge, quét dải IP. |
| FR-03 | Trạng thái Online / Bận / Offline. |
| FR-04 | Trạng thái Ẩn. |
| FR-05 | Xử lý đúng máy nhiều card mạng. |

### 5.2 Danh tính, tin cậy, quyền
| ID | Yêu cầu |
|---|---|
| FR-10 | Mỗi thiết bị có định danh mật mã duy nhất. |
| FR-11 | Kết nối = xin + chấp nhận; hiện mã 6 số ở hai bên để đối chiếu. |
| FR-12 | Quản lý liên hệ: tên gợi nhớ, ngắt kết nối, chặn. |
| FR-13 | Mọi kết nối TLS và xác thực hai chiều. |
| FR-14 | **Mặc định từ chối:** peer chưa tin cậy chỉ được xin kết nối / xin vào nhóm; không gửi file, không PEX. |

### 5.3 Gửi & nhận file (luồng chính)
| ID | Yêu cầu |
|---|---|
| FR-20 | Gửi file/thư mục cho 1 hoặc nhiều người, hoặc chọn từ nhóm. |
| FR-21 | Người nhận thấy offer trong Hộp nhận và tự chọn: tải hết, tải một phần, bỏ qua, để sau. |
| FR-22 | Người gửi thấy trạng thái từng người nhận; thu hồi được. |
| FR-23 | Tiến độ, tốc độ, thời gian còn lại; hủy được. |
| FR-24 | **Rớt mạng ngắn → tự nối lại và tải tiếp**; quá cửa sổ (mặc định 60 giây) → Lỗi, xóa file tạm, cho [Tải lại]. |
| FR-25 | Kiểm tra SHA-256 sau khi nhận. |
| FR-26 | Kiểm tra dung lượng đĩa trước khi tải. |
| FR-27 | File đã nhận là bản sao độc lập, **không bao giờ bị app xóa**. |
| FR-28 | Đọc thẳng file gốc; sao chép tạm khi file đang bị khóa ghi, hoặc khi người dùng bật "luôn sao chép tạm". |

### 5.4 Nhóm
| ID | Yêu cầu |
|---|---|
| FR-30 | Ai cũng tạo được nhóm; máy người tạo là Host; nhóm hiện trong LAN. |
| FR-31 | Vào nhóm: xin → Host duyệt; hoặc Host mời → người được mời đồng ý. |
| FR-32 | Thành viên gửi file cho nhau mà không cần Kết nối từng cặp. |
| FR-33 | Host loại thành viên, đổi tên, đóng nhóm; thành viên rời nhóm. |
| FR-34 | Host offline: thành viên vẫn gửi cho nhau theo danh sách đã cache. |

## 6. Yêu cầu phi chức năng

| ID | Hạng mục | Yêu cầu |
|---|---|---|
| NFR-01 | Hiệu năng | ≥ 90 MB/s trên LAN 1 Gbps giữa 2 máy SSD. |
| NFR-02 | Hiệu năng | Thấy peer cùng subnet trong ≤ 3 giây. |
| NFR-03 | Hiệu năng | Thấy lại peer khác subnet đã biết trong ≤ 30 giây. |
| NFR-04 | UX | Người gửi: kéo thả là xong. Người nhận: 1 click [Tải]. |
| NFR-05 | Độ bền | Rớt mạng ≤ 60 giây không làm hỏng transfer. |
| NFR-06 | Quy mô | ≥ 300 peer online; ≥ 100 thành viên/nhóm. |
| NFR-07 | Tài nguyên | Nhàn rỗi: RAM < 150 MB, CPU ≈ 0%, mạng < 1 KB/s. |
| NFR-08 | Tin cậy | Lỗi không để lại file hỏng ở thư mục đích; không ghi đè file có sẵn. |
| NFR-09 | Bảo mật | Mặc định từ chối; không gửi dữ liệu ra ngoài LAN; không telemetry. |
| NFR-10 | Khả chuyển | Core/Engine không phụ thuộc API riêng của Windows. |
| NFR-11 | Tương thích | Khác phiên bản giao thức thì báo "cần cập nhật". |
| NFR-12 | Kích thước | File ≥ 100 GB, đường dẫn > 260 ký tự. |
