# 05 — Giao thức

## 1. Cổng & kênh

| Kênh | Giao thức | Cổng | Dùng cho |
|---|---|---|---|
| Discovery | UDP multicast `239.255.47.47` + unicast | `47470/udp` | announce, heartbeat, reply, bye, probe |
| API | HTTPS (TLS 1.2/1.3 theo hệ điều hành, mTLS), HTTP/1.1 | `47471/tcp` | mọi thứ còn lại |
| Âm thanh | UDP unicast, AES-GCM | cổng động của máy nghe | "Phát âm thanh" (§4.7) |

- Cổng 47471 bận (phiên người dùng thứ 2 trên cùng máy) thì dùng cổng động, quảng bá qua `port` trong presence.
- Đổi được qua GPO, nhưng toàn mạng phải dùng **cùng cổng UDP**.

## 2. Gói UDP

```text
┌────────┬──────────┬──────────────────────────────┐
│ "SKP1" │ reserved │ JSON payload (UTF-8)         │
│ 4 byte │ 2 byte   │ ≤ 1194 byte                  │
└────────┴──────────┴──────────────────────────────┘
```

```json
{
  "v": 1,
  "type": "announce",
  "id": "k3f92mxa7q...",
  "name": "Lê Huy · PC-DEV-03",
  "host": "PC-DEV-03",
  "os": "windows",
  "app": "0.1.0",
  "port": 47471,
  "addrs": ["10.1.1.23"],
  "status": "available"
}
```

| Trường | Ý nghĩa | Có từ |
|---|---|---|
| `v` | Phiên bản định dạng gói; khác `1` thì bỏ qua | M1 |
| `type` | `announce` \| `heartbeat` \| `reply` \| `bye` (`probe` từ M2) | M1 |
| `id` | DeviceId (bắt buộc, sai định dạng thì bỏ gói) | M1 |
| `name` | Tên hiển thị, tối đa 64 ký tự | M1 |
| `host`, `os`, `app` | Tên máy, hệ điều hành, phiên bản app | M1 |
| `port` | Cổng HTTPS API | M1 (API có từ M2) |
| `addrs` | Các IPv4 của máy | M1 |
| `status` | `available` \| `busy` | M1 |
| `caps` | Khả năng: `bridge` khi máy đang làm cầu nối | M4 |
| `proto` | Phiên bản API hỗ trợ | (dự kiến) |
| `groups` | Nhóm mà máy này Host (tối đa 5): `[{ id, name, n }]`, `n` = số thành viên | M5 |
| `dept` | Phòng ban trong AD, tối đa 40 ký tự | M6b |
| `av` | Có ảnh ở `GET /hello/avatar`; 8 ký tự hex, đổi khi ảnh đổi | M6b |
| `bridges` | Bridge mà peer đang biết | (bỏ: PEX đã mang danh sách Bridge, gói UDP giữ gọn) |

- Mọi gói (kể cả `heartbeat`) mang **đầy đủ** presence. Gói ~300 byte, nên không cần bản rút gọn. Gói vượt 1200 byte thì bỏ bớt nhóm (từ cuối), rồi tới `dept`.
- `dept` dài quá hoặc `av` sai định dạng thì bỏ cả gói, như `name`.
- Trường thiếu thì dùng giá trị mặc định; trường lạ thì bỏ qua. Nhờ vậy thêm trường ở milestone sau không làm hỏng peer cũ.

> Presence công khai tên và nhóm. Đó là thông tin **ai trong LAN cũng thấy**, cần thiết để xin kết nối hoặc xin vào nhóm.

## 3. HTTP API — quy ước

- Base path `/api/v1`, mTLS, JSON camelCase, thời gian ISO-8601 UTC, ID là ULID.
- **Mỗi endpoint khai báo mức quyền tối thiểu** (`Any`, `GroupMember`, `Trusted`, `Recipient`, `Host`). Middleware kiểm tra trước khi vào handler ([04 §3](04-identity-security.md)).
- Lỗi dạng `application/problem+json`:

```json
{ "status": 403, "code": "not_trusted", "title": "Not trusted" }
```

| `code` | HTTP | Ý nghĩa |
|---|---|---|
| `not_trusted` | 403 | Cần Kết nối (hoặc cùng nhóm) trước |
| `not_recipient` | 403 | Không nằm trong danh sách người nhận của offer |
| `blocked` | 403 | Bị chặn (trả giống `not_trusted` để không lộ thông tin) |
| `unsupported_version` | 426 | Phiên bản không tương thích |
| `rate_limited` | 429 | Gửi yêu cầu quá nhiều |
| `not_found` | 404 | Offer/file/nhóm không tồn tại |
| `offer_closed` | 410 | Offer đã bị thu hồi / hết hạn |
| `source_changed` | 412 | File nguồn bị đổi (`If-Range`/ETag không khớp) |

`GET /api/v1/hello` luôn ổn định, dùng để thương lượng phiên bản.

## 4. Endpoint

### 4.1 Chung & Kết nối

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| GET | `/hello` | Any | Tên, phiên bản, `proto`, `caps` |
| GET | `/hello/avatar` | Any | Ảnh đại diện (AD mức 1, [13](13-active-directory.md)) |
| GET | `/identity/attestation?nonce=` | Any | Chứng thực bằng cert AD (AD mức 3) |
| POST | `/pairing/request` | Any (rate limit) | `{ pairingId, nonce, name, host, apiPort, note }` → `202 { nonce, name, host }`. Gửi lại cùng `pairingId` thì nhận lại cùng câu trả lời |
| GET | `/pairing/{id}/decision` | Any (chỉ người xin) | Long-poll tối đa 25 giây → `{ status: pending \| accepted \| declined \| expired \| cancelled }` |
| DELETE | `/pairing/{id}` | Any (chỉ người xin) | Người xin hủy |
| POST | `/pairing/revoke` | Trusted | Ngắt kết nối: bên nhận quên người gọi |
| GET | `/peers/known` | Trusted | PEX → `{ peers: [{ deviceId, name, address, discoveryPort }], bridges: [{ deviceId, address, port }] }`. Chỉ peer mình nghe trực tiếp, không gồm người gọi |

### 4.2 Gửi file — endpoint trên **máy người nhận** (Hộp nhận)

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/inbox/offers` | Trusted, hoặc GroupMember (nếu offer có `groupId`) | Giao offer |
| POST | `/inbox/offers/{offerId}/withdrawn` | Người gửi của offer | Người gửi thu hồi |

```json
{
  "offerId": "01J8Z4...",
  "createdAt": "2026-09-23T08:15:00Z",
  "expiresAt": "2026-09-24T08:15:00Z",
  "groupId": null,
  "note": "Bản build 1.4.2",
  "totalSize": 2576980377,
  "files": [
    { "fileId": "f1", "relativePath": "build/app-1.4.2.zip", "size": 2576980377, "modifiedAt": "2026-09-23T07:59:12Z" },
    { "fileId": "d1", "relativePath": "build/logs/", "size": 0, "isDirectory": true }
  ]
}
```

`note` dài tối đa 20 000 ký tự. **Gửi văn bản** (một link, một đoạn chữ) là offer có `note` và `files` rỗng: người nhận coi như đã nhận ngay khi offer tới (không có gì để tải, không gửi receipt), người gửi đánh dấu người nhận là `completed` khi giao offer thành công. Offer không có cả file lẫn `note` bị từ chối.

### 4.3 Gửi file — endpoint trên **máy người gửi**

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| GET | `/offers/{offerId}/files/{fileId}` | Recipient | Dữ liệu; hỗ trợ `Range`, `If-Range`, `ETag` |
| GET | `/offers/{offerId}/files/{fileId}/hash` | Recipient | `{ sha256 }`. Server chờ hash nền tối đa 10 giây rồi mới trả `202` (đang tính, hỏi lại sau) |
| POST | `/offers/{offerId}/receipts` | Recipient | `{ status: "downloading" \| "completed" \| "partial" \| "failed" \| "declined" }`. `partial` = đã lấy file mình chọn, offer vẫn mở |

`Recipient` = DeviceId nằm trong `recipients` của offer **và** vẫn còn quyền (Trusted, hoặc còn là thành viên nhóm của offer).

### 4.4 Nhóm — endpoint trên **Host**

Quyền `Any` ở middleware; handler tự quyết theo DeviceId người gọi. Ai không liên quan đều nhận `404`, không biết thêm gì.

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/groups/{groupId}/join-requests` | Any (rate limit) | `{ name, host, note }` → `202 { status: "pending" }`, hoặc `200 { status: "member" }` nếu đã được mời / đã là thành viên. `429` khi gửi nhiều hoặc bị từ chối 3 lần trong 24 giờ, `503` khi nhóm đủ 100 người |
| GET | `/groups/{groupId}/state?since=&wait=` | Thành viên / người đang xin | Long-poll tối đa 25 giây → `{ status, version, name, members: [{ deviceId, name, isHost, address, discoveryPort }] }`. `status`: `member` (kèm danh sách, chờ đến khi `version` khác `since`), `pending` (chờ quyết định), `declined`. Người khác, người bị loại, nhóm đã đóng: `404` |
| POST | `/groups/{groupId}/leave` | Any | Rời nhóm, hoặc rút yêu cầu đang chờ |

### 4.5 Nhóm — endpoint trên **người được mời**

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/groups/invitations` | Any (rate limit) | `{ groupId, groupName, hostName }` → `202`. Người gọi là Host. Đồng ý = gửi `join-requests`, Host cho vào ngay |

Endpoint gửi file (§4.2, §4.3) dùng quyền **Trusted hoặc cùng nhóm**: middleware cho qua khi người gọi là Trusted hoặc có chung ít nhất một nhóm, handler kiểm tra đúng nhóm của offer (`groupId`).

### 4.6 Bridge (khi bật)

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/bridge/register` | Any | `{ name, host, os, app, status, port }` → `{ leaseSeconds: 60, observedAddr }`. DeviceId lấy từ chứng chỉ TLS. Renew mỗi 20 giây |
| DELETE | `/bridge/register` | Any | Hủy đăng ký |
| GET | `/bridge/peers?since=&wait=` | Any | Long-poll tối đa 25 giây → `{ version, full, peers: [{ deviceId, name, host, os, app, status, address, port }], removed: [deviceId] }`. `since=0` hoặc quá cũ → `full: true` |

Bridge chỉ trả thông tin **vốn đã công khai** qua presence (tên, địa chỉ, nhóm), nên `Any` là đủ. Máy không bật Bridge trả `404 not_found`; đầy (2.000 máy) trả `503 busy`.

### 4.7 Âm thanh ("Phát âm thanh" / "Nghe")

Máy tính bật **Phát âm thanh** thì presence có capability `audio`; liên hệ thấy và tự bấm **Nghe**. Hai hành động đó là sự đồng ý của hai bên, không có hộp hỏi thêm.

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/audio/listeners` | Trusted | Trên máy phát. `{ sessionId, key, port }`: `key` là 32 byte (base64) máy nghe vừa tạo, `port` là cổng UDP máy nghe đang mở ở địa chỉ gọi tới → `204`. Chưa bật phát (hoặc không thu được loa) `404`; đủ 4 máy nghe `503 busy`. Gọi lại thì thay phiên cũ |
| DELETE | `/audio/listeners/{sessionId}` | Trusted | Máy nghe dừng nghe |
| POST | `/audio/listening/{sessionId}/ended` | Trusted | Trên máy nghe: máy phát đã tắt phát |

**Luồng UDP** (máy phát → máy nghe), mỗi datagram một khung 10 ms:

- Âm thanh loa (WASAPI loopback) 48 kHz stereo, Opus "restricted low delay" 128 kbps CBR. Chỉ thu khi có người nghe; nén một lần, niêm phong riêng cho từng máy nghe.
- Datagram: `version (1) · sequence (4) · sender time µs (8)` để rõ, sau đó `khung (2 + n) · khung trước (2 + n)` mã hóa AES-GCM (nonce = loại + sequence, header là associated data), tag 16 byte. Mất một gói thì phát từ bản sao trong gói sau.
- Máy nghe giữ khung trong bộ đệm jitter: **Thấp** 30 ms, **Ổn định** 80 ms. Khi luồng đến trễ (Wi-Fi điện thoại quét mạng làm gián đoạn 50–140 ms), máy nghe chờ thay vì bỏ khung, tạm phát âm Opus đoán và nâng mức đệm (tối đa 120 ms); vài giây yên ổn thì hạ dần về mức gốc.
- Mỗi giây máy nghe gửi về một keep-alive (`0xA1 · counter (4) · 1 byte niêm phong · tag`) tới đúng cổng nguồn của luồng. Máy phát bỏ máy nghe sau 6 giây không có keep-alive; máy nghe dừng sau 8 giây không có âm thanh.
- Máy phát giữ Wi-Fi ở media streaming mode và tắt background scan khi đang thu (`WlanSetInterface`).

## 5. Timeout & giới hạn

| Tham số | Giá trị |
|---|---|
| TCP connect | 3 giây / ứng viên |
| Request điều khiển | 10 giây |
| Stream: không có byte mới | 15 giây → Reconnecting |
| Cửa sổ kết nối lại | 60 giây (15–300, cấu hình được) |
| Offer mở tối đa | 24 giờ (cấu hình được) |
| JSON request tối đa | 1 MB (≤ 10.000 file/offer) |
| File song song / người nhận | 4 |
| Stream đồng thời phía người gửi | 16 |
