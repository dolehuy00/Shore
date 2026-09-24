# Câu hỏi mở

## Đã chốt

| # | Câu hỏi | Kết luận |
|---|---|---|
| Q1 | Có máy chạy Node riêng không? | **Không.** Thuần P2P, ai tạo nhóm thì làm Host; Bridge là vai trò tùy chọn → ADR-001 |
| Q2 | Firewall giữa VLAN có cho kết nối thẳng không? | **Cho phép**, đổi lại **mặc định từ chối** với peer chưa tin cậy → ADR-009, [04 §3](04-identity-security.md) |
| Q3 | Tích hợp AD? | 4 mức tùy chọn → [13](13-active-directory.md) |
| Q4 | Người lạ có gửi file được không? | **Không.** Phải Kết nối hoặc vào chung nhóm |
| Q5 | Rớt mạng? | Nối lại và tải tiếp trong 60 giây; quá thì Lỗi → ADR-007 |
| Q6/Q8 | Hạn file / người nhận có giữ file không? | File đã nhận là **vĩnh viễn**. Chỉ có **offer** hết hạn (người chưa tải thì không tải được nữa) |
| Q9 | Nhóm phức tạp? | Nhóm chỉ là danh sách thành viên: xin vào → Host duyệt → ADR-004 |
| Q14 | Đọc thẳng file gốc hay luôn snapshot? | **Đọc thẳng** mặc định; snapshot khi file bị khóa hoặc bật tùy chọn |
| — | Ai quyết định gửi cho ai? | **Người gửi** chọn người nhận; **người nhận** tự quyết tải hay không |

## Còn mở

| # | Câu hỏi | Ảnh hưởng | Đề xuất hiện tại |
|---|---|---|---|
| Q7 | Có cần **audit log** cho bộ phận bảo mật không? | Lưu trữ, riêng tư | Tắt mặc định, bật qua GPO |
| Q10 | Có Wi-Fi bật client isolation hoặc VPN không? | Discovery | Wi-Fi isolation: chỉ Manual/Bridge; VPN để sau |
| Q11 | Tên hiển thị: "Shorekeeper" hay tên tiếng Việt (vd "Xóm LAN")? | UI, installer | Tạm "Shorekeeper" |
| Q12 | IPv6? | Discovery | Chỉ IPv4 ở giai đoạn 1 |
| Q13 | Công ty có **AD CS** không? | AD mức 3–4 | Không có thì chỉ làm mức 1–2 |
| Q15 | Offer mở mặc định bao lâu: 24 giờ có hợp không? | UX | 24 giờ, người dùng đổi được, IT đặt trần |
| Q16 | Cửa sổ kết nối lại 60 giây có đủ không (vd: máy laptop ngủ ngắn)? | UX | 60 giây, cấu hình 15–300 giây |
