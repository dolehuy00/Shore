# 21 — Âm thanh trong Tin nhắn

> Trạng thái: **xong** (2026-11-02). Đưa "Phát âm thanh" / "Nghe" ([05 §4.7](05-protocol.md), [10 §12](10-ux.md)) vào hội thoại, cho người và cho nhóm.

## 1. Đã chốt (2026-11-02)

| Câu hỏi | Kết luận |
|---|---|
| Tính năng nào từ trang khác vào Tin nhắn? | Đợt này **Âm thanh**. Đã cân nhắc và để sau: việc với người trong ⋮ của hội thoại (Tự nhận file, Mời vào nhóm, Ngắt kết nối, Chặn), việc với nhóm (Mời, Sửa, Rời / Đóng; Nhắn riêng / Kết nối trong danh sách Thành viên), "Ảnh và file" của cuộc trò chuyện, gửi thư mục bằng 📎 trên PC |
| Thành viên nhóm chưa Kết nối có được nghe không? | **Có**: như nhận file qua nhóm, ai cùng ở một nhóm với máy phát đều nghe được (giao thức đổi, §2) |

## 2. Giao thức

- `/audio/listeners`, `/audio/listeners/{sessionId}/keys`, `DELETE /audio/listeners/{sessionId}` (trên máy phát) và `/audio/listening/{sessionId}/ended`, `/control` (trên máy nghe) đổi quyền từ **Trusted** sang **liên hệ hoặc cùng nhóm** (`TrustedOrGroupMember`). Người lạ vẫn `403`.
- Máy phát bản cũ vẫn chỉ cho liên hệ: thành viên chưa Kết nối bấm Nghe thì được báo máy đó từ chối.
- Mật khẩu nghe, giới hạn 4 máy, "Ngừng phát", "Không cho nghe" giữ nguyên. Ai cùng một nhóm bất kỳ với máy phát đều nghe được khi máy đó bật Phát âm thanh; muốn giới hạn thì đặt mật khẩu.
- Ra khỏi nhóm (rời, bị loại) mà không phải liên hệ thì không nghe được nữa.

## 3. Trong hội thoại (PC và điện thoại, `ChatViewModel`)

- **Đầu hội thoại**: người kia, hay thành viên của nhóm, đang phát thì có dải "🔊 Mai đang phát âm thanh · [Nghe]"; đang nghe thì "Đang nghe âm thanh từ Mai · [Dừng nghe]". Dùng chính `AudioViewModel` của trang Âm thanh, nên hỏi mật khẩu, thông báo lỗi và thông báo đang nghe (điện thoại) như ở đó.
- **⋮ của hội thoại**: "Phát âm thanh cho {người / nhóm}" (chỉ trên máy phát được) bật Phát âm thanh như công tắc ở trang Âm thanh (điện thoại vẫn hỏi quyền thu tiếng). Khi đã phát thật (`AudioViewModel.PlayingStarted`), một tin nhắn thường được gửi vào hội thoại đó: "🔊 Mình đang phát âm thanh: bấm Nghe ở đầu cuộc trò chuyện để nghe." (đọc được cả ở bản cũ, có thông báo như mọi tin). Đang phát thì mục này là "Tắt phát âm thanh".
- Trang Âm thanh: danh sách "Nghe" có cả thành viên cùng nhóm; chữ ghi "máy đã kết nối và người cùng nhóm".

## 4. Đã kiểm

- Test engine: thành viên cùng nhóm, không là liên hệ, nghe được; bị loại khỏi nhóm thì `403`; người lạ `403` (đổi quyền về Trusted thì test đầu hỏng).
- Thử thật (2026-11-02, PC và Nubia, hai máy là liên hệ):
  - PC: ⋮ của hội thoại Z2577 → "Phát âm thanh cho Z2577": Phát âm thanh bật, tin mời gửi đi ("Đã nhận").
  - Nubia: hội thoại PC có dải "PC · MSI đang phát âm thanh · [Nghe]" → Nghe → "Đang nghe âm thanh từ PC · MSI · [Dừng nghe]", trang Âm thanh của PC có Z2577 trong "Đang nghe" → Dừng nghe.
  - Nubia: hội thoại "Nhóm thử" (PC là thành viên) cũng có dải đó.
  - PC: ⋮ → "Tắt phát âm thanh": công tắc ở trang Âm thanh tắt, dải ở Nubia mất.
  - Đã xóa tin mời thử ở hai máy.
- Chưa thử trên máy thật: thành viên nhóm chưa Kết nối (hai máy đang có là liên hệ của nhau); điện thoại phát (cần đồng ý chia sẻ màn hình trên máy).
