-- Messages between two contacts (docs/15-chat.md §5). Ids are made by the sender, so they are unique per peer only.
CREATE TABLE ChatMessages (
  PeerId     TEXT NOT NULL,
  Id         TEXT NOT NULL,
  Outgoing   INTEGER NOT NULL,
  Kind       TEXT NOT NULL,              -- text | voice | a kind from a newer version
  Text       TEXT,
  VoicePath  TEXT,
  DurationMs INTEGER,
  Wave       BLOB,
  SentAt     INTEGER NOT NULL,           -- the sender's clock
  ReceivedAt INTEGER,
  State      TEXT NOT NULL,              -- sent: pending | delivered | read | failed; received: unread | read
  PRIMARY KEY (PeerId, Id)
);

CREATE INDEX IX_ChatMessages_SentAt ON ChatMessages (PeerId, SentAt);

-- "Đã xem" not told yet: the newest message from that peer read here.
CREATE TABLE ChatReceipts (
  PeerId   TEXT PRIMARY KEY,
  ReadUpTo TEXT NOT NULL
);
