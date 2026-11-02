# 22 — Dữ liệu cỡ thật (S1–S3)

> Trạng thái: **đã đo và sửa phần cơ sở dữ liệu** (2026-11-02); phần giao diện (S1–S3) là **kế hoạch**, chờ chốt §5.

Test thường chỉ có vài tin, vài file. Đợt này đo trên lượng dữ liệu của một máy dùng vài năm.

## 1. Bộ dữ liệu

`ScaleData` (tests/Shorekeeper.Engine.Tests) ghi thẳng vào cơ sở dữ liệu:

| | |
|---|---|
| Liên hệ | 30 |
| Tin 1-1 | một hội thoại **50 000** tin (2 năm), 29 hội thoại 2 000 tin; khoảng 20% có cảm xúc (13 000 ở hội thoại lớn) |
| Nhóm | một nhóm **100 người, 30 000 tin**, 10% của mình (mỗi tin một dòng giao cho 99 người), 36 000 cảm xúc; 9 nhóm 8 người, 1 000 tin |
| File | 2 000 lượt nhận, 2 000 lượt gửi trong 90 ngày |

## 2. Cách đo

- `ScaleTests` (chỉ chạy khi `SHOREKEEPER_SCALE=1`): đổ dữ liệu vào một node thử, đo từng bước ba lần, báo lần đầu và trung vị, hỏng khi quá ngưỡng người dùng thấy chậm.
- App thật trên thư mục riêng: bản **Debug** của app PC đọc `SHOREKEEPER_DATA_DIR` (không có ở bản phát hành), test `Fill_an_app_folder` đổ dữ liệu vào thư mục đó. Đo bằng cách bấm qua PostMessage và hỏi cửa sổ bằng `WM_NULL` (không dùng UI Automation, vì nó buộc app dựng thêm đối tượng cho từng mục).

## 3. Kết quả

### Engine (PC, ms, trung vị)

| Bước | Trước | Sau |
|---|---:|---:|
| Danh sách hội thoại 1-1 | 411 | **28** |
| Danh sách hội thoại nhóm | 300 | **60** |
| Tin của hội thoại 50 000 | 306 | 356 |
| Cảm xúc của hội thoại đó | 103 | 66 |
| Tin của nhóm 30 000 (100 người) | 854 | 861 |
| Cảm xúc của nhóm đó | 257 | 126 |
| Tìm (Ctrl+K), có kết quả sớm | 156 | 156 |
| Tìm, không có kết quả (đọc hết) | 790 | 771 |
| Gửi tin / thả cảm xúc vào hội thoại lớn | 25 | 35 |
| Nạp 2 000 file nhận / 2 000 file gửi lúc mở app | 60 / 43 | 52 / 31 |

Đã sửa:
- **Lỗi**: nhóm có hơn 32 766 tin của mình **không mở được** ("too many SQL variables"): trạng thái tin của mình được hỏi từng đợt 500 id.
- Danh sách hội thoại tìm tin mới nhất của từng người / nhóm bằng chỉ mục thay vì so từng tin; đếm tin chưa đọc và tìm cảm xúc mới nhất trên tin của mình bằng chỉ mục mới (migration `0020`).

### App PC

| Việc | Kết quả |
|---|---|
| Mở app | cửa sổ sau 7 s (bản Debug) |
| Trang Tin nhắn (danh sách) | 0,7 s |
| Mở hội thoại **2 000** tin | giao diện **đứng 17 s**, bộ nhớ 352 MB → **2,2 GB** |
| Mở hội thoại **50 000** tin | đứng **hơn 5 phút**, bộ nhớ **11 GB** (đã dừng) |
| Trang File (2 000 lượt nhận) | đứng **31 s**, bộ nhớ → **4,7 GB** |

Nguyên nhân:
- Danh sách tin và danh sách File trên PC dựng **mọi** mục cùng lúc (không ảo hóa); mỗi bong bóng tạo sẵn cả menu chuột phải (hàng cảm xúc, các mục): khoảng 8 ms và 1 MB một tin.
- Mỗi thay đổi (một tin đến, một cảm xúc, tiến độ file) đọc lại **cả** hội thoại đang mở và cả cảm xúc của nó, rồi cập nhật từng tin: với 50 000 tin là gần nửa giây cơ sở dữ liệu trước cả phần giao diện.
- Điện thoại: danh sách tin đã ảo hóa (chỉ dựng phần đang thấy) nhưng vẫn đọc và xử lý cả hội thoại mỗi lần; danh sách File chưa ảo hóa.

Lặt vặt: nút "Xem" trên thẻ file chưa dịch (hiện tiếng Việt khi app tiếng Anh).

## 4. Kế hoạch

### S1 — Tin nhắn theo trang

- Mở hội thoại chỉ đọc **100 tin mới nhất**; cuộn lên tới đầu thì đọc thêm 100, giữ nguyên chỗ đang xem.
- Thay đổi chỉ đọc lại phần đã mở; cảm xúc chỉ của các tin đang có.
- Tin được trả lời nằm ngoài phần đã mở: đọc riêng tin đó cho phần trích.
- Ctrl+K mở một tin cũ: đọc quanh tin đó (50 trước, 50 sau).

### S2 — Danh sách nhẹ

- PC: danh sách tin ảo hóa như điện thoại; giữ các hành vi cuộn hiện có (xuống cuối khi có tin, giữ chỗ khi thẻ ảnh mở rộng, nhảy tới tin tìm thấy).
- Cả hai app: menu chuột phải / nhấn giữ của bong bóng chỉ tạo khi mở.
- Trang File (cả hai app): hiện 100 lượt mới nhất, nút "Hiện thêm"; lọc và tìm vẫn trên toàn bộ.

### S3 — Đo lại

- `ScaleTests` thêm ngưỡng cho phần đọc theo trang; đo lại app PC trên thư mục thử; thêm một bản đo trên điện thoại (cần cách đổ dữ liệu vào app điện thoại mà không đụng dữ liệu thật, §5 Q3).

## 5. Cần chốt

| # | Câu hỏi | Đề xuất |
|---|---|---|
| Q1 | Mỗi trang bao nhiêu tin? | **100** (vừa vài màn hình, đọc dưới 10 ms) |
| Q2 | Tìm kiếm (0,8 s khi không có kết quả, chậm hơn trên điện thoại) | **Để sau**: đúng chỉ khi gõ từ không có; nếu cần thì dùng chỉ mục toàn văn của SQLite (FTS5), bỏ dấu như bây giờ |
| Q3 | Đo trên điện thoại với dữ liệu lớn | Bản Debug của app điện thoại đọc thêm một thư mục dữ liệu riêng như PC (không đụng dữ liệu thật); hay chỉ đo phần Engine trên điện thoại |
