# 09 — Lưu trữ dữ liệu

## 1. Thư mục (theo từng người dùng Windows)

```text
%LOCALAPPDATA%\Shorekeeper\
├── identity.pfx.dpapi              ← khóa thiết bị (DPAPI CurrentUser)
├── shorekeeper.db                  ← SQLite (WAL)
├── settings.json                   ← cài đặt người dùng (GPO ghi đè)
├── snapshots\{offerId}\...         ← bản sao tạm; xóa khi offer đóng
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
  AutoAcceptMaxBytes INTEGER,           -- NULL = tắt tự nhận
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
  ClosedAt   INTEGER
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
  ExpiresAt  INTEGER NOT NULL,
  State      TEXT NOT NULL,             -- new|seen|downloading|completed|partial|failed|declined|withdrawn|expired
  PRIMARY KEY (SenderId, OfferId)
);

CREATE TABLE InboxFiles (
  SenderId     TEXT NOT NULL,
  OfferId      TEXT NOT NULL,
  FileId       TEXT NOT NULL,
  RelativePath TEXT NOT NULL,
  Size         INTEGER NOT NULL,
  State        TEXT NOT NULL,           -- available|downloading|completed|failed|skipped
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
  JoinedAt     INTEGER NOT NULL
);

-- Host: nguồn sự thật. Thành viên: bản cache (dùng khi Host offline)
CREATE TABLE GroupMembers (
  GroupId TEXT NOT NULL, DeviceId TEXT NOT NULL, DisplayName TEXT,
  IsHost INTEGER NOT NULL, JoinedAt INTEGER NOT NULL,
  PRIMARY KEY (GroupId, DeviceId)
);

CREATE TABLE GroupJoinRequests (       -- chỉ ở Host
  GroupId TEXT NOT NULL, DeviceId TEXT NOT NULL, Note TEXT,
  State TEXT NOT NULL,                  -- pending|approved|declined
  DeclinedCount INTEGER NOT NULL DEFAULT 0,
  RequestedAt INTEGER NOT NULL,
  PRIMARY KEY (GroupId, DeviceId)
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
| `.skpart` | Xóa khi lỗi/hủy; dọn phần sót lúc khởi động |
| `Peers` Unknown không thấy lại | 30 ngày |
| `PeerEndpoints` không thành công | 14 ngày |
| `HashCache` | 30 ngày không dùng |
| `ActivityLog` | 90 ngày |
| Log | 14 ngày |

> **File đã nhận không bao giờ bị app xóa.** Xóa lịch sử chỉ xóa bản ghi.
