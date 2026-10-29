# 17 — Dọn giao diện (U5 → U7)

> Trạng thái: **xong trên PC** (2026-10-29): U5, U6, U7. Điện thoại có U5a và U6 trong mã, chưa build APK để thử. Nối tiếp đợt đổi giao diện U1–U4 (2026-10-16, [12](12-roadmap.md)). Làm theo thứ tự U5 → U6 → U7: U5 đổi bố cục chung, làm sau thì phải sửa lại nhiều chỗ.

## 1. Vì sao

Xem lại cả 6 trang của app PC (2026-10-29): rối chủ yếu vì **một thứ nằm ở nhiều chỗ** và **chữ giải thích quá nhiều**, hơn là thiếu tính năng.

| Thấy gì | Ở đâu |
|---|---|
| Văn bản ("Gửi văn bản") hiện cả trong hội thoại lẫn Hộp nhận / Đã gửi, mỗi cái một thẻ cao chỉ có [Sao chép] | Hộp nhận, Đã gửi, Tin nhắn |
| Hộp nhận và Đã gửi là hai mục riêng; điện thoại đã gộp thành tab **File** từ C2 | Thanh bên |
| Máy mình hiện hai lần: góc trên phải (trạng thái, tên, mã) và thẻ dưới trái (tên, trạng thái, ổ đĩa) | Khung cửa sổ |
| "Tên hiển thị" ở hai chỗ (chỉ đọc ở "Thiết bị này", sửa được ở "Gửi & nhận") | Cài đặt |
| Thẻ văn bản trống nội dung (11 offer từ OPPO ngày 21/10, `Note` NULL trong DB, đúng ngày DB gặp sự cố; code hiện nay không tạo được offer như vậy) | Hộp nhận |
| Mỗi thẻ cao ~110 px cho 1–2 dòng, màn hình chỉ thấy 5 mục; không chia ngày, không lọc | Hộp nhận, Đã gửi |
| Đoạn hướng dẫn dài cuối trang; mỗi tùy chọn âm thanh 3–4 dòng giải thích, cách cài VB-CABLE viết thẳng trong mô tả | Xóm, Nhóm, Âm thanh |
| Một trang cuộn rất dài, DeviceId 52 ký tự ở đầu | Cài đặt |

## 2. U5 — Gom chỗ trùng

**U5a. Văn bản chỉ ở Tin nhắn** (PC và điện thoại)
- Offer chỉ có chữ, không file, **không hiện** ở Hộp nhận / Đã gửi (điện thoại: tab File). Nó vẫn là thẻ trong hội thoại như hiện nay ([15 §6.3](15-chat.md), [16 §6.3](16-group-chat.md)), nơi có [Sao chép], [Mở] link, Thu hồi.
- Giao thức không đổi: vẫn gửi bằng offer có `note`, nên gửi được cho nhiều người một lần và cho thành viên nhóm chưa Kết nối, và máy bản cũ vẫn nhận được.
- Gửi xong văn bản (Gửi nhanh, Ctrl+V chữ, "Gửi văn bản…"): một người hay một nhóm thì mở hội thoại đó; nhiều người thì ở yên, không nhảy sang Đã gửi như trước.
- Cửa sổ nhỏ báo văn bản đến: [Xem] mở hội thoại với người gửi (hay hội thoại nhóm), không mở Hộp nhận.
- Ô tìm Ctrl+K không còn trả về văn bản (nó tìm trong Hộp nhận / Đã gửi). Tìm trong tin nhắn là việc khác, để sau.
- Thẻ văn bản trong hội thoại hiện đủ chữ (trước đây 200 ký tự, 3 dòng), chọn được, với [Sao chép] và [Mở] cho link; không còn [Xem]. Thông báo văn bản đến trên điện thoại mở hội thoại.
- Thử thật (2026-10-29, PC): Hộp nhận và Đã gửi chỉ còn offer có file; Gửi nhanh một đoạn chữ cho "Mai (máy thử)" mở hội thoại với Mai, thẻ có đủ chữ và [Sao chép].
- Offer có cả file lẫn chữ thì vẫn ở trang File, chữ là ghi chú như cũ.

**U5b. Trang File** (PC): Hộp nhận và Đã gửi thành một mục **File** trên thanh bên, hai tab **Nhận | Đã gửi** đầu trang, như điện thoại. Huy hiệu của mục File là số file mới nhận. Mọi chỗ trước đây mở Hộp nhận / Đã gửi (thẻ trong hội thoại, cửa sổ file đến, kết quả tìm, gửi xong) mở đúng tab. Thanh bên còn 6 mục.

**U5c. Máy mình một chỗ** (PC): góc trên phải giữ trạng thái (Online / Bận / Ẩn), tên và mã ngắn. Thẻ dưới trái chỉ còn **ổ nhận**: thanh dung lượng và "Trống 119 GB / 462 GB".

**U5d. Cài đặt**: bỏ dòng "Tên hiển thị" chỉ đọc ở "Thiết bị này"; tên chỉ sửa ở "Gửi & nhận". "Thư mục nhận" cũng trùng như vậy nên bỏ luôn.

Thử thật U5b–d (2026-10-29, PC): thanh bên 6 mục; File mở tab Nhận, bấm "Đã gửi" đổi tab; thẻ dưới trái "Ổ nhận file"; "Thiết bị này" còn DeviceId, dữ liệu ứng dụng, phiên bản.

## 3. U6 — Văn bản trống

Offer văn bản không có chữ (chỉ có ở dữ liệu cũ) hiện **"(Không có nội dung)"** in nghiêng, màu nhạt, trong thẻ hội thoại và dòng xem trước của danh sách hội thoại, thay vì một dòng trống; thẻ đó không có [Sao chép]. Không sửa DB.

Chưa xem tận mắt: các offer như vậy trên PC thử đều nằm trong hội thoại với OPPO, còn tin thoại chưa nghe nên không mở.

## 4. U7 — Làm gọn từng trang (PC)

**U7a. Danh sách File**
- Hàng gọn (~64 px): biểu tượng hay ảnh nhỏ, tên, một dòng "người · dung lượng · trạng thái", nút chính, [⋯].
- Chia theo ngày: **Hôm nay**, **Hôm qua**, **7 ngày qua**, **Trước đó**.
- Lọc nhanh: **Tất cả · Ảnh · File · Thư mục**, và ô tìm theo tên người hay tên file (bỏ dấu, như Ctrl+K). Lọc và tìm dùng chung cho hai tab. "Chọn tất cả" chỉ chọn những mục đang hiện; mục bị lọc ẩn thì bỏ chọn. Chọn một kết quả Ctrl+K thì bỏ lọc để thấy nó.
- Thử thật (2026-10-29, PC): hàng cao ~70 px (trước ~110); "bao cao" tìm ra "Báo cáo tháng 9.pdf"; "Ảnh" ở Đã gửi hiện "Không có mục nào khớp."

**U7b. Bớt chữ**
- Hướng dẫn cuối trang Xóm, Nhóm: còn một dòng; phần dài vào tooltip của biểu tượng (i).
- Mỗi tùy chọn: một dòng mô tả; phần dài (cách cài VB-CABLE, ý nghĩa từng mức độ trễ) vào tooltip.

**U7c. Âm thanh**: **Phát âm thanh** và **Nghe** ở trên. Độ trễ khi nghe, Phát ra, Nhận qua Bluetooth vào mục **Nâng cao** đóng mở được (mặc định đóng).

Thử thật U7b–c (2026-10-29, PC): Xóm và Nhóm còn một dòng với biểu tượng (i), đưa chuột vào thấy đủ hướng dẫn; Âm thanh còn Phát và Nghe, "Nâng cao" mở ra Độ trễ, Phát ra, Bluetooth với mô tả một dòng (đủ chữ khi đưa chuột vào).

**U7d. Cài đặt**: thanh mục con ở đầu trang, mỗi lần một mục:

| Mục | Gồm |
|---|---|
| Chung | Tên, ngôn ngữ, giao diện, phím tắt, chạy cùng Windows |
| Gửi & nhận | Thư mục nhận, thời hạn, tự nhận, ảnh trong hội thoại |
| Mạng | Máy ở mạng khác, chẩn đoán, máy thêm thủ công |
| Bảo mật | Sao lưu / khôi phục danh tính, đã chặn |
| Thống kê | Như hiện nay |
| Giới thiệu | Phiên bản, DeviceId đầy đủ kèm [Sao chép], thư mục dữ liệu |

Mục đúng nhóm nào sẽ theo những gì trang Cài đặt đang có; bảng trên là hướng, không thêm cài đặt mới.

Đã làm (2026-10-29): khối "Gửi & nhận" có một nút Lưu chung cho tên, thư mục, thời hạn, ngôn ngữ, phím tắt…, nên không tách: mục **Chung** là cả khối đó. **Mạng**: Máy ở mạng khác (nút Lưu riêng), chẩn đoán, máy thêm thủ công. **Bảo mật**: sao lưu / khôi phục danh tính, đã chặn. **Thống kê**. **Giới thiệu**: DeviceId với [Sao chép], dữ liệu ứng dụng, phiên bản. Mở Cài đặt lần đầu là mục Chung; app nhớ mục đang xem tới khi tắt.

## 5. Không làm đợt này

Tìm trong tin nhắn, trả lời trích dẫn, sửa tin, khu "đang gửi / đang nhận" chung, xem trước file trong app: tính năng mới, làm riêng sau. Điện thoại chỉ có U5a; phần còn lại theo sau nếu cần.

## 6. Kiểm tra

- Mỗi giai đoạn: build 0 cảnh báo (Desktop, Android), test Engine + Core, kiểm tra l10n (không thiếu, không thừa khóa).
- Xem thật bằng app dev: chụp từng trang chỉ trong cửa sổ Shorekeeper, sáng và tối; không mở hội thoại có tin thoại chưa nghe.
- U5a thêm: gửi văn bản PC → OPPO và ngược lại, xem nó chỉ ở hội thoại.
