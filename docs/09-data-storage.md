# 09 — Lưu trữ dữ liệu

## 1. Thư mục (theo từng người dùng Windows)

```text
%LOCALAPPDATA%\Shorekeeper\
├── identity.pfx.dpapi              ← khóa thiết bị (DPAPI CurrentUser)
├── shorekeeper.db                  ← SQLite (WAL)
├── settings.json                   ← cài đặt người dùng (GPO ghi đè); mật khẩu nghe chỉ ở dạng hash
├── audio-passwords.bin             ← mật khẩu nghe đã dùng được, theo máy phát (DPAPI CurrentUser)
├── directory.json, avatar.img      ← hồ sơ AD lần đọc gần nhất (tên, phòng ban, ảnh), M6b
├── restore.pending                 ← bản sao lưu chờ áp dụng ở lần khởi động sau (mã hóa như khóa), M6b
├── identity.pfx.dpapi.replaced-*   ← khóa cũ, giữ lại sau khi khôi phục
├── snapshots\{offerId}\...         ← bản sao tạm; xóa khi offer đóng
├── chat\voice\{id}.ogg              ← tin nhắn thoại gửi và nhận (docs/15-chat.md §3)
└── logs\shorekeeper-YYYYMMDD.log

%USERPROFILE%\Downloads\Shorekeeper\   ← file nhận được (vĩnh viễn, app không tự xóa)
```

## 2. SQLite

- WAL, `synchronous=NORMAL`, một connection ghi qua hàng đợi, nhiều connection đọc.
- Migration: bảng `SchemaVersion` + script nhúng `src/Shorekeeper.Engine/Storage/Migrations/NNNN_mo_ta.sql`, mỗi script một transaction. Schema dưới đây là `0001_initial.sql`. Trước bản phát hành đầu tiên có thể sửa thẳng file này; sau đó chỉ thêm migration mới.
- Thời gian là `INTEGER` Unix ms UTC.

```sql
-- ───────────── Peer & tin cậy ─────────────
CREATE TABLE Peers (
  DeviceId           TEXT PRIMARY KEY,
  DisplayName        TEXT NOT NULL,
  HostName           TEXT,
  Alias              TEXT,
  TrustLevel         INTEGER NOT NULL,  -- 0 Unknown, 1 Trusted, 2 Blocked
  TrustedAt          INTEGER,
  PublicKey          BLOB,
  AutoAcceptMaxBytes INTEGER,           -- NULL = tắt tự nhận, -1 = theo giới hạn trong Cài đặt
  DirectoryUpn       TEXT,              -- nếu xác minh qua AD (13)
  DeclinedCount      INTEGER NOT NULL DEFAULT 0,  -- chống spam yêu cầu
  MutedUntil         INTEGER,
  LastSeenAt         INTEGER,
  AppVersion         TEXT
);

CREATE TABLE PeerEndpoints (
  DeviceId      TEXT NOT NULL,
  Address       TEXT NOT NULL,
  Port          INTEGER NOT NULL,
  Source        TEXT NOT NULL,          -- multicast|known|pex|group|bridge|probe|manual
  LastSeenAt    INTEGER,
  LastSuccessAt INTEGER,
  PRIMARY KEY (DeviceId, Address, Port)
);

CREATE TABLE ManualTargets (
  Id INTEGER PRIMARY KEY, Target TEXT NOT NULL UNIQUE, Kind TEXT NOT NULL  -- host|cidr
);

CREATE TABLE KnownBridges (
  Address TEXT PRIMARY KEY, DeviceId TEXT, Source TEXT NOT NULL, LastSuccessAt INTEGER
);

-- ───────────── Phía GỬI ─────────────
CREATE TABLE Offers (
  OfferId    TEXT PRIMARY KEY,
  GroupId    TEXT,                      -- nếu chọn người nhận từ nhóm
  Note       TEXT,
  TotalBytes INTEGER NOT NULL,
  State      TEXT NOT NULL,             -- open|withdrawn|expired|done
  CreatedAt  INTEGER NOT NULL,
  ExpiresAt  INTEGER NOT NULL,
  ClosedAt   INTEGER,
  Recalled   INTEGER NOT NULL DEFAULT 0 -- 0009: "Thu hồi" trong Tin nhắn, thẻ thành ghi chú
);

CREATE TABLE OfferFiles (
  OfferId      TEXT NOT NULL,
  FileId       TEXT NOT NULL,
  RelativePath TEXT NOT NULL,
  Size         INTEGER NOT NULL,
  ModifiedAt   INTEGER,
  SourcePath   TEXT NOT NULL,
  SnapshotPath TEXT,
  Changed      INTEGER NOT NULL DEFAULT 0,  -- file gốc đã bị sửa sau khi gửi
  PRIMARY KEY (OfferId, FileId)
);

CREATE TABLE OfferRecipients (
  OfferId     TEXT NOT NULL,
  DeviceId    TEXT NOT NULL,
  State       TEXT NOT NULL,            -- pending|delivered|downloading|completed|failed|declined
  DeliveredAt INTEGER,
  UpdatedAt   INTEGER,
  PRIMARY KEY (OfferId, DeviceId)
);

CREATE TABLE HashCache (
  Path TEXT NOT NULL, Size INTEGER NOT NULL, ModifiedAt INTEGER NOT NULL, Sha256 TEXT NOT NULL,
  LastUsedAt INTEGER NOT NULL,          -- để dọn mục không dùng 30 ngày
  PRIMARY KEY (Path, Size, ModifiedAt)
);

-- ───────────── Phía NHẬN (Hộp nhận) ─────────────
CREATE TABLE InboxOffers (
  OfferId    TEXT NOT NULL,
  SenderId   TEXT NOT NULL,
  GroupId    TEXT,
  Note       TEXT,
  TotalBytes INTEGER NOT NULL,
  ReceivedAt INTEGER NOT NULL,
  SentAt     INTEGER,                   -- 0008: createdAt của người gửi (không muộn hơn ReceivedAt), xếp thẻ trong Tin nhắn; trống thì dùng ReceivedAt
  ExpiresAt  INTEGER NOT NULL,
  State      TEXT NOT NULL,             -- new|seen|downloading|completed|partial|failed|declined|withdrawn|expired
  Recalled   INTEGER NOT NULL DEFAULT 0, -- 0009: người gửi thu hồi trong Tin nhắn
  ChatPicture INTEGER NOT NULL DEFAULT 0, -- 0009: ảnh tự tải để hiện trong Tin nhắn; thu hồi thì xóa file
  PRIMARY KEY (SenderId, OfferId)
);

CREATE TABLE InboxFiles (
  SenderId     TEXT NOT NULL,
  OfferId      TEXT NOT NULL,
  FileId       TEXT NOT NULL,
  RelativePath TEXT NOT NULL,
  Size         INTEGER NOT NULL,
  ModifiedAt   INTEGER,                 -- thêm ở 0002: để khôi phục thời gian sửa file khi tải lại
  State        TEXT NOT NULL,           -- available|downloading|completed|failed
  FinalPath    TEXT,
  Sha256       TEXT,
  Error        TEXT,
  PRIMARY KEY (SenderId, OfferId, FileId)
);

-- ───────────── Nhóm ─────────────
CREATE TABLE Groups (
  GroupId      TEXT PRIMARY KEY,
  Name         TEXT NOT NULL,
  HostDeviceId TEXT NOT NULL,
  IsHostedByMe INTEGER NOT NULL,
  CreatedAt    INTEGER NOT NULL,
  JoinedAt     INTEGER NOT NULL,
  State        TEXT NOT NULL DEFAULT 'member',  -- 0003: member | pending (đã xin, chờ Host duyệt)
  HostOffsetMs INTEGER NOT NULL DEFAULT 0       -- 0014: đồng hồ Host nhanh hơn máy này bao nhiêu (docs/07-groups.md §4.1)
);

-- Host: nguồn sự thật. Thành viên: bản cache (dùng khi Host offline)
CREATE TABLE GroupMembers (
  GroupId TEXT NOT NULL, DeviceId TEXT NOT NULL, DisplayName TEXT,
  IsHost INTEGER NOT NULL, JoinedAt INTEGER NOT NULL,
  PRIMARY KEY (GroupId, DeviceId)
);

-- 0013: danh sách Host đã ký (docs/07-groups.md §4.1), bản mới nhất mỗi nhóm, giữ nguyên JSON để đưa lại và kiểm lại
CREATE TABLE GroupRosters (
  GroupId TEXT PRIMARY KEY, Version INTEGER NOT NULL, Roster TEXT NOT NULL
);

CREATE TABLE GroupJoinRequests (       -- chỉ ở Host
  GroupId TEXT NOT NULL, DeviceId TEXT NOT NULL, Note TEXT,
  State TEXT NOT NULL,                  -- pending | invited (Host đã mời = duyệt trước) | declined
  DeclinedCount INTEGER NOT NULL DEFAULT 0,
  RequestedAt INTEGER NOT NULL,         -- với declined: lúc từ chối, tính khóa 24 giờ
  DisplayName TEXT, HostName TEXT,      -- 0003: để Host biết ai xin
  PRIMARY KEY (GroupId, DeviceId)
);

-- ───────────── Tin nhắn (0006, docs/15-chat.md) ─────────────
CREATE TABLE ChatMessages (
  PeerId TEXT NOT NULL, Id TEXT NOT NULL,   -- Id do người gửi tạo, chỉ duy nhất theo từng máy
  Outgoing INTEGER NOT NULL,
  Kind TEXT NOT NULL,                       -- text | voice | recalled (0007) | loại của bản sau
  Text TEXT, VoicePath TEXT, DurationMs INTEGER, Wave BLOB,
  SentAt INTEGER NOT NULL,                  -- giờ máy gửi, dùng để xếp
  ReceivedAt INTEGER,
  State TEXT NOT NULL,                      -- gửi: pending | delivered | read | failed; nhận: unread | read
  PRIMARY KEY (PeerId, Id)
);

CREATE TABLE ChatReceipts (                 -- "Đã xem" chưa báo được cho máy kia
  PeerId TEXT PRIMARY KEY, ReadUpTo TEXT NOT NULL
);

CREATE TABLE ChatRecalls (                  -- 0007: thu hồi chưa báo được cho máy kia
  PeerId TEXT NOT NULL, Id TEXT NOT NULL,
  IsOffer INTEGER NOT NULL DEFAULT 0,       -- 0009: Id là offer chứ không phải tin
  PRIMARY KEY (PeerId, Id)
);

CREATE TABLE ChatCleared (                  -- 0010: "Xóa hội thoại"; thẻ file tới lúc này không hiện trong hội thoại
  PeerId TEXT PRIMARY KEY, ClearedAt INTEGER NOT NULL
);

-- ───────────── Tin nhóm (0011, docs/16-group-chat.md §5) ─────────────
CREATE TABLE ChatGroups (                   -- tên nhóm, giữ cho hội thoại cả khi nhóm không còn
  GroupId TEXT PRIMARY KEY, Name TEXT NOT NULL,
  ClearedAt INTEGER,                        -- "Xóa hội thoại": tin tới lúc này không quay lại
  HistoryBefore INTEGER,                    -- lần kéo đầu: tin chuyển hộ từ trước đó là tin cũ, đã đọc
  Muted INTEGER NOT NULL DEFAULT 0          -- "Tắt thông báo"
);

CREATE TABLE ChatGroupMessages (
  GroupId TEXT NOT NULL, Id TEXT NOT NULL,
  AuthorId TEXT NOT NULL, AuthorName TEXT NOT NULL,
  AuthorKey BLOB NOT NULL,                  -- SubjectPublicKeyInfo của tác giả, để kiểm và chuyển tiếp
  Outgoing INTEGER NOT NULL, Kind TEXT NOT NULL, Text TEXT,
  SentAt INTEGER NOT NULL, ReceivedAt INTEGER,
  State TEXT NOT NULL,                      -- tin nhận: unread|read; tin mình: theo ChatGroupDeliveries
  DurationMs INTEGER, Wave BLOB, VoicePath TEXT,
  Sig BLOB NOT NULL, RecallSig BLOB,        -- chữ ký tin và lệnh thu hồi của tác giả
  Seq INTEGER NOT NULL,                     -- thứ tự lưu / thu hồi ở máy này, thành viên kéo theo nó
  Deleted INTEGER NOT NULL DEFAULT 0,       -- đã xóa ở đây: chỉ còn id, để không ai đưa lại
  PRIMARY KEY (GroupId, Id)
);

CREATE TABLE ChatGroupCursors (             -- đã kéo tới Seq nào ở mỗi thành viên
  GroupId TEXT NOT NULL, PeerId TEXT NOT NULL, After INTEGER NOT NULL,
  PRIMARY KEY (GroupId, PeerId)
);

CREATE TABLE ChatGroupDeliveries (          -- tin mình gửi, mỗi thành viên một dòng
  GroupId TEXT NOT NULL, Id TEXT NOT NULL, PeerId TEXT NOT NULL,
  State TEXT NOT NULL,                      -- pending|delivered|read|failed|old
  RefusedAt INTEGER,                        -- lần đầu bị trả not_member: thử lại 2 phút
  PRIMARY KEY (GroupId, Id, PeerId)
);

CREATE TABLE ChatGroupReceipts (            -- "Đã xem" chưa báo được cho tác giả
  GroupId TEXT NOT NULL, AuthorId TEXT NOT NULL, ReadUpTo TEXT NOT NULL,
  PRIMARY KEY (GroupId, AuthorId)
);

CREATE TABLE ChatGroupRecalls (             -- thu hồi chưa báo được cho thành viên
  GroupId TEXT NOT NULL, PeerId TEXT NOT NULL, Id TEXT NOT NULL, Sig BLOB NOT NULL,
  IsOffer INTEGER NOT NULL DEFAULT 0,       -- 0012: Id là offer gửi cho nhóm (không chữ ký)
  PRIMARY KEY (GroupId, PeerId, Id)
);

-- ───────────── Nhật ký ─────────────
CREATE TABLE ActivityLog (
  Id INTEGER PRIMARY KEY, At INTEGER NOT NULL, Kind TEXT NOT NULL,
  PeerId TEXT, GroupId TEXT, Detail TEXT
);
```

> Trạng thái tải dở (`.skpart`, số byte, hash state) **chỉ nằm trong bộ nhớ**, không lưu DB, vì không tải tiếp qua lần khởi động lại.

## 3. Dọn dẹp

| Dữ liệu | Giữ bao lâu |
|---|---|
| `Offers` đã đóng | 90 ngày (lịch sử) |
| `InboxOffers` đã xong/hết hạn | 90 ngày (người dùng xóa tay được) |
| `snapshots\` | Xóa khi offer đóng; dọn phần sót lúc khởi động |
| `ChatMessages` | Không tự xóa; người dùng xóa từng tin hoặc cả hội thoại (kèm file thoại) |
| `.skpart` | Xóa khi lỗi/hủy; dọn phần sót lúc khởi động |
| `Peers` Unknown không thấy lại | 30 ngày |
| `PeerEndpoints` không thành công | 14 ngày |
| `HashCache` | 30 ngày không dùng |
| `ActivityLog` | 90 ngày |
| Log | 14 ngày |

> **File đã nhận không bao giờ bị app xóa.** Xóa lịch sử chỉ xóa bản ghi.
