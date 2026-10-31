-- Editing a text message (docs/18-chat-extras.md §5): only the newest words are kept. EditedAt: when its author last
-- changed it after it reached someone, on the author's clock. EditSig (groups): the author's signed edit, which a member
-- passing the message on sends with it, since the message's own signature no longer matches its words.
ALTER TABLE ChatMessages ADD COLUMN EditedAt INTEGER;
ALTER TABLE ChatGroupMessages ADD COLUMN EditedAt INTEGER;
ALTER TABLE ChatGroupMessages ADD COLUMN EditSig BLOB;

-- Edits not told yet, sent with the waiting messages: the newest words are read from the message then.
CREATE TABLE ChatEdits (
  PeerId TEXT NOT NULL,
  Id     TEXT NOT NULL,
  PRIMARY KEY (PeerId, Id)
);

CREATE TABLE ChatGroupEdits (
  GroupId TEXT NOT NULL,
  PeerId  TEXT NOT NULL,
  Id      TEXT NOT NULL,
  PRIMARY KEY (GroupId, PeerId, Id)
);
