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

**Quyết định:** Một MSI per-machine có tham số, tạo rule firewall, phân phối bằng GPO/Intune. Không có bản per-user (M4b). Dùng **WiX v5**: v6 không thêm gì cần cho gói này (upgrade code không bắt buộc, WixUI không custom action, thay đổi Burn) nhưng kèm Open Source Maintenance Fee và bước chấp nhận EULA khi build. Chưa ký Authenticode (chưa có chứng chỉ).

**Hệ quả:** ✅ Hợp môi trường doanh nghiệp. ✅ Ai build từ mã nguồn cũng không vướng điều khoản phí. ❌ Không tự cập nhật, cập nhật qua kênh IT. ❌ Máy không có quyền admin thì không cài được. ❌ Chưa ký nên SmartScreen cảnh báo.

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

---

## ADR-010 — Bridge dùng long-poll thay cho SSE

**Trạng thái:** Chấp nhận (M4a)

**Bối cảnh:** Thiết kế ban đầu đẩy thay đổi registry của Bridge qua SSE. App đã có long-poll ở hai chỗ (quyết định kết nối, checksum) và mọi request đi qua cùng một `PeerClient` (mTLS, thử nhiều địa chỉ, timeout từng lần).

**Quyết định:** `GET /bridge/peers?since=<version>&wait=<giây>`: có thay đổi sau `since` thì trả ngay (gộp lô 1 giây), không thì chờ tối đa 25 giây. Phiên bản bắt đầu từ thời điểm Bridge khởi động, nên client giữ version cũ sau khi Bridge khởi động lại sẽ nhận lại toàn bộ. Bản ghi xóa được nhớ 5 phút; tụt xa hơn cũng nhận toàn bộ.

**Hệ quả:**
- ✅ Một kiểu request, không cần parser SSE hay giữ stream; đứt kết nối thì request sau tự đồng bộ lại.
- ✅ Client dùng chính vòng long-poll để làm mới các peer học từ Bridge, nên không cần heartbeat riêng.
- ❌ Mỗi client mở lại request khoảng 25 giây một lần (300 client ≈ 12 request/giây), không đáng kể.

---

## ADR-011 — Nhóm: thành viên long-poll Host, lời mời là duyệt trước

**Trạng thái:** Chấp nhận (M5)

**Bối cảnh:** Thiết kế ban đầu: Host đẩy sự kiện qua SSE và gọi ngược tới người xin (`/joined`, `/join-declined`) hoặc người được mời (`/join`). Như vậy Host phải gọi được tới từng thành viên, phải tự thử lại khi họ offline, và thêm 4 endpoint cùng logic SSE.

**Quyết định:**
- Thành viên và người đang xin **cùng một endpoint** `GET /groups/{id}/state?since=&wait=` (long-poll như Bridge, ADR-010). Kết quả: danh sách (thành viên), `pending`, `declined`, hoặc `404` (không liên quan / bị loại / nhóm đóng).
- Lời mời = Host ghi sẵn yêu cầu `invited` (duyệt trước) rồi báo người được mời. Đồng ý = gửi yêu cầu vào nhóm như thường; Host thấy `invited` thì cho vào ngay.
- Phiên bản danh sách bắt đầu từ thời điểm Host khởi động, nên thành viên giữ version cũ sau khi Host khởi động lại sẽ nhận lại danh sách.

**Hệ quả:**
- ✅ Chỉ cần thành viên gọi được tới Host (giống mọi thứ khác: người nhận tải từ người gửi). Người xin offline lúc được duyệt vẫn biết khi mở lại app.
- ✅ 3 endpoint trên Host + 1 trên người được mời, không có SSE.
- ❌ Đổi trạng thái online của thành viên không đẩy ngay; thành viên lấy địa chỉ mới ở lần poll kế tiếp (≤ 20 giây). Online/offline trên UI lấy từ discovery của chính mình nên không bị ảnh hưởng.

---

## ADR-012 — Toast và menu Explorer không cần package identity

**Trạng thái:** Chấp nhận (M6a)

**Bối cảnh:** Kế hoạch ban đầu dùng sparse MSIX để có package identity, từ đó có toast "chuẩn" và menu cấp 1 trên Windows 11 (`IExplorerCommand`). Sparse package chỉ đăng ký được khi có chữ ký từ chứng chỉ máy tin cậy; dự án chưa ký số (11 §7). Cài cert tự ký vào máy người dùng thì đổi một lỗ hổng lấy một tính năng.

**Quyết định:**
- Toast: đăng ký app id `Shorekeeper` (tên + icon) dưới `HKCU\Software\Classes\AppUserModelId`, gửi toast bằng WinRT (`ToastNotificationManager`, TFM `net10.0-windows10.0.19041.0`). Nút bấm về qua sự kiện `Activated` khi app đang chạy; không đăng ký COM activator. Không hiện được toast (bị tắt, lỗi) → cửa sổ nhỏ như cũ.
- Explorer: verb cổ điển `HKLM\Software\Classes\{*,Directory}\shell\Shorekeeper.Send` (do MSI ghi, `MultiSelectModel=Player`) → `Shorekeeper.exe --send "<path>"`. Mỗi mục một tiến trình; tiến trình phụ chuyển đường dẫn cho bản đang chạy qua named pipe, bản đang chạy gom vào một cửa sổ Gửi nhanh.
- MSI dùng codepage 65001 để ghi được chữ tiếng Việt vào registry.

**Hệ quả:**
- ✅ Không cần chứng chỉ; chạy cả khi `dotnet run`.
- ✅ Chạy trên Windows 10 và 11 như nhau.
- ❌ Windows 11: menu nằm trong "Show more options".
- ❌ Bấm toast sau khi đã thoát app không làm gì; vì vậy app xóa toast của mình khi thoát.
- ❌ Chọn nhiều file trong Explorer → nhiều tiến trình khởi động ngắn (mỗi tiến trình vài trăm ms trước khi chuyển cho bản đang chạy).
- ❌ Bản projection WinRT làm app nặng thêm ~25 MB (trước nén).
- Khi có chữ ký số: thêm sparse MSIX cho menu mới của Windows 11, giữ verb cổ điển cho Windows 10.
