-- Group conversations (docs/16-group-chat.md §5): one per group, messages signed by their authors.
CREATE TABLE ChatGroups (
  GroupId TEXT PRIMARY KEY,
  Name    TEXT NOT NULL
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
  PRIMARY KEY (GroupId, Id)
);

CREATE INDEX IX_ChatGroupMessages_Order ON ChatGroupMessages (GroupId, SentAt, Id);

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

-- "Thu hồi" not told to a member yet, with the signed recall.
CREATE TABLE ChatGroupRecalls (
  GroupId TEXT NOT NULL,
  PeerId  TEXT NOT NULL,
  Id      TEXT NOT NULL,
  Sig     BLOB NOT NULL,
  PRIMARY KEY (GroupId, PeerId, Id)
);
