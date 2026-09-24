-- Initial schema. See docs/09-data-storage.md §2.
-- Times are Unix milliseconds (UTC).

-- ───────────── Peers & trust ─────────────
CREATE TABLE Peers (
  DeviceId           TEXT PRIMARY KEY,
  DisplayName        TEXT NOT NULL,
  HostName           TEXT,
  Alias              TEXT,
  TrustLevel         INTEGER NOT NULL DEFAULT 0,  -- 0 Unknown, 1 Trusted, 2 Blocked
  TrustedAt          INTEGER,
  PublicKey          BLOB,
  AutoAcceptMaxBytes INTEGER,
  DirectoryUpn       TEXT,
  DeclinedCount      INTEGER NOT NULL DEFAULT 0,
  MutedUntil         INTEGER,
  LastSeenAt         INTEGER,
  AppVersion         TEXT
);

CREATE TABLE PeerEndpoints (
  DeviceId      TEXT NOT NULL,
  Address       TEXT NOT NULL,
  Port          INTEGER NOT NULL,
  Source        TEXT NOT NULL,
  LastSeenAt    INTEGER,
  LastSuccessAt INTEGER,
  PRIMARY KEY (DeviceId, Address, Port)
);

CREATE TABLE ManualTargets (
  Id     INTEGER PRIMARY KEY,
  Target TEXT NOT NULL UNIQUE,
  Kind   TEXT NOT NULL
);

CREATE TABLE KnownBridges (
  Address       TEXT PRIMARY KEY,
  DeviceId      TEXT,
  Source        TEXT NOT NULL,
  LastSuccessAt INTEGER
);

-- ───────────── Sending ─────────────
CREATE TABLE Offers (
  OfferId    TEXT PRIMARY KEY,
  GroupId    TEXT,
  Note       TEXT,
  TotalBytes INTEGER NOT NULL,
  State      TEXT NOT NULL,
  CreatedAt  INTEGER NOT NULL,
  ExpiresAt  INTEGER NOT NULL,
  ClosedAt   INTEGER
);

CREATE TABLE OfferFiles (
  OfferId      TEXT NOT NULL REFERENCES Offers(OfferId) ON DELETE CASCADE,
  FileId       TEXT NOT NULL,
  RelativePath TEXT NOT NULL,
  Size         INTEGER NOT NULL,
  ModifiedAt   INTEGER,
  SourcePath   TEXT NOT NULL,
  SnapshotPath TEXT,
  Changed      INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (OfferId, FileId)
);

CREATE TABLE OfferRecipients (
  OfferId     TEXT NOT NULL REFERENCES Offers(OfferId) ON DELETE CASCADE,
  DeviceId    TEXT NOT NULL,
  State       TEXT NOT NULL,
  DeliveredAt INTEGER,
  UpdatedAt   INTEGER,
  PRIMARY KEY (OfferId, DeviceId)
);

CREATE TABLE HashCache (
  Path       TEXT NOT NULL,
  Size       INTEGER NOT NULL,
  ModifiedAt INTEGER NOT NULL,
  Sha256     TEXT NOT NULL,
  LastUsedAt INTEGER NOT NULL,
  PRIMARY KEY (Path, Size, ModifiedAt)
);

-- ───────────── Receiving (inbox) ─────────────
CREATE TABLE InboxOffers (
  SenderId   TEXT NOT NULL,
  OfferId    TEXT NOT NULL,
  GroupId    TEXT,
  Note       TEXT,
  TotalBytes INTEGER NOT NULL,
  ReceivedAt INTEGER NOT NULL,
  ExpiresAt  INTEGER NOT NULL,
  State      TEXT NOT NULL,
  PRIMARY KEY (SenderId, OfferId)
);

CREATE TABLE InboxFiles (
  SenderId     TEXT NOT NULL,
  OfferId      TEXT NOT NULL,
  FileId       TEXT NOT NULL,
  RelativePath TEXT NOT NULL,
  Size         INTEGER NOT NULL,
  State        TEXT NOT NULL,
  FinalPath    TEXT,
  Sha256       TEXT,
  Error        TEXT,
  PRIMARY KEY (SenderId, OfferId, FileId),
  FOREIGN KEY (SenderId, OfferId) REFERENCES InboxOffers(SenderId, OfferId) ON DELETE CASCADE
);

-- ───────────── Groups ─────────────
CREATE TABLE Groups (
  GroupId      TEXT PRIMARY KEY,
  Name         TEXT NOT NULL,
  HostDeviceId TEXT NOT NULL,
  IsHostedByMe INTEGER NOT NULL,
  CreatedAt    INTEGER NOT NULL,
  JoinedAt     INTEGER NOT NULL
);

CREATE TABLE GroupMembers (
  GroupId     TEXT NOT NULL REFERENCES Groups(GroupId) ON DELETE CASCADE,
  DeviceId    TEXT NOT NULL,
  DisplayName TEXT,
  IsHost      INTEGER NOT NULL,
  JoinedAt    INTEGER NOT NULL,
  PRIMARY KEY (GroupId, DeviceId)
);

CREATE TABLE GroupJoinRequests (
  GroupId       TEXT NOT NULL REFERENCES Groups(GroupId) ON DELETE CASCADE,
  DeviceId      TEXT NOT NULL,
  Note          TEXT,
  State         TEXT NOT NULL,
  DeclinedCount INTEGER NOT NULL DEFAULT 0,
  RequestedAt   INTEGER NOT NULL,
  PRIMARY KEY (GroupId, DeviceId)
);

-- ───────────── Activity log ─────────────
CREATE TABLE ActivityLog (
  Id      INTEGER PRIMARY KEY,
  At      INTEGER NOT NULL,
  Kind    TEXT NOT NULL,
  PeerId  TEXT,
  GroupId TEXT,
  Detail  TEXT
);

CREATE INDEX IX_ActivityLog_At ON ActivityLog (At);
CREATE INDEX IX_InboxOffers_State ON InboxOffers (State);
CREATE INDEX IX_Offers_State ON Offers (State);
