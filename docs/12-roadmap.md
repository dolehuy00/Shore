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

## M2 — Kết nối & quyền hạn ✅ (2026-10-01)

- Kestrel + mTLS; `PeerConnector`; `GET /hello`.
- **AccessPolicy** (Any / Trusted / GroupMember / Recipient) + rate limit cho Unknown. Viết test cho từng endpoint.
- Kết nối: xin → chấp nhận, mã 6 số, ngắt kết nối, chặn, tên gợi nhớ.
- Manual (thêm máy theo hostname/IP) + Known Endpoints.

**Xong khi:** máy chưa kết nối chỉ gọi được 3 endpoint công khai (có test chứng minh); kết nối được máy khác subnet bằng hostname, và tự thấy lại sau khi khởi động lại app.

## M3 — Gửi & nhận file ⭐ (trọng tâm)

### M3a ✅ (2026-10-01)

- Offer: tạo, giao (kể cả khi người nhận online sau), thu hồi, hết hạn, lưu DB.
- Hộp nhận: tải / bỏ qua / để sau / tải lại / hủy; mở file, mở thư mục.
- Downloader: stream, hash incremental, `.skpart`, **Reconnecting + Range/If-Range tải tiếp** trong cửa sổ 60 giây, Lỗi → Tải lại.
- Người gửi: phục vụ Range/ETag, hash nền có cache, snapshot khi file bị khóa (có hỏi), phát hiện file gốc bị đổi, tối đa 16 stream (vượt thì trả `503 busy`, bên nhận coi như rớt mạng ngắn).
- Gửi nhiều file, thư mục, nhiều người nhận; trùng tên; Mark of the Web; kiểm tra dung lượng.
- UX: kéo thả lên thẻ, Ctrl+click chọn nhiều, menu Gửi file…/Gửi thư mục…, popup góc màn hình khi có file đến, trang Hộp nhận / Đã gửi, badge số offer mới.
- Đo trên loopback (một máy, cùng ổ đĩa): 4 GB trong 34,9 giây tính cả kiểm tra SHA-256 (~117 MB/s trung bình, ~176 MB/s lúc truyền). Kill máy gửi giữa chừng rồi bật lại: bên nhận tải tiếp và checksum khớp.

### M3b ✅ (2026-10-01)

- Click chọn thẻ / Ctrl+click chọn nhiều; `Ctrl+V` gửi file đang copy cho người đã chọn.
- "Chọn file…" tải một phần offer → trạng thái "Đã nhận x/y file", sau đó "Tải phần còn lại". Bên gửi thấy "đã nhận một phần" và offer vẫn mở (receipt `partial`).
- Tự nhận file theo từng liên hệ (menu chuột phải, tối đa 2 GB); offer tự nhận không hiện popup.
- Màn hình Cài đặt "Gửi & nhận": tên hiển thị (đổi là phát presence mới ngay), thư mục nhận, thư mục con theo người gửi, thời hạn offer, thời gian chờ kết nối lại, luôn sao chép tạm. Mục bị GPO khóa thì chỉ đọc.
- Benchmark 10.000 file × 4 KB, cả hai đầu trên một máy: ~69 giây (~145 file/giây), giới hạn bởi việc tạo file của hệ điều hành/antivirus (đo riêng: 290–590 file/giây chỉ phía nhận). Kèm sửa: giới hạn 10.000 **file** (thư mục không tính), checksum long-poll (bỏ chờ 1 giây mỗi file nhỏ), lọc log từng request của ASP.NET Core (110.033 dòng → 22 dòng).
- **Chưa làm:** benchmark LAN 1 Gbps giữa hai máy thật. Windows toast chuyển sang M6 (làm cùng package identity).

**Xong khi:** gửi 20 GB đạt ≥ 90 MB/s và hash khớp; rút dây 20 giây rồi cắm lại thì tải tiếp; rút 2 phút thì báo Lỗi rõ ràng, không để lại file rác, và [Tải lại] thành công.

## M4 — Khác subnet tự động & triển khai

### M4a ✅ (2026-10-01)

- **PEX** (chỉ Trusted): liên hệ kể cho nhau peer mình nghe trực tiếp; peer học được chỉ hiện sau khi trả lời probe.
- **Giữ kết nối khác subnet:** peer ngoài subnet đang nghe trực tiếp được probe mỗi 5 giây, nên hai bên không rớt khỏi danh sách sau 30 giây.
- **Bridge:** registry trong RAM, khóa theo chứng chỉ người gọi, lease 60 giây; client long-poll (thay SSE, xem ADR-010), tối đa 3 Bridge. Client biết Bridge qua `caps: ["bridge"]`, PEX và địa chỉ cấu hình (GPO `BridgeAddresses` / Cài đặt). Thẻ hiện "qua cầu nối".
- **Subnet Probe:** dải CIDR (GPO `ProbeSubnets` / Cài đặt), tối đa /22, 100 gói/giây, 10 phút một lần.
- Cài đặt "Máy ở mạng khác" (bật cầu nối, địa chỉ cầu nối, dải IP; GPO `AllowBridge` / `EnableBridge` khóa được), Chẩn đoán hiện trạng thái cầu nối.
- Test trên loopback: PEX, Bridge end-to-end (đăng ký → thấy nhau → rời là biến mất), registry (version, lease, long-poll), quét dải IP.

### M4b ✅ (2026-10-02)

- MSI per-machine bằng WiX v5 (`deploy/build-msi.ps1`): app self-contained, shortcut Start menu, rule firewall theo chương trình (Domain + Private), tự khởi động xuống tray, tham số `AUTOSTART` / `FIREWALL_RULES` / `DOWNLOAD_DIR` / `BRIDGE_ADDRESSES`, đóng app đang chạy khi nâng cấp/gỡ.
- ADMX + ADML (en-US, vi-VN) cho 10 chính sách app đang đọc.
- **Chưa làm:** Authenticode (chưa có chứng chỉ, xem [11 §7](11-windows-deployment.md)); lab 2 subnet trên máy thật; cài thử MSI trên máy sạch.

**Xong khi:** 2 subnet thấy nhau sau một lần Kết nối bắc cầu, hoặc ngay lập tức nếu có Bridge.

## M5 — Nhóm

- `GroupHostService` / `GroupMemberService`, SSE.
- Tạo nhóm, hiện trong mạng, xin vào → duyệt, Host mời → đồng ý, rời / loại / đóng.
- Quyền "cùng nhóm" cho offer; gửi cho nhóm (chọn người nhận); cache thành viên khi Host offline.
- Nhóm là nguồn discovery (địa chỉ thành viên khác subnet).

**Xong khi:** nhóm 6 máy (có máy khác subnet) gửi file cho nhau không cần Kết nối từng cặp; Host tắt thì vẫn gửi được; người bị loại không gửi được nữa.

## M6 — Hoàn thiện

- Package identity (sparse MSIX) → Windows toast thật + menu "Gửi bằng Shorekeeper…" trong Explorer (Windows 11); cửa sổ Gửi nhanh + phím tắt.
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
