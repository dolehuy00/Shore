-- Group conversations (docs/16-group-chat.md §5): one per group, messages signed by their authors.
-- ClearedAt: "Xóa hội thoại", messages up to then never come back. HistoryBefore: set at the first pull from other
-- members; what they pass on from before it is history, already read and never notified. Muted: "Tắt thông báo".
CREATE TABLE ChatGroups (
  GroupId       TEXT PRIMARY KEY,
  Name          TEXT NOT NULL,
  ClearedAt     INTEGER,
  HistoryBefore INTEGER,
  Muted         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE ChatGroupMessages (
  GroupId    TEXT NOT NULL,
  Id         TEXT NOT NULL,
  AuthorId   TEXT NOT NULL,
  AuthorName TEXT NOT NULL,
  AuthorKey  BLOB NOT NULL,
  Outgoing   INTEGER NOT NULL,
  Kind       TEXT NOT NULL,
  Text       TEXT,
  SentAt     INTEGER NOT NULL,
  ReceivedAt INTEGER,
  State      TEXT NOT NULL,
  DurationMs INTEGER,
  Wave       BLOB,
  VoicePath  TEXT,
  Sig        BLOB NOT NULL,
  RecallSig  BLOB,
  -- Order of storing or taking back here, which members pull by (docs/16-group-chat.md §3.6).
  Seq        INTEGER NOT NULL,
  -- Deleted here: only its id stays, so nobody passes it on to us again.
  Deleted    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (GroupId, Id)
);

CREATE INDEX IX_ChatGroupMessages_Order ON ChatGroupMessages (GroupId, SentAt, Id);
CREATE INDEX IX_ChatGroupMessages_Seq ON ChatGroupMessages (GroupId, Seq);

-- Ours, one row per member it is for: pending | delivered | read | failed | old.
CREATE TABLE ChatGroupDeliveries (
  GroupId   TEXT NOT NULL,
  Id        TEXT NOT NULL,
  PeerId    TEXT NOT NULL,
  State     TEXT NOT NULL,
  RefusedAt INTEGER,
  PRIMARY KEY (GroupId, Id, PeerId)
);

CREATE INDEX IX_ChatGroupDeliveries_Peer ON ChatGroupDeliveries (PeerId, State);

-- "Đã xem" not told to the author yet.
CREATE TABLE ChatGroupReceipts (
  GroupId  TEXT NOT NULL,
  AuthorId TEXT NOT NULL,
  ReadUpTo TEXT NOT NULL,
  PRIMARY KEY (GroupId, AuthorId)
);

-- How far we have pulled from each member.
CREATE TABLE ChatGroupCursors (
  GroupId TEXT NOT NULL,
  PeerId  TEXT NOT NULL,
  After   INTEGER NOT NULL,
  PRIMARY KEY (GroupId, PeerId)
);

-- "Thu hồi" not told to a member yet, with the signed recall.
CREATE TABLE ChatGroupRecalls (
  GroupId TEXT NOT NULL,
  PeerId  TEXT NOT NULL,
  Id      TEXT NOT NULL,
  Sig     BLOB NOT NULL,
  PRIMARY KEY (GroupId, PeerId, Id)
);
