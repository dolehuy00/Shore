# 12 — Lộ trình

Mỗi milestone kết thúc bằng một bản **chạy được, demo được**. Trọng tâm là **M1–M3**: thấy nhau, kết nối, gửi file.

## M0 — Nền móng ✅ (2026-09-23)

- Solution + project theo [02 §3](02-architecture.md); build CI.
- Generic Host, Serilog, cấu hình (settings.json + đọc GPO).
- `DeviceIdentity`: khóa P-256, cert tự ký, DPAPI, DeviceId.
- SQLite + migration runner.
- Avalonia shell: cửa sổ chính rỗng, tray, single-instance.

**Xong khi:** mở app thấy DeviceId của mình; đóng/mở lại vẫn giữ nguyên.

## M1 — Thấy nhau (cùng subnet) ✅ (2026-10-01)

- Multicast discovery: announce / heartbeat / reply / bye, nhiều interface, lọc adapter ảo.
- `PeerDirectory`, trạng thái Online/Stale/Offline.
- Màn hình "Xóm" + trạng thái của mình (Online/Bận/Ẩn).
- Chẩn đoán mạng bản đầu.

**Xong khi:** 3 máy cùng subnet thấy nhau trong ≤ 3 giây; rút dây thì máy đó Offline trong ≤ 30 giây.

## M2 — Kết nối & quyền hạn

- Kestrel + mTLS; `PeerConnector`; `GET /hello`.
- **AccessPolicy** (Any / Trusted / GroupMember / Recipient) + rate limit cho Unknown. Viết test cho từng endpoint.
- Kết nối: xin → chấp nhận, mã 6 số, ngắt kết nối, chặn, tên gợi nhớ.
- Manual (thêm máy theo hostname/IP) + Known Endpoints.

**Xong khi:** máy chưa kết nối chỉ gọi được 3 endpoint công khai (có test chứng minh); kết nối được máy khác subnet bằng hostname, và tự thấy lại sau khi khởi động lại app.

## M3 — Gửi & nhận file ⭐ (trọng tâm)

- Offer: tạo, giao (kể cả khi người nhận online sau), thu hồi, hết hạn, lưu DB.
- Hộp nhận: tải hết / chọn file / bỏ qua / để sau; tự nhận theo liên hệ.
- Downloader: stream, hash incremental, `.skpart`, **Reconnecting + Range tải tiếp** trong cửa sổ 60 giây, Lỗi → Tải lại.
- Người gửi: phục vụ Range/ETag, hash cache, snapshot, phát hiện file gốc bị đổi.
- Gửi nhiều file, thư mục, nhiều người nhận; trùng tên; MOTW; kiểm tra dung lượng.
- UX: kéo thả, chọn nhiều, `Ctrl+V`, toast có nút, Đã gửi / Hộp nhận.
- Benchmark: 20 GB, 10.000 file nhỏ, 1 Gbps.

**Xong khi:** gửi 20 GB đạt ≥ 90 MB/s và hash khớp; rút dây 20 giây rồi cắm lại thì tải tiếp; rút 2 phút thì báo Lỗi rõ ràng, không để lại file rác, và [Tải lại] thành công.

## M4 — Khác subnet tự động & triển khai

- PEX (chỉ Trusted); Bridge (registry + SSE); Subnet Probe.
- Installer WiX, rule firewall, ADMX/ADML, Authenticode.
- Lab 2 subnet.

**Xong khi:** 2 subnet thấy nhau sau một lần Kết nối bắc cầu, hoặc ngay lập tức nếu có Bridge.

## M5 — Nhóm

- `GroupHostService` / `GroupMemberService`, SSE.
- Tạo nhóm, hiện trong mạng, xin vào → duyệt, Host mời → đồng ý, rời / loại / đóng.
- Quyền "cùng nhóm" cho offer; gửi cho nhóm (chọn người nhận); cache thành viên khi Host offline.
- Nhóm là nguồn discovery (địa chỉ thành viên khác subnet).

**Xong khi:** nhóm 6 máy (có máy khác subnet) gửi file cho nhau không cần Kết nối từng cặp; Host tắt thì vẫn gửi được; người bị loại không gửi được nữa.

## M6 — Hoàn thiện

- Tích hợp Explorer, cửa sổ Gửi nhanh + phím tắt.
- AD mức 1–2 ([13](13-active-directory.md)); mức 3–4 nếu có AD CS.
- Sao lưu/khôi phục danh tính.
- Chuẩn bị port Linux/macOS.

## Kiểm thử (xuyên suốt)

| Loại | Nội dung |
|---|---|
| Unit | DeviceId, mã 6 số, sanitize đường dẫn, máy trạng thái offer/tải, AccessPolicy |
| Integration | Nhiều Engine trong 1 process trên loopback, discovery giả lập |
| Bảo mật | Mọi endpoint bị gọi bởi Unknown / thành viên đã bị loại / người không có trong recipients → phải `403` |
| Fault injection | Ngắt mạng 5s / 30s / 120s giữa chừng, đổi IP người gửi, file nguồn đổi, đĩa đầy, hash sai |
| Mạng thật | Lab 2 VLAN |
| Hiệu năng | 1 Gbps / 2.5 Gbps; 1 × 50 GB; 50.000 file nhỏ |
| UX | Thử với 3–5 người dùng thật sau M3 |
