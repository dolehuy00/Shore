# 18 — Tin nhắn: tìm, trả lời, @nhắc tên, sửa, cảm xúc (C6)

> Trạng thái: **C6 xong** (C6a–C6d, 2026-10-31), theo kế hoạch đã chốt (§8). Nối tiếp [15](15-chat.md) (1-1) và [16](16-group-chat.md) (nhóm), lúc đó để sau: "@nhắc tên, cảm xúc, trả lời trích dẫn, sửa tin", và "tìm trong tin nhắn" ([17](17-ui-cleanup.md) U5a).

## 1. Mục tiêu

| Làm | Không làm (§7) |
|---|---|
| **Tìm theo nội dung tin nhắn**: Ctrl+K trên PC, ô tìm ở tab Tin nhắn trên điện thoại | Đồng bộ lịch sử giữa các máy của cùng một người |
| **Trả lời trích dẫn** một tin, 1-1 và nhóm | Tìm trong tin nhắn thoại |
| **@nhắc tên** thành viên trong nhóm | Lịch sử các lần sửa |
| **Sửa tin** chữ của mình | Cảm xúc tùy chọn ngoài bộ cố định |
| **Thả cảm xúc** (bộ 6 biểu tượng cố định), 1-1 và nhóm | Chuyển hộ tin của người đã rời nhóm ([ADR-016](decisions.md)) |

Thứ tự làm: **C6a tìm** (không đổi giao thức) → **C6b trả lời + @nhắc tên** (cùng đổi nội dung tin, đổi chữ ký nhóm một lần) → **C6c sửa** → **C6d cảm xúc**.

## 2. Tương thích bản cũ

- Presence thêm `caps: "chat2"`. Máy không có `chat2` vẫn nhắn được như hiện nay; những gì mới chỉ đi tới máy có `chat2`.
- **1-1**: máy cũ bỏ qua trường lạ ([15 §4](15-chat.md)), nên tin có `replyTo` vẫn tới, chỉ không hiện phần trích. Lệnh sửa và cảm xúc đi route mới; máy cũ từ chối route lạ nên lệnh bị bỏ, và nó vẫn thấy chữ cũ, không thấy cảm xúc.
- **Nhóm**: chữ ký là chuỗi byte cố định có tiền tố phiên bản ([16 §3.5](16-group-chat.md)). Tin có trường mới ký bằng `shorekeeper-group-chat/2`, gồm thêm `replyTo` và `mentions`; tin không có trường mới vẫn ký `/1`. Máy cũ không kiểm được `/2` nên sẽ bỏ tin. Vì vậy tin `/2` không gửi cho thành viên chưa có `chat2`, và với người đó tin mang trạng thái `old` ("Máy này chưa cập nhật"), như máy chưa có chat nhóm hiện nay.

## 3. C6a — Tìm theo nội dung tin nhắn ✅ (2026-10-31)

**Engine**
- `ChatService.FindAsync` và `GroupChatService.FindAsync`: đọc tin chữ từ mới tới cũ (`QueryUnbufferedAsync`), giữ tin có **mọi từ** của câu tìm theo đúng quy tắc của Ctrl+K (`TextSearch`: không phân biệt hoa thường, bỏ dấu, `đ` thành `d`), dừng khi đủ 50. Không thêm cột hay migration: lọc trong bộ nhớ, vài nghìn tin chỉ mất vài ms, và sửa tin (C6c) không phải cập nhật gì thêm. Đo lại nếu chậm (FTS5).
- Bỏ tin thoại, tin đã thu hồi; tin nhóm đã xóa (`Deleted`) cũng bỏ, tin 1-1 đã xóa thì không còn trong bảng.

**Chung cho hai app** (`ChatViewModel.FindAsync`, `Shorekeeper.UI`)
- Gộp tin 1-1, tin nhóm và **thẻ văn bản** (offer chỉ có chữ, gửi hay nhận, chưa thu hồi), mới nhất trước, tối đa 50.
- Mỗi kết quả (`ChatSearchHit`): tên hội thoại, giờ, và một dòng "Mai: …nộp Báo cáo tháng 9…": người viết, rồi đoạn chữ quanh chỗ khớp (`TransferText.Snippet`, tối đa 80 ký tự, cắt ở ranh giới từ). Phần khớp không tô đậm.
- `OpenAt`: mở hội thoại, rồi cuộn tới đúng tin (hay thẻ) và viền nó 2 giây (`ChatScroll.Reveal`, kiểu `Border.found`). Hội thoại tải cả lịch sử nên tin cũ cũng tới được.

**Giao diện**
- **PC**: Ctrl+K liệt kê thêm tối đa 8 tin nhắn (biểu tượng 💬) sau Người / Nhóm / File; chúng tới sau các mục kia một chút vì đọc từ database. Chọn thì sang trang Tin nhắn tại tin đó.
- **Điện thoại**: ô **Tìm trong tin nhắn** luôn nằm đầu danh sách hội thoại (thay cho nút 🔍 dự kiến). Có chữ thì danh sách kết quả hiện thay chỗ các hội thoại, "Không tìm thấy tin nhắn nào." nếu trống; ✕ hay nút Back xóa ô tìm. Chạm một kết quả thì mở hội thoại tại tin đó.

**Đã kiểm**: test Engine (có dấu / không dấu, nhiều từ không theo thứ tự, giới hạn số kết quả, bỏ tin thoại / đã thu hồi / đã xóa, tin nhóm); test UI cho đoạn trích. Thử thật (2026-10-31):
- PC: Ctrl+K "u5 nubia" ra thẻ "Bạn: U5 thu tren Nubia…" trong hội thoại với Z2577; Enter mở hội thoại và viền thẻ đó.
- Nubia: gõ "nubia" ra 2 kết quả (tin nhóm và thẻ văn bản); chạm thẻ thì mở hội thoại với PC và viền nó.

## 4. C6b — Trả lời trích dẫn và @nhắc tên ✅ (2026-10-31)

**Trả lời trích dẫn**
- Tin mang `replyTo`: id của tin được trả lời trong cùng hội thoại, hoặc `offer:` cộng id offer khi trả lời một thẻ file (cùng một offer là "đã gửi" ở bên này và "đã nhận" ở bên kia). Phần trích **không** gửi kèm: máy nhận tự tìm tin đó trong hội thoại của mình, nên không ai giả được nội dung trích (`ChatReplies.IsReplyTo` kiểm định dạng, sai thì 400).
- Bong bóng hiện khung trích ngay dưới tên người viết: tên và dòng đầu, "Tin nhắn thoại 0:12", tên file. Bấm vào khung thì cuộn tới tin gốc và viền nó (`ChatScroll.Reveal`, như kết quả tìm ở §3).
  - Tin gốc là thẻ đã thu hồi: chữ thu hồi của thẻ, ví dụ "File đã được thu hồi".
  - Máy mình không có tin gốc (đã xóa, hay người mới vào nhóm không có lịch sử đó): "Tin nhắn không còn".
- Cách bắt đầu trả lời: mục **Trả lời** khi chuột phải (PC) hay nhấn giữ (điện thoại). Ô soạn hiện dải "Trả lời Mai: …" có nút ✕. **Vuốt bong bóng sang phải chưa làm.**
- Trả lời được tin chữ, tin thoại và thẻ file chưa thu hồi.

**@nhắc tên** (chỉ trong nhóm)
- Gõ `@` (đầu ô hay sau khoảng trắng) thì phía trên ô soạn hiện **@mọi người** và các thành viên khác, lọc theo chữ gõ tiếp (không dấu, `TextSearch`), tối đa 8. Chọn một mục thì thay "@…" bằng "@Tên " và con trỏ về cuối ô; nút gợi ý không lấy focus nên bàn phím điện thoại không bị đóng.
- Tin mang `mentions`: danh sách DeviceId hoặc `"all"`, tối đa 100, không trùng, nằm trong phần được ký. Chỉ người đã **chọn từ gợi ý** và chữ "@Tên" vẫn còn trong tin lúc gửi mới vào `mentions`: xóa chữ đó trước khi gửi thì người đó không bị nhắc.
- Người được nhắc (`GroupChatService.IsMentioned`: tin của người khác có tên mình hay `all`):
  - **cả bong bóng** có viền màu nhấn (kiểu `Border.bubble.mention`), không tô riêng đoạn "@Tên" như dự kiến: chữ "@Tên" có thể đã bị sửa tay, và viền thì dễ thấy khi cuộn;
  - hội thoại có dấu **@** trong danh sách khi còn tin chưa đọc nhắc tới mình (`GroupChatConversation.Mentioned`);
  - **có thông báo kể cả khi đã Tắt thông báo nhóm** (§8 Q3): toast trên PC, thông báo trên điện thoại.
- `mentions` **không** lọc theo danh sách thành viên của máy nhận như dự kiến: nó nằm trong phần được ký và được chuyển hộ nguyên vẹn, bỏ bớt thì chữ ký hỏng ở máy sau. Mỗi máy chỉ xét tên mình và `all`, nên nhắc người ngoài nhóm không gây gì.

**Engine và giao thức**
- `ChatMessageBody.ReplyTo`; `GroupChatMessageBody.ReplyTo` và `Mentions`. `SendTextAsync` / `SendVoiceAsync` nhận thêm `replyTo` (và `mentions` cho nhóm).
- Chữ ký nhóm `/2` (§2) chỉ dùng khi tin có `replyTo` hay `mentions`; sửa một trong hai thì chữ ký hỏng và tin bị bỏ (400).
- Presence thêm `chat2` (`PeerInfo.CanChat2`). Tin `/2` không gửi cho thành viên nghe thấy trực tiếp mà không có `chat2`: với người đó tin mang trạng thái `old`.
  - Còn hở: máy cũ **tự kéo** tin `/2` qua mục lục (khi vắng mặt rồi quay lại) thì kiểm chữ ký không qua và bỏ tin đó. Chấp nhận, vì máy nào cũng sẽ cập nhật.
- Migration `0017_chat_replies.sql`: cột `ReplyTo` cho hai bảng tin, `Mentions` cho tin nhóm (lưu cách nhau bởi khoảng trắng, đúng thứ tự đã ký).

**Đã kiểm**:
- Test Engine:
  - `replyTo` tới cả tin lẫn thẻ file, và `replyTo` sai định dạng bị từ chối;
  - `replyTo` và `mentions` tới mọi thành viên và được chuyển hộ nguyên chữ ký (cả cho người mới vào);
  - sửa `mentions` / `replyTo`, hay bỏ `replyTo` khỏi tin `/2`, thì tin bị bỏ;
  - máy không có `chat2` nhận trạng thái `old`;
  - đếm `Mentioned` của hội thoại, kể cả `all`.
- Test UI: khung trích của tin chữ, tin thoại và khi không còn tin gốc.
- Thử thật PC và Nubia (2026-10-31; OPPO mất kết nối nên chỉ 2 máy):
  - 1-1: PC trả lời một thẻ văn bản; Nubia trả lời lại tin đó, và PC hiện khung trích "Bạn / C6b tra loi the van ban".
  - Nhóm tạm: gõ "@p" trên Nubia thì chỉ còn "@PC · MSI"; chọn rồi gõ tiếp, gửi. PC thấy dấu **@** trong danh sách, bong bóng có viền. Ngược lại, PC gửi "@mọi người": Nubia có thông báo, dấu **@**, và viền.
  - Chưa thử: thông báo khi nhóm đã Tắt thông báo (code đã có trên cả hai app).
- Chỗ chưa đẹp: trên bong bóng của mình (nền màu nhấn), vạch màu của khung trích lẫn vào nền.

## 5. C6c — Sửa tin ✅ (2026-10-31)

**Ai sửa được gì**
- Chỉ **tin chữ của mình**. Không sửa được tin thoại, thẻ file, hay tin đã thu hồi; thu hồi sau khi sửa thì vẫn được.
- **Thời hạn:**
  - Tin đã tới ai đó: trong **15 phút** tính từ lúc gửi (`ChatService.EditWindow`). Lấy mốc giờ gửi như thu hồi, vì không lưu lúc tin tới. Như vậy tin chờ gửi lâu rồi mới tới thì còn ít thời gian sửa.
  - Tin chưa tới ai (1-1: chờ gửi hay không gửi được; nhóm: chưa tới thành viên nào): **lúc nào cũng được**. Tin đi luôn với chữ mới và **không** có dấu "Đã sửa" (nhóm: tin được ký lại).
- Thời hạn do **máy người gửi** giữ. Máy nhận không kiểm, vì đồng hồ hai máy có thể lệch.
- Chỉ giữ chữ mới nhất, không giữ lịch sử. Phần trích (C6b), tìm kiếm (C6a) và dòng xem trước trong danh sách đều theo chữ mới. Tin sửa thì không có thông báo mới.

**Giao diện**
- Mục **Sửa** trong menu chuột phải (PC) hay nhấn giữ (điện thoại) của tin. Chữ cũ hiện vào ô soạn, phía trên có dải "Sửa tin nhắn" với ✕. Gửi thì thay chữ; ✕, mở hội thoại khác, hay bấm Trả lời thì thôi sửa và xóa ô soạn.
- Bong bóng ghi "· Đã sửa" ngay sau giờ, trước trạng thái: "22:06 · Đã sửa · Đã xem".
- Quá hạn trong lúc đang sửa thì gửi không được, và hiện "Tin nhắn này không còn sửa được.".

**1-1**
- `POST /chat/edits` `{ id, text, editedAt }`. Máy nhận đổi chữ nếu `editedAt` mới hơn lần sửa đang có: lệnh sửa cũ tới sau không đè được lệnh mới.
- Hàng đợi `ChatEdits` chỉ giữ id; lúc gửi mới đọc chữ mới nhất, nên sửa nhiều lần khi người kia offline chỉ gửi một lần. Bản cũ không có route này thì từ chối, và lệnh sửa bị bỏ.

**Nhóm**
- Lệnh sửa ký `shorekeeper-group-edit/1`. Phần ký gồm `groupId`, `id`, tác giả, `sentAt`, **chữ mới**, `replyTo`, `mentions` và `editedAt`, tức mọi thứ chữ ký của tin gốc che, chỉ thay chữ.
  - Vì thế khi chuyển hộ, tin đã sửa **tự đứng được bằng chữ ký sửa**. `GroupChatMessageBody` mang thêm `editedAt` và `editSig`; máy nhận kiểm chữ ký sửa thay cho chữ ký gốc (chữ ký gốc ký chữ cũ). Không phải giữ chữ cũ để chuyển hộ.
  - Chỉ tác giả sửa được: lệnh sửa phải ký bằng đúng khóa đi kèm tin.
- Thành viên đã có tin được báo qua `POST /chat/groups/{id}/edits`, hàng đợi `ChatGroupEdits`. Thành viên chưa nhận tin thì nhận luôn tin đã sửa.
- **Chuyển hộ:** sửa thì `Seq` của tin tăng, và mục lục có thêm `editedAt`. Máy kéo thấy bản sửa mới hơn bản mình có thì tải lại tin đó. Máy offline lúc sửa vẫn nhận được chữ mới từ bất kỳ thành viên nào.
- Thành viên chưa có `chat2` mà còn chờ nhận tin đã sửa thì mang trạng thái `old`, như tin có trả lời hay nhắc tên.
- **Sửa đúng lúc tin đang trên đường đi** (máy kia nhận chữ cũ): sau khi gửi xong, máy gửi thấy chữ đã khác thì báo thêm một lệnh sửa bình thường cho máy đó. Tin nhóm ký lại khi chưa tới ai thì đổi thành tin đã sửa có ký. Cả hai bên ghi "Đã sửa".
- Lệnh sửa không đổi `mentions`: xóa chữ "@Tên" khi sửa thì người đó vẫn được tính là bị nhắc.

**Engine và lưu trữ**
- `ChatService.EditAsync` và `GroupChatService.EditAsync` trả `false` khi không còn sửa được.
- `ChatMessage.EditedAt` và `GroupChatMessage.EditedAt`.
- Migration `0018_chat_edits.sql`: `EditedAt` cho hai bảng tin, `EditSig` cho tin nhóm, và hai bảng hàng đợi.

**Đã kiểm**:
- Test Engine:
  - 1-1: sửa tới máy kia và tìm theo chữ mới; tin còn chờ gửi thì đi với chữ mới, không có dấu; quá 15 phút, tin thoại, chữ rỗng hay tin đã thu hồi thì không sửa được; người nhận không sửa được tin của người gửi; lệnh sửa cũ không đè lệnh mới.
  - Nhóm:
    - lệnh sửa tới mọi thành viên, `mentions` vẫn giữ;
    - người mới vào nhận tin đã sửa kèm chữ ký sửa;
    - thành viên có tin từ người khác vẫn kéo được bản sửa;
    - người chuyển hộ sửa chữ thì tin bị bỏ;
    - tin chưa tới ai thì đi với chữ mới, không có dấu;
    - chỉ tác giả sửa được, và trong 15 phút.
  - Thử ngược: bỏ kiểm chữ ký sửa ở cả hai chỗ, bỏ điều kiện kéo bản sửa mới, hay bỏ nhánh tin còn chờ gửi thì các test tương ứng báo lỗi.
- Test UI: "Sửa" chỉ có trên tin chữ của mình trong 15 phút (hay khi chưa tới ai); "Đã sửa" cạnh giờ; đường link theo chữ mới.
- Thử thật PC và Nubia (2026-10-31):
  - 1-1: PC sửa "C6c hop luc 3 gio" thành "…4 gio nhe", Nubia hiện chữ mới và "Đã sửa". Ngược lại, Nubia sửa tin của mình, PC hiện chữ mới và "Đã sửa".
  - Nhóm tạm: Nubia sửa tin nhóm đã tới PC; PC hiện chữ mới và "Đã sửa", tức chữ ký sửa được kiểm qua.

## 6. C6d — Thả cảm xúc ✅ (2026-10-31)

**Thả và xem**
- Bộ cố định 6 biểu tượng: 👍 ❤️ 😂 😮 😢 🙏 (§8 Q2, `ChatReactions.Emojis`). Mỗi người một cảm xúc cho mỗi tin: chọn cái khác thì thay, chọn lại cái đang thả thì bỏ.
- Thả được trên tin chữ và tin thoại (cả của mình), không trên tin đã thu hồi hay thẻ file.
- **Cách thả:** hàng 6 biểu tượng ở **đầu menu** của tin, chuột phải trên PC hay nhấn giữ trên điện thoại. Bấm một biểu tượng thì menu đóng.
  - Khác dự kiến: không có nút 🙂 hiện khi rê chuột, và PC cũng dùng hàng biểu tượng thay cho menu con "Thả cảm xúc". Một lần bấm thay vì hai, và giống điện thoại.
- **Chip dưới chữ, trong bong bóng:** "👍 2 ❤️ 1", theo thứ tự của bộ. Chip có cảm xúc của mình thì có viền (màu nhấn; trắng trên bong bóng của mình).
  - Rê chuột (PC) hay bấm/chạm vào chip thì hiện ai đã thả, ví dụ "👍 Mai, Bạn", ở dòng ghi chú trên ô soạn.

**Thông báo, gộp theo từng tin** (§8 Q5)
- Ai đó thả cảm xúc vào tin của mình thì có **một** thông báo cho tin đó. Thông báo liệt kê những người thả **từ lần xem trước**, mỗi người một lần với cảm xúc mới nhất của họ (`ChatReactionNotice`):
  - "Mai đã thả ❤️ vào tin của bạn: “họp lúc 3 giờ”";
  - "Tuấn và Mai đã thả cảm xúc vào tin của bạn: …";
  - "Lan, Tuấn và 2 người khác đã thả cảm xúc vào tin của bạn: …" (người mới nhất trước).
- Thả thêm hay đổi cảm xúc khi chưa xem thì **sửa** thông báo đó, không kêu lại:
  - PC: toast có `Tag` là id tin; toast sau cùng tag thay toast cũ và có `<audio silent="true"/>`;
  - điện thoại: thông báo có tag `reaction:` cộng id tin, `SetOnlyAlertOnce`.
- Bỏ cảm xúc thì không báo gì.
- **Đã xem** nghĩa là mở hội thoại đó (`MarkReactionsSeenAsync`). Trên điện thoại thông báo tự mất; lần thả sau tạo thông báo mới.
- Không báo khi đang mở đúng hội thoại đó, hay khi nhóm đã Tắt thông báo. PC còn không báo khi đang "Bận", như tin nhắn.
- **Chưa làm:** danh sách hội thoại ghi "Mai đã thả ❤️" khi đó là việc mới nhất. Danh sách vẫn chỉ hiện tin mới nhất.

**Giao thức**
- `POST /chat/reactions` và `POST /chat/groups/{id}/reactions`, cùng thân `{ id, emoji?, at }` (`emoji` rỗng nghĩa là bỏ).
  - Máy nhận giữ cái có `at` mới hơn: cảm xúc cũ tới sau không đè được cái mới, kể cả khi đã bỏ (dòng bỏ vẫn giữ lại, không có emoji).
- **Nhóm không ký** như dự kiến. Cảm xúc không chuyển hộ, nên luôn đến thẳng từ người thả, và kết nối đã cho biết đó là ai. Chỉ nhận từ thành viên, và chỉ trên tin mình có.
- **Không chuyển hộ:**
  - người mới vào không thấy cảm xúc trên tin cũ;
  - cảm xúc tới trước tin (máy chưa có tin) thì bị bỏ.
  - Thành viên offline nhận khi online lại, từ chính người thả.
- **Hàng đợi** `ChatReactionsOut` / `ChatGroupReactionsOut` chỉ giữ id tin. Lúc gửi mới đọc cảm xúc mới nhất, nên đổi nhiều lần khi người kia offline chỉ gửi một lần. Bản cũ không có route này thì từ chối, và cảm xúc bị bỏ.
- **Lỗi chạy đua đã sửa (cả lệnh sửa của C6c):** dòng hàng đợi chỉ được xoá nếu cái vừa gửi vẫn là mới nhất. Trước đó, đổi cảm xúc (hay sửa tin) đúng lúc lần trước đang gửi thì lần sau bị mất.
- Lưu ở `ChatReactions` / `ChatGroupReactions` (hội thoại, `MessageId`, `ReactorId`, `Emoji`, `At`, `Unseen`), migration `0019_chat_reactions.sql`.
  - Tin bị thu hồi (ở cả hai phía), xóa tin hay xóa hội thoại thì xóa luôn cảm xúc của nó.

**Đã kiểm**:
- Test Engine:
  - 1-1: tới máy kia, thay, bỏ (không báo), cảm xúc lạ bị từ chối; cái cũ tới sau không đè cái mới (cả khi bỏ); xem rồi thì lần thả sau lại là chưa xem; thu hồi xóa cảm xúc ở cả hai phía.
  - Nhóm: tới mọi thành viên, mỗi người một; chỉ tác giả được báo; chờ thành viên offline và không chuyển hộ qua người khác; người ngoài nhóm bị từ chối; thu hồi xóa cảm xúc.
  - Sửa tin nhiều lần liền nhau thì máy kia ra chữ cuối; bỏ điều kiện chặn xoá hàng đợi thì test này báo lỗi.
- Test UI: chip (gộp, thứ tự, của mình, ai đã thả); chữ thông báo cho 1, 2 và nhiều người. Test Windows: toast thay thế không kêu.
- Thử thật PC và Nubia (2026-10-31; OPPO mất kết nối nên không thử được 3 máy):
  - 1-1, Nubia thả 👍 rồi đổi ❤️, 😂 vào tin của PC. Lịch sử toast của PC chỉ có **một** toast cho tin đó, toast sau cùng không kêu. Trên Nubia chỉ còn chip "😂 1".
  - PC thả ❤️ rồi đổi 🙏 vào tin của Nubia. Nubia có một thông báo (`ONLY_ALERT_ONCE`), chữ đổi theo. Mở hội thoại thì thông báo tự mất.
  - Chạm chip thì hiện "🙏 PC · MSI". Nubia chọn lại 😂 thì chip mất ở cả hai máy.
  - Nhóm tạm, theo cả hai chiều: thông báo trên Nubia "Nhóm C6d · PC · MSI đã thả ❤️…", toast trên PC "Nhóm C6d · Z2577 đã thả 😮…".

## 7. Không làm

**Đồng bộ lịch sử giữa các máy của cùng một người.** Mỗi cài đặt Shorekeeper là một **thiết bị** với khóa và DeviceId riêng ([04](04-identity-security.md)). App không có khái niệm "một người có nhiều máy".
- Nếu Huy có PC và điện thoại, người khác thấy đó là **hai** máy, phải Kết nối riêng từng máy.
- Tin Mai gửi cho PC của Huy chỉ nằm trên PC; điện thoại của Huy không thấy, và ngược lại.
- Sao lưu danh tính ([ADR-013](decisions.md)) chỉ chép **một** danh tính sang máy mới để thay máy cũ, không gộp hai máy.

Muốn đồng bộ thì phải có:
1. Khái niệm **người dùng** đứng trên các thiết bị: một khóa chính ký danh sách thiết bị của mình, và liên kết máy mới bằng mã QR.
2. Người khác nhận ra các máy đó là một người: Kết nối một lần cho cả nhóm thiết bị.
3. Mỗi tin gửi tới **mọi** máy của người nhận, kể cả tin mình gửi đi, để máy kia của mình cũng thấy.
4. Chép lịch sử cũ sang máy vừa liên kết.

Việc này đụng tới danh tính, Kết nối, quyền và mọi luồng chat, tức là gần như một hệ thống mới (như "thiết bị liên kết" của Signal). Không làm trong đợt này; nếu cần thì lên kế hoạch riêng.

**Khác**:
- Tìm trong tin thoại (cần chuyển giọng nói thành chữ).
- Lịch sử các lần sửa.
- Cảm xúc tùy chọn hay emoji tự do.
- Chuyển hộ tin của người đã rời nhóm ([ADR-016](decisions.md)).

## 8. Cần chốt

Chốt ngày 2026-10-31.

| | Câu hỏi | Chốt |
|---|---|---|
| Q1 | Sửa tin trong bao lâu? | **15 phút** sau khi tới (lâu hơn thu hồi 5 phút vì sửa không làm mất tin); còn chờ gửi thì lúc nào cũng được |
| Q2 | Bộ cảm xúc | **6 cái cố định** 👍 ❤️ 😂 😮 😢 🙏 |
| Q3 | Bị @nhắc thì có thông báo khi đã Tắt thông báo nhóm? | **Có**, như Messenger / Zalo |
| Q4 | Ai được dùng **@mọi người**? | **Mọi thành viên** (nhóm nhỏ trong LAN); có thể giới hạn cho Host sau |
| Q5 | Thả cảm xúc vào tin của mình thì có thông báo? | **Có, một thông báo cho mỗi tin**: thêm người thả thì sửa thông báo đó khi chưa xem, một người đổi nhiều lần chỉ tính một (§6) |
| Q6 | Thứ tự làm | C6a → C6b → C6c → C6d như §1 |

## 9. Rủi ro

| Rủi ro | Cách xử lý |
|---|---|
| Máy bản cũ trong nhóm bỏ tin ký `/2` | Không gửi tin `/2` cho máy chưa có `chat2`; người gửi thấy trạng thái `old` với người đó (§2) |
| Người chuyển hộ sửa `replyTo` / `mentions` | Nằm trong phần được ký `/2` |
| Giả lệnh sửa, cảm xúc hay nhắc tên của người khác | Lệnh sửa và nhắc tên ký bằng khóa tác giả, lệnh sửa phải khớp khóa của tin gốc; cảm xúc không chuyển hộ nên luôn đến thẳng từ người thả, được kết nối xác nhận |
| Spam @mọi người hay cảm xúc | Chung giới hạn 20 tin/giây mỗi máy ([16 §3.1](16-group-chat.md)); Tắt thông báo vẫn chặn được @mọi người nếu Q4 đổi thành chỉ Host |
| Tìm chậm khi lịch sử dài | Bắt đầu bằng `LIKE` trên cột đã chuẩn hóa; đo, chuyển FTS5 nếu cần |
| Trích dẫn tin quá cũ, máy mình không còn | Hiện "Tin nhắn không còn" thay vì để trống |
