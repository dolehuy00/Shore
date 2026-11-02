# 22 — Dữ liệu cỡ thật (S1–S3)

> Trạng thái: **đã đo và sửa phần cơ sở dữ liệu** (2026-11-02); **S1, S2 đã làm** (trang 50 tin), S3 (đo trên điện thoại) đang làm.

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
| S1: 50 tin mới nhất / 50 tin trước đó của hội thoại 50 000 | — | **0,8 / 0,7** |
| S1: cảm xúc của 50 tin đó | — | **0,5** |
| S1: 50 tin mới nhất / cảm xúc của chúng, nhóm 30 000 | — | **2,4 / 0,6** |

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

Sau S1, S2:

| Việc | Kết quả |
|---|---|
| Mở hội thoại 50 000 tin | **tức thì**, bộ nhớ khoảng 400 MB |
| Mở nhóm 100 người, 30 000 tin | tức thì (cảm xúc, "Đã xem 99/99" đúng) |
| Cuộn lên ba tuần tin cũ | trang trước vào dần, không giật chỗ đang xem; khoảng 450 MB |
| Ctrl+K tới một tin cũ | mở quanh tin đó, tin nằm giữa và đứng yên; cuộn xuống đọc dần tới tin mới |
| Gửi tin khi đang xem phần cũ | nhảy về tin mới nhất |
| Trang File (Nhận, Gửi, lọc Ảnh, cuộn dài) | **tức thì**, 330–470 MB |

Nguyên nhân:
- Danh sách tin và danh sách File trên PC dựng **mọi** mục cùng lúc (không ảo hóa); mỗi bong bóng tạo sẵn cả menu chuột phải (hàng cảm xúc, các mục): khoảng 8 ms và 1 MB một tin.
- Mỗi thay đổi (một tin đến, một cảm xúc, tiến độ file) đọc lại **cả** hội thoại đang mở và cả cảm xúc của nó, rồi cập nhật từng tin: với 50 000 tin là gần nửa giây cơ sở dữ liệu trước cả phần giao diện.
- Điện thoại: danh sách tin đã ảo hóa (chỉ dựng phần đang thấy) nhưng vẫn đọc và xử lý cả hội thoại mỗi lần; danh sách File chưa ảo hóa.

Lặt vặt: nút "Xem" trên thẻ file chưa dịch (hiện tiếng Việt khi app tiếng Anh). Đã sửa.

## 4. Đã làm

### S1 — Tin nhắn theo trang

- `ChatRange` (Engine): đọc 50 tin mới nhất, 50 tin trước / sau một tin, hoặc từ một tin tới một tin; vị trí là (giờ gửi, id) nên các tin cùng giờ không lẫn. Cảm xúc chỉ đọc của các tin trong phần đó.
- Mở hội thoại đọc **50 tin mới nhất**; cuộn gần tới đầu (400 px) thì đọc thêm 50, tin đầu tiên đang thấy đứng yên.
- Mỗi thay đổi đọc lại đúng phần đã mở (và tin mới đến sau nó); các tin đã có giữ nguyên, chỉ thêm ở đầu hoặc cuối.
- Tin được trả lời nằm ngoài phần đã mở: đọc riêng tin đó cho phần trích; bấm vào phần trích thì mở thêm tới tin đó.
- Ctrl+K mở một tin cũ: đọc 25 tin trước, 25 tin sau; cuộn xuống tới cuối thì đọc thêm 50 tin mới hơn. Gửi tin lúc đó thì về lại tin mới nhất.

### S2 — Danh sách nhẹ

- PC: danh sách tin ảo hóa như điện thoại; các hành vi cuộn vẫn như cũ (xuống cuối khi có tin, giữ chỗ khi thẻ ảnh mở rộng, nhảy tới tin tìm thấy).
- Trang File (cả hai app): danh sách Nhận và Gửi ảo hóa, nên không cần nút "Hiện thêm"; lọc và tìm vẫn trên toàn bộ.
- Menu của bong bóng **không** đổi sang tạo khi mở: sau khi ảo hóa chỉ còn 10–20 bong bóng được dựng, không đáng.

### S3 — Đo lại

- `ScaleTests` có thêm ngưỡng cho phần đọc theo trang; app PC đã đo lại (§3). Còn đo trên điện thoại: bản Debug của app điện thoại dùng dữ liệu riêng (§5 Q3).

## 5. Đã chốt

| # | Câu hỏi | Đề xuất |
|---|---|---|
| Q1 | Mỗi trang bao nhiêu tin? | **50** (người dùng chọn) |
| Q2 | Tìm kiếm (0,8 s khi không có kết quả, chậm hơn trên điện thoại) | **Để sau** (đã chốt): đúng chỉ khi gõ từ không có; nếu cần thì dùng chỉ mục toàn văn của SQLite (FTS5), bỏ dấu như bây giờ |
| Q3 | Đo trên điện thoại với dữ liệu lớn | **Bản Debug của app điện thoại dùng dữ liệu riêng** (đã chốt): không đụng dữ liệu thật |
