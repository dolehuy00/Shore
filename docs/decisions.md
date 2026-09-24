# Quyết định kiến trúc (ADR)

Mỗi mục: **Bối cảnh → Quyết định → Hệ quả**. Trạng thái: `Đề xuất` / `Chấp nhận` / `Thay thế`.

---

## ADR-001 — Thuần P2P, không có server riêng

**Trạng thái:** Chấp nhận (v0.2, thay thế bản v0.1 "Node tùy chọn")

**Bối cảnh:** Ban đầu có ý tưởng Node chạy dạng Windows Service. Người dùng muốn mô hình ngang cấp hoàn toàn: ai mở nhóm hoặc khởi tạo kết nối thì máy đó điều phối.

**Quyết định:** Một app duy nhất. Vai trò theo hành động: người gửi phục vụ file của mình, Group Host (người tạo nhóm) quản lý thành viên, Bridge (tùy chọn, bật trên bất kỳ client nào).

**Hệ quả:**
- ✅ Không phải vận hành server, không có điểm lỗi chung.
- ✅ Một sản phẩm, một installer.
- ❌ Người nhận chỉ tải được khi người gửi online; không duyệt thành viên mới được khi Host offline.
- ❌ Khác subnet cần một lần bắc cầu, trừ khi có Bridge.

---

## ADR-002 — Avalonia cho UI

**Trạng thái:** Chấp nhận

**Bối cảnh:** Windows trước, Linux/macOS sau. UI là trọng tâm sản phẩm.

**Quyết định:** Avalonia + CommunityToolkit.Mvvm. Tính năng riêng Windows (toast, Explorer, AD) đặt sau interface trong `Platform.Windows`.

**Hệ quả:** ✅ Một code UI cho mọi OS. ❌ Toast và menu Explorer phải làm riêng cho từng OS.

---

## ADR-003 — Discovery nhiều lớp, không cần server

**Trạng thái:** Chấp nhận

**Bối cảnh:** Có máy khác subnet. Multicast không qua router. Không có server.

**Quyết định:** Multicast + Known Endpoints + PEX + Group Host + Bridge (tùy chọn) + Subnet Probe + Manual, hợp nhất trong `PeerDirectory`. Danh tính luôn xác minh bằng mTLS.

**Hệ quả:** ✅ Cùng subnet không cần cấu hình; khác subnet chỉ cần bắc cầu một lần, hoặc tự động nếu có Bridge. ❌ Nhiều code discovery, cần lab nhiều subnet để test.

---

## ADR-004 — Nhóm chỉ là danh sách thành viên do Host quản lý

**Trạng thái:** Chấp nhận (v0.3, thay thế "Host giữ mục chia sẻ" của v0.2 và mô hình phân tán của v0.1)

**Bối cảnh:** Người dùng muốn đơn giản: ai muốn vào thì xin, Host duyệt; người gửi quyết định gửi cho ai.

**Quyết định:** Nhóm **không chứa file**. Host chỉ quản lý thành viên (xin vào → duyệt, mời → đồng ý, loại, đóng) và phát sự kiện qua SSE. Gửi trong nhóm = một Offer mà người gửi chọn người nhận từ danh sách thành viên. Thành viên cache danh sách để vẫn gửi được khi Host offline.

**Hệ quả:** ✅ Rất đơn giản; một cơ chế gửi duy nhất. ✅ Host offline không làm tê liệt nhóm. ❌ Người vào nhóm sau không thấy file đã gửi trước đó (đúng ý đồ: người gửi quyết định).

---

## ADR-005 — ECDSA P-256 cho danh tính thiết bị

**Trạng thái:** Chấp nhận

**Quyết định:** P-256 cho cert TLS; DeviceId = SHA-256(SPKI). Có sẵn trong BCL, không cần thư viện ngoài.

---

## ADR-006 — MSI (WiX)

**Trạng thái:** Chấp nhận

**Quyết định:** Một MSI per-machine có tham số, tạo rule firewall, phân phối bằng GPO/Intune. Có bản per-user dự phòng.

**Hệ quả:** ✅ Hợp môi trường doanh nghiệp. ❌ Không tự cập nhật, cập nhật qua kênh IT.

---

## ADR-007 — Gửi file: Offer do người gửi sở hữu, người nhận tự tải, nối lại trong phiên

**Trạng thái:** Chấp nhận (v0.3)

**Bối cảnh:** Người dùng xác định: người gửi là chủ file và chọn gửi cho ai; người nhận thấy file và tự quyết; gửi là copy, nhận xong là vĩnh viễn; rớt mạng ngắn thì chạy tiếp, không nối lại được thì fail.

**Quyết định:**
- **Offer** = file + danh sách người nhận, mở đến khi thu hồi / hết hạn (mặc định 24 giờ). Dùng chung cho 1–1, nhiều người, nhóm.
- Người nhận thấy offer trong **Hộp nhận**; tải hết / chọn file / bỏ qua / để sau.
- Người nhận `GET` stream. Rớt mạng → **Reconnecting**, thử lại với backoff, tải tiếp bằng `Range` + `If-Range/ETag` trong **cửa sổ 60 giây**. Quá thì Failed, xóa `.skpart`, [Tải lại] nếu offer còn mở.
- Tiến độ chỉ giữ trong bộ nhớ, không tải tiếp qua lần khởi động lại.
- Người gửi đọc thẳng file gốc (Q14); snapshot khi file bị khóa ghi hoặc khi người dùng bật "luôn sao chép tạm". Hash tính một lần phía gửi (cache), incremental phía nhận.

**Hệ quả:**
- ✅ Chịu được rớt Wi-Fi, đổi mạng, lag ngắn mà không cần lưu trạng thái xuống đĩa.
- ✅ Một cơ chế cho mọi kiểu gửi.
- ❌ Thoát app giữa chừng thì phải tải lại từ đầu. Chấp nhận được.

---

## ADR-009 — Mặc định từ chối với peer chưa tin cậy

**Trạng thái:** Chấp nhận (v0.3)

**Bối cảnh:** Mạng cho phép kết nối thẳng giữa các VLAN (Q2), nên mọi máy trong công ty đều chạm được tới API của app.

**Quyết định:** Mỗi endpoint khai báo mức quyền tối thiểu. Peer Unknown chỉ được: xem presence, `GET /hello`, **xin kết nối**, **xin vào nhóm** (có giới hạn tần suất). Gửi file cần Trusted hoặc cùng nhóm. PEX chỉ cho Trusted. Tải file cần nằm trong danh sách người nhận. Bỏ chính sách "cho người lạ gửi file".

**Hệ quả:** ✅ Bề mặt tấn công nhỏ; không spam file được. ❌ Lần đầu gửi cho ai đó phải Kết nối trước (1 bước chấp nhận), hoặc vào chung nhóm.

---

## ADR-008 — Tích hợp AD qua chứng chỉ (attestation), không qua Kerberos

**Trạng thái:** Đề xuất (phụ thuộc công ty có AD CS)

**Bối cảnh:** App chạy trong phiên người dùng trên nhiều máy, không có SPN, nên Kerberos ngang hàng không khả thi, còn NTLM đang bị loại bỏ.

**Quyết định:** Giữ khóa thiết bị cho mTLS. Nếu có AD CS, thêm bước attestation: ký `{deviceId, nonce}` bằng cert người dùng do AD CS cấp và xác minh chain tới Root CA công ty. Chi tiết ở [13](13-active-directory.md).

**Hệ quả:** ✅ Bỏ được bước so mã; chặn được máy ngoài domain. ❌ Cần IT cấu hình AD CS template + auto-enrollment.
