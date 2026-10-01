# 05 — Giao thức

## 1. Cổng & kênh

| Kênh | Giao thức | Cổng | Dùng cho |
|---|---|---|---|
| Discovery | UDP multicast `239.255.47.47` + unicast | `47470/udp` | announce, heartbeat, reply, bye, probe |
| API | HTTPS (TLS 1.2/1.3 theo hệ điều hành, mTLS), HTTP/1.1 | `47471/tcp` | mọi thứ còn lại |

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
| `proto`, `caps` | Phiên bản API hỗ trợ, khả năng | M2 |
| `groups` | Nhóm mà máy này Host (tối đa 5) | M5 |
| `bridges` | Bridge mà peer đang biết | M4 |

- Mọi gói (kể cả `heartbeat`) mang **đầy đủ** presence. Gói ~300 byte, nên không cần bản rút gọn.
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
| GET | `/peers/known` | Trusted | PEX |

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

### 4.3 Gửi file — endpoint trên **máy người gửi**

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| GET | `/offers/{offerId}` | Recipient | Manifest + trạng thái offer |
| GET | `/offers/{offerId}/files/{fileId}` | Recipient | Dữ liệu; hỗ trợ `Range`, `If-Range`, `ETag` |
| GET | `/offers/{offerId}/files/{fileId}/hash` | Recipient | `{ sha256 }` (`202` nếu đang tính) |
| POST | `/offers/{offerId}/receipts` | Recipient | `{ status: "downloading" \| "completed" \| "failed" \| "declined", fileIds? }` |

`Recipient` = DeviceId nằm trong `recipients` của offer **và** vẫn còn quyền (Trusted, hoặc còn là thành viên nhóm của offer).

### 4.4 Nhóm — endpoint trên **Host**

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/groups/{groupId}/join-requests` | Any (rate limit) | Xin vào |
| POST | `/groups/{groupId}/join` | Người được mời | Chấp nhận lời mời `{ invitationId }` |
| POST | `/groups/{groupId}/leave` | GroupMember | Rời nhóm |
| GET | `/groups/{groupId}/members` | GroupMember | Snapshot thành viên (+ địa chỉ, online) |
| GET | `/groups/{groupId}/events` | GroupMember | SSE: `member-joined`, `member-left`, `member-online`, `member-offline`, `group-renamed`, `group-closed` |

### 4.5 Nhóm — endpoint trên **máy thành viên / người xin**

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/groups/invitations` | Any (rate limit) | Host mời máy này; người dùng đồng ý hoặc không |
| POST | `/groups/{groupId}/joined` | Host của nhóm | Host báo đã duyệt, kèm snapshot thành viên |
| POST | `/groups/{groupId}/join-declined` | Host của nhóm | Host từ chối |

### 4.6 Bridge (khi bật)

| Method | Path | Quyền | Mô tả |
|---|---|---|---|
| POST | `/bridge/register` | Any | Đăng ký/gia hạn → `{ leaseSeconds, observedAddr }` |
| DELETE | `/bridge/register` | Any | Hủy đăng ký |
| GET | `/bridge/peers/stream` | Any | SSE: `snapshot`, `upsert`, `remove` (chỉ presence công khai) |

Bridge chỉ trả thông tin **vốn đã công khai** qua presence (tên, địa chỉ, nhóm), nên `Any` là đủ.

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
