# 19 — Khu "Đang chuyển" và xem trước file (T1–T3)

> Trạng thái: **T1, T2 xong** (2026-11-01), theo kế hoạch đã chốt (§6); **T3 xong** (2026-11-02, §7): ảnh chưa tải hiện bằng bản nhỏ. Xem trước chữ và PDF để sau. Nối tiếp [17 §5](17-ui-cleanup.md): "khu đang gửi / đang nhận chung, xem trước file trong app: tính năng mới, làm riêng sau".

## 1. Mục tiêu

| Làm | Không làm |
|---|---|
| **Một chỗ** thấy mọi việc đang chuyển, cả nhận lẫn gửi, trên PC và điện thoại | Tạm dừng / tiếp tục bằng tay (đã có tự nối lại 60 giây khi rớt mạng, [06](06-file-transfer.md)) |
| Tiến độ, tốc độ, thời gian còn lại; Hủy ngay tại đó | Giới hạn tốc độ, xếp hàng ưu tiên |
| **Xem trước ảnh** ngay trong app, phóng to, qua ảnh trước/sau | Xem trước file **chưa tải** về (trừ ảnh: bản nhỏ, T3 §7) |
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

## 4. T2 — Xem ảnh trong app ✅ (2026-11-01)

Đợt này chỉ ảnh (§6 Q4); chữ và PDF để sau.

**Giải mã và hướng EXIF** (`PictureLoader`, `Shorekeeper.UI`)
- Đọc phần đầu file, không thêm thư viện:
  - JPEG: đi qua các marker tới khung ảnh (SOF) để lấy cỡ, lấy tag Orientation (0x0112) trong APP1 "Exif", theo cả hai thứ tự byte;
  - PNG: cỡ trong IHDR.
- Giải mã không lớn hơn cỡ cần hiện (`DecodeToWidth`, hay `DecodeToHeight` khi ảnh lưu nằm ngang), ảnh nhỏ giữ nguyên cỡ.
- Rồi xoay theo tag, bằng cách sắp lại điểm ảnh 32 bit; đủ 8 giá trị (lật, xoay 90/180/270, chuyển vị).
- Áp dụng cho ảnh trong thẻ hội thoại (hai app) và ảnh nhỏ ở File › Nhận (PC).
- Định dạng Skia không đọc được (HEIC…) thì thẻ vẫn ghi như trước; trong trình xem ghi "Không xem được ảnh này trong app."

**Trình xem** (`PictureViewerViewModel`, dùng chung)
- Giữ ảnh đang xem và hai ảnh kế bên (nạp trước); ảnh xa hơn được giải phóng. Đóng trình xem thì giải phóng hết.
- Giải mã tối đa 2560 × 1600 trên PC, 1600 × 2560 trên điện thoại.
- Mở bằng cách:
  - bấm ảnh trong thẻ hội thoại (§6 Q5), với mọi ảnh của thẻ đó, bắt đầu từ ảnh vừa bấm;
  - trên PC, bấm ảnh nhỏ ở File › Nhận: các ảnh đã nhận của offer đó.
- Kết quả Ctrl+K vẫn mở bằng app của hệ điều hành như trước.

**PC** (`PictureWindow`)
- Cửa sổ riêng, ảnh vừa khung (không phóng quá cỡ thật).
- Lăn chuột phóng to / thu nhỏ quanh con trỏ, kéo để di chuyển; `+` / `−` / `0` (vừa khung).
- ◀ ▶ hay ←/→ sang ảnh khác, có "2 / 3"; `Esc` để đóng.
- [Sao chép ảnh], [Hiện trong thư mục], [Mở bằng app khác].

**Điện thoại** (lớp phủ trong `MainView`)
- Nền đen toàn màn hình, tên file và "2 / 3" ở trên, ✕ hay nút Back của hệ thống để đóng.
- Chụm hai ngón phóng to từ 1 đến 6 lần (`PinchGestureRecognizer`); đã phóng to thì kéo để di chuyển.
- Chưa phóng to thì vuốt ngang để sang ảnh khác (sang trái là ảnh sau).
- **Chưa làm:** [Chia sẻ] và [Mở bằng app khác], vì app chưa có FileProvider để đưa file cho app khác.

**Đã kiểm**
- Test UI:
  - cỡ và hướng từ JPEG theo cả hai thứ tự byte, có đoạn APP0 đứng trước;
  - PNG; file lạ; JPEG không có tag;
  - cả 8 cách sắp điểm ảnh.
- Thử thật (2026-11-01). PC gửi Nubia 3 ảnh JPEG tạo bằng PIL, có chữ "TRÊN" ở mép trên:
  - một ảnh dọc lưu nằm ngang 2000 × 1500 với tag 6;
  - một ảnh lưu lộn ngược với tag 3;
  - một ảnh thường.
- Kết quả:
  - Ảnh trong hội thoại đứng đúng chiều trên cả PC lẫn Nubia.
  - Trình xem PC mở "3 / 3", ◀ về "1 / 3" là ảnh dọc đứng đúng, lăn chuột phóng to quanh giữa, `Esc` đóng.
  - Nubia: chạm ảnh mở lớp phủ "2 / 3", vuốt trái sang "3 / 3", vuốt phải về "1 / 3", Back đóng.
- Chưa thử trên máy thật:
  - chụm hai ngón (adb chỉ giả lập được một ngón);
  - ảnh nhỏ ở File › Nhận của PC (cần PC nhận ảnh);
  - ảnh 40 MB và thẻ 48 ảnh.

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
| Q7 | (2026-11-02) Ảnh chưa tải: xem được không, từ ai? | **Bản nhỏ** máy gửi làm (T3, §7), không tự tải ảnh gốc. Như "Tự tải ảnh": chỉ từ máy đã Kết nối, **trừ nhóm chat**: trong nhóm thì từ mọi thành viên |

## 7. T3 — Ảnh chưa tải hiện bằng bản nhỏ ✅ (2026-11-02)

Trước đây ảnh không tự tải (quá 20 MB tổng, tắt "Tự tải ảnh", hay từ thành viên nhóm chưa Kết nối) chỉ hiện tên file và [Tải]. Giờ thẻ hiện bản nhỏ của từng ảnh; ảnh gốc vẫn chỉ về khi bấm Tải.

**Máy gửi**
- `GET /offers/{offerId}/files/{fileId}/thumbnail` ([05 §4.3](05-protocol.md)): cùng quyền như tải file (người nhận của offer, còn là liên hệ hay còn trong nhóm, offer còn mở).
- Làm khi được hỏi, không làm trước: JPEG chất lượng 80, cạnh dài tối đa **640 px**, đứng đúng chiều (EXIF), phần trong suốt thành nền trắng (`PictureThumbnailMaker`, qua `IThumbnailMaker`).
- Giữ trong bộ nhớ cho người nhận sau (tối đa 256 bản); hai ảnh giải mã cùng lúc; ảnh trên 100 MB hay kiểu không đọc được (HEIC) thì `404`.
- Lấy bản nhỏ **không** tính là tải: trạng thái người nhận vẫn "chưa mở".

**Máy nhận** (`InboxService`)
- Offer chỉ gồm ảnh, không tự tải, từ liên hệ đã Kết nối hay gửi qua nhóm: lấy bản nhỏ ngay khi offer tới, và mỗi lần thẻ hiện mà còn thiếu (người gửi vừa online lại), cách nhau ít nhất 1 phút.
- Lưu ở `previews\{người gửi}\{offerId}\…` trong thư mục dữ liệu của app ([09 §1](09-data-storage.md)), không phải thư mục nhận; mở lại app vẫn còn.
- Xóa khi offer bị xóa khỏi lịch sử hay bị thu hồi; lúc khởi động dọn bản nhỏ của offer không còn (quá 90 ngày).
- Người gửi bản cũ (chưa có địa chỉ này) hay không làm được bản nhỏ: thẻ như trước.

**Thẻ và trình xem**
- Thẻ ảnh hiện bản nhỏ cho ảnh chưa tải, ảnh gốc cho ảnh đã tải; có [Tải] và thanh tiến độ ngay dưới (trước đây thẻ ảnh chỉ có khi đã tải xong nên không cần).
- Bấm bản nhỏ mở trình xem như ảnh thường, có dòng "Ảnh xem trước · bấm Tải để lấy ảnh gốc"; trên PC ẩn [Hiện trong thư mục] và [Mở bằng app khác].
- File › Nhận chưa dùng bản nhỏ (vẫn biểu tượng ảnh).

**Đã kiểm**
- Test engine: bản nhỏ đến mà offer vẫn "Mới" và thư mục nhận trống, người gửi vẫn thấy "chưa mở"; xóa offer thì hết sạch; thu hồi thì xóa; máy không phải người nhận bị `403`; người gửi không làm được bản nhỏ hay offer có file khác thì không có; thành viên nhóm **không** là liên hệ vẫn nhận được bản nhỏ (bỏ điều kiện nhóm thì test này hỏng). Test UI: phủ nền trắng.
- Thử thật (2026-11-02):
  - PC gửi Nubia 3 ảnh JPEG 9 MB (tổng 26,8 MB, nên không tự tải), một ảnh lưu nằm ngang có tag 6: thẻ hiện 3 bản nhỏ đứng đúng chiều, "Mới"; trình xem "1 / 3" có dòng xem trước; cài lại app vẫn còn; [Tải] → "Đang tải 32%" có thanh → "Đã nhận", ảnh gốc thay bản nhỏ.
  - Nubia gửi PC một ảnh 26 MB chọn bằng 📎 (`content://`, tag 6): PC hiện bản nhỏ 471 × 640 px, **9,4 KB**, đứng đúng chiều; trình xem PC có dòng xem trước và chỉ còn [Sao chép ảnh].
  - Xóa offer: thư mục bản nhỏ trên PC bị xóa.
