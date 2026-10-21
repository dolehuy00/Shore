-- "Thu hồi" not told to the peer yet (docs/15-chat.md §2): sent with the waiting messages once it is online.
CREATE TABLE ChatRecalls (
  PeerId TEXT NOT NULL,
  Id     TEXT NOT NULL,
  PRIMARY KEY (PeerId, Id)
);
