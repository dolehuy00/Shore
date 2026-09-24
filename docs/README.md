# Shorekeeper — Tài liệu thiết kế

> **Shorekeeper** là ứng dụng gửi file ngang hàng (P2P) trong mạng LAN công ty.
> Mỗi máy cài app, tự nhìn thấy nhau. **Người gửi** kéo thả file vào những người mình chọn;
> **người nhận** thấy file trong Hộp nhận và tự quyết có tải hay không. Tải xong thì file là của họ.
> **Nhóm** là danh sách người: ai muốn vào thì xin, người tạo nhóm (Host) duyệt.
> **Không có server** và **mặc định từ chối** với máy chưa được xác nhận tin cậy.

| Thông tin | Giá trị |
|---|---|
| Trạng thái | Draft v0.3 |
| Nền tảng giai đoạn 1 | Windows 10/11 (x64) |
| Nền tảng tương lai | Linux, macOS |
| Công nghệ chính | .NET 10, Avalonia UI, ASP.NET Core Kestrel (nhúng), SQLite |
| Cập nhật lần cuối | 2026-09-23 |

## Mục lục

| # | Tài liệu | Nội dung |
|---|---|---|
| 01 | [Tổng quan & yêu cầu](01-overview.md) | Mục tiêu, phạm vi, use case, yêu cầu |
| 02 | [Kiến trúc](02-architecture.md) | Thành phần, cấu trúc solution, luồng nội bộ |
| 03 | [Discovery & Presence](03-discovery-presence.md) | Tìm máy online trong cùng subnet **và khác subnet**, không cần server |
| 04 | [Danh tính, Tin cậy & Quyền hạn](04-identity-security.md) | Khóa thiết bị, Kết nối, **quyền hạn (mặc định từ chối)**, threat model |
| 05 | [Giao thức](05-protocol.md) | Gói UDP, HTTP API |
| 06 | [Gửi file](06-file-transfer.md) | **Luồng chính:** người gửi chọn người nhận → Hộp nhận → tự quyết tải → nối lại khi rớt mạng |
| 07 | [Nhóm](07-groups.md) | Danh sách thành viên: xin vào → Host duyệt |
| 08 | [Vai trò Host & Bridge](08-host-roles.md) | Mọi client đều có thể làm "Node" |
| 09 | [Lưu trữ dữ liệu](09-data-storage.md) | Thư mục, schema SQLite |
| 10 | [UX & luồng màn hình](10-ux.md) | Màn hình, kéo thả, thông báo |
| 11 | [Triển khai trên Windows](11-windows-deployment.md) | Installer, firewall, GPO, auto-start |
| 12 | [Lộ trình](12-roadmap.md) | Milestone M0 → M6 |
| 13 | [Tích hợp Active Directory](13-active-directory.md) | Tùy chọn, nếu công ty có AD |
| — | [Quyết định kiến trúc (ADR)](decisions.md) | Nhật ký quyết định & lý do |
| — | [Câu hỏi mở](open-questions.md) | Những điểm cần chốt thêm |

## Thuật ngữ

| Thuật ngữ | Nghĩa |
|---|---|
| **Peer / Thiết bị** | Một máy cài Shorekeeper. |
| **DeviceId** | Định danh thiết bị = hash của public key. Không giả mạo được. |
| **Presence** | Tín hiệu "tôi đang online" + thông tin liên lạc của một peer. |
| **Unknown** | Peer chưa được xác nhận. Chỉ được xin kết nối / xin vào nhóm. |
| **Trusted (Đã kết nối)** | Hai người đã Kết nối: một bên xin, bên kia chấp nhận. |
| **Offer** | Lượt gửi: file + danh sách người nhận do người gửi chọn. Mở đến khi thu hồi / hết hạn. |
| **Hộp nhận** | Nơi người nhận thấy các offer gửi cho mình và quyết định tải / bỏ qua. |
| **Reconnecting** | Rớt mạng khi đang tải: tự nối lại và tải tiếp trong cửa sổ 60 giây. |
| **Host** | Máy tạo nhóm, duyệt/loại thành viên. Không giữ file. |
| **Nhóm** | Danh sách thành viên. Thành viên gửi file cho nhau được mà không cần Kết nối từng cặp. |
| **Bridge** | Vai trò tùy chọn mà bất kỳ client nào cũng bật được: làm "danh bạ" giúp các subnet thấy nhau. |
| **PEX (Peer Exchange)** | Các peer kể cho nhau những máy mà chúng đang thấy. |

## Lịch sử thay đổi

| Hạng mục | v0.1 | v0.2 | v0.3 (hiện tại) |
|---|---|---|---|
| Server | Node riêng (Windows Service) | Bỏ; mọi client có thể là Host/Bridge | Giữ như v0.2 |
| Gửi file | Resume qua chunk bitmap, nhiều nguồn | Copy thẳng; rớt mạng = lỗi | **Offer do người gửi chọn người nhận; Hộp nhận; nối lại & tải tiếp trong 60 giây** |
| File đã nhận | Có thể hết hạn | Vĩnh viễn | Vĩnh viễn |
| Nhóm | Phân tán, ký số, CRDT | Host giữ thành viên + mục chia sẻ | **Chỉ là danh sách thành viên: xin vào → Host duyệt** |
| Quyền | Cho người lạ gửi (tùy chính sách) | Như v0.1 | **Mặc định từ chối; phải Kết nối hoặc cùng nhóm** |
| Khác subnet | Qua Node | PEX + ghi nhớ + Host nhóm + Bridge + nhập tay | Giữ như v0.2, PEX chỉ giữa Trusted |

## Quy ước

- Viết bằng tiếng Việt, giữ thuật ngữ kỹ thuật tiếng Anh.
- Yêu cầu đánh mã `FR-xx`, `NFR-xx`.
- Sơ đồ: Mermaid hoặc ASCII.
