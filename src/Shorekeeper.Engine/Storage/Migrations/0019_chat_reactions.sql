-- Reactions (docs/18-chat-extras.md §6): one per person and message, the newest by its time. Emoji NULL: taken away,
-- kept so one older arriving late cannot bring it back. Unseen: someone else's on our message, not seen yet; the
-- notification for that message lists these.
CREATE TABLE ChatReactions (
  PeerId    TEXT NOT NULL,
  MessageId TEXT NOT NULL,
  ReactorId TEXT NOT NULL,
  Emoji     TEXT,
  At        INTEGER NOT NULL,
  Unseen    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (PeerId, MessageId, ReactorId)
);

CREATE TABLE ChatGroupReactions (
  GroupId   TEXT NOT NULL,
  MessageId TEXT NOT NULL,
  ReactorId TEXT NOT NULL,
  Emoji     TEXT,
  At        INTEGER NOT NULL,
  Unseen    INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (GroupId, MessageId, ReactorId)
);

-- Our reactions not told yet, sent with the waiting messages: the newest is read from the tables above then.
CREATE TABLE ChatReactionsOut (
  PeerId    TEXT NOT NULL,
  MessageId TEXT NOT NULL,
  PRIMARY KEY (PeerId, MessageId)
);

CREATE TABLE ChatGroupReactionsOut (
  GroupId   TEXT NOT NULL,
  PeerId    TEXT NOT NULL,
  MessageId TEXT NOT NULL,
  PRIMARY KEY (GroupId, PeerId, MessageId)
);
