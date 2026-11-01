# 19 — Khu "Đang chuyển" và xem trước file (T1–T2)

> Trạng thái: **T1 xong** (2026-11-01); T2 theo kế hoạch đã chốt (§6). Nối tiếp [17 §5](17-ui-cleanup.md): "khu đang gửi / đang nhận chung, xem trước file trong app: tính năng mới, làm riêng sau".

## 1. Mục tiêu

| Làm | Không làm |
|---|---|
| **Một chỗ** thấy mọi việc đang chuyển, cả nhận lẫn gửi, trên PC và điện thoại | Tạm dừng / tiếp tục bằng tay (đã có tự nối lại 60 giây khi rớt mạng, [06](06-file-transfer.md)) |
| Tiến độ, tốc độ, thời gian còn lại; Hủy ngay tại đó | Giới hạn tốc độ, xếp hàng ưu tiên |
| **Xem trước ảnh** ngay trong app, phóng to, qua ảnh trước/sau | Xem trước file **chưa tải** về |
| Ảnh chụp bằng điện thoại hiện đúng chiều (EXIF), ở cả ảnh nhỏ | Sửa ảnh, xem video / nghe nhạc trong app (vẫn mở app của hệ điều hành) |

Thứ tự: **T1 Đang chuyển** (không đổi giao thức) → **T2 Xem trước** (chỉ ở máy mình, không đổi giao thức).

## 2. Hiện nay

- **Nhận**: dòng của offer ở File › Nhận ghi "Đang tải 45% · 98 MB/s" và có [Hủy]; điện thoại tương tự ở tab File. Thông báo thường trực của điện thoại chỉ ghi "Đang nhận 2 lượt file", không có %.
- **Gửi**: người nhận tự kéo file từ máy mình ([06](06-file-transfer.md)). File › Đã gửi ghi từng người "đang tải 45%" (`BytesServed`), không có tốc độ.
- Không có chỗ nào gộp hai chiều: muốn biết máy đang bận gì thì phải mở đúng tab và tìm dòng đó giữa lịch sử. Bản phác ở [10 §2](10-ux.md) có "⬆ 1 đang gửi" ở cuối thanh bên, nhưng chưa làm.
- **Mở ảnh / file**: [Mở] và bấm ảnh trong hội thoại đều gọi app của hệ điều hành. Ảnh nhỏ trong hội thoại và trang File được giải mã thẳng, **không đọc hướng EXIF**: ảnh chụp dọc bằng điện thoại có thể hiện nằm ngang.

## 3. T1 — Khu "Đang chuyển" ✅ (2026-11-01)

**Chung cho hai app** (`TransferActivityViewModel`, `Shorekeeper.UI`)
- Danh sách việc đang chạy, lấy từ dữ liệu đã có, không thêm gì vào Engine hay giao thức:
  - nhận: offer ở `InboxState.Downloading`, tiến độ theo `BytesReceived`;
  - gửi: mỗi người đang tải file mình gửi (`RecipientState.Downloading`), tiến độ theo `BytesServed`, một dòng cho mỗi người.
- Mỗi dòng có chiều (⬇/⬆), tên ("báo cáo.pdf + 2 file"), người gửi hay người đang tải, thanh tiến độ, và "45% · 8,9 MB/s · còn 3 phút". Tốc độ được làm mượt như dòng ở trang File; thời gian còn lại chỉ hiện khi đã có tốc độ. Đang nối lại thì ghi "Đang nối lại…".
- Cập nhật theo sự kiện `Changed`, thêm nhịp 1 giây khi danh sách không trống: `BytesServed` tăng mà không có sự kiện.
- Xong (nhận Completed / Partial, người nhận Completed / Partial) thì dòng thành **"Xong ✓"** 5 giây rồi biến mất. Hủy, lỗi hay bị từ chối thì biến mất ngay.
- [Hủy] chỉ có ở việc nhận. Việc gửi không có nút dừng: muốn dừng thì Thu hồi cả offer ở Đã gửi (§6 Q2).

**PC**
- Thanh bên, ngay trên thẻ "Ổ nhận file": thẻ **Đang chuyển**, chỉ hiện khi có việc, với "⬇ 2 · ⬆ 1", thanh tiến độ chung và tốc độ tổng.
- Bấm thẻ thì mở popup liệt kê từng việc. Việc nhận xong có [Mở] (mở file, hay thư mục khi có nhiều file).
- Bấm một dòng: popup đóng, mở File đúng tab và cuộn tới mục đó (như kết quả Ctrl+K). Popup đóng **sau** khi lệnh của dòng đã chạy; đóng trước thì dòng mất mục cần mở, như lỗi menu cảm xúc ở C6d.
- Tooltip của biểu tượng tray: "Shorekeeper · Đang nhận 2 · đang gửi 1".

**Điện thoại**
- Tab File: dải **Đang chuyển** dưới hai nút Nhận / Đã gửi, chạm thì mở ra danh sách như popup của PC. Chạm một dòng thì sang đúng tab Nhận / Đã gửi.
- Thông báo thường trực của `TransferService` có thanh tiến độ chung và phần trăm ("Đang nhận 1 lượt file · 22%"), xem lại mỗi 2 giây, chỉ đăng lại khi chữ hay số đổi. Không có tốc độ: thông báo được tính trong service, không qua giao diện.

**Đã kiểm**
- Test UI: làm mượt tốc độ (chưa đủ nửa giây thì giữ, đếm lùi thì về 0), chữ "45% · … · còn 2 phút / 2 giờ / vài giây", "Đang nối lại…", chữ cho tray.
- Thử thật PC và Nubia (2026-11-01; OPPO không cắm), PC gửi 1,4 GB qua Gửi nhanh:
  - PC "⬆ 1 · 9,1 MB/s", Nubia "⬇ 1 · 8,8 MB/s"; popup PC "T1 thu chuyen.bin · Z2577 · 13% · 8,9 MB/s · còn 3 phút"; thông báo Nubia "Đang nhận 1 lượt file · 22%" có thanh.
  - [Hủy] trên dải của Nubia: việc dừng ("Đã dừng"), dải và thẻ biến mất ở cả hai máy.
  - File 286 MB tải hết: cả hai máy "Xong ✓", 5 giây sau biến mất.
  - Bấm dòng trong popup PC mở File › Đã gửi.
- Chưa thử: chiều điện thoại → PC (nên chưa thấy [Mở] trên PC bằng mắt), hai việc cùng lúc.

## 4. T2 — Xem trước file trong app

**Ảnh** (JPEG, PNG, GIF tĩnh, WebP, BMP: những gì Skia giải mã được)
- Mở bằng: bấm ảnh trong hội thoại (§6 Q5), [Xem] hay bấm vào dòng ảnh ở trang File, kết quả Ctrl+K là ảnh.
- **PC**: cửa sổ xem với ảnh vừa khung, lăn chuột để phóng to / thu nhỏ, kéo để di chuyển, ←/→ sang ảnh khác của cùng offer hay thẻ, `Esc` để đóng. Thêm nút [Mở bằng app khác], [Hiện trong thư mục], [Sao chép ảnh] (`CopyImageAsync` đã có).
- **Điện thoại**: màn hình đầy, chụm hai ngón để phóng to, vuốt ngang sang ảnh khác, nút quay lại để đóng; [Chia sẻ], [Mở bằng app khác].
- Giải mã theo cỡ màn hình (`Bitmap.DecodeToWidth`), không giữ ảnh gốc 50 MP trong bộ nhớ; ảnh kế bên được nạp trước.
- **Hướng EXIF**: đọc tag Orientation (JPEG, mã ngắn tự viết, không thêm thư viện) rồi xoay khi hiện. Áp dụng cho cả ảnh nhỏ đang có ở hội thoại và trang File (§2).
- Định dạng không giải mã được (HEIC, RAW…) thì hiện biểu tượng file với [Mở bằng app khác].

**Chữ và PDF** (§6 Q4)
- Chữ thuần (.txt .log .md .json .csv .xml, tối đa 1 MB): chữ đơn cách, chỉ đọc, chọn và chép được.
- PDF: PC dùng `Windows.Data.Pdf` (có sẵn từ Windows 10) để vẽ từng trang thành ảnh; điện thoại dùng `android.graphics.pdf.PdfRenderer`. Chỉ xem, cuộn theo trang.

**Xong khi:** ảnh dọc chụp bằng điện thoại gửi sang PC hiện đúng chiều ở ảnh nhỏ và trong cửa sổ xem; ảnh 40 MB mở trong dưới 1 giây và bộ nhớ không tăng quá cỡ màn hình × vài ảnh; ←/→ đi hết các ảnh của một thẻ 48 ảnh.

## 5. Rủi ro

| Rủi ro | Cách xử lý |
|---|---|
| Nhịp 1 giây làm tốn pin điện thoại | Chỉ chạy khi có việc và khi app đang mở; thông báo dùng tiến độ có sẵn của `TransferService` |
| Tốc độ nhảy lung tung với nhiều file nhỏ | Làm mượt (trung bình trượt), như dòng ở trang File hiện nay |
| Ảnh lớn làm hết bộ nhớ trên điện thoại | Luôn giải mã theo cỡ màn hình; chỉ giữ ảnh đang xem và hai ảnh kế bên |
| `Windows.Data.Pdf` cần WinRT | App PC đã gọi WinRT cho toast; bản Linux/macOS sau này không có PDF |

## 6. Đã chốt (2026-11-01, theo đề xuất)

| # | Câu hỏi | Kết luận |
|---|---|---|
| Q1 | Khu Đang chuyển trên PC đặt ở đâu? | **Thẻ ở thanh bên + popup** (luôn thấy, không thêm trang); hoặc tab thứ ba "Đang chuyển" trong trang File |
| Q2 | Bên gửi: có nút dừng một người đang tải file mình gửi không? | **Không**: chỉ hiện; muốn dừng thì Thu hồi cả offer ở Đã gửi như hiện nay |
| Q3 | Việc xong thì sao? | **Ở lại "Xong ✓" 5 giây**, có [Mở], rồi biến mất |
| Q4 | Xem trước những loại nào? | **Đợt này chỉ ảnh**; chữ và PDF làm sau nếu cần |
| Q5 | Bấm ảnh trong hội thoại mở gì? | **Trình xem trong app** (có nút mở bằng app khác) thay vì app của hệ điều hành |
| Q6 | Thông báo trên điện thoại có thanh tiến độ không? | **Có**, một thanh chung cho mọi việc |
