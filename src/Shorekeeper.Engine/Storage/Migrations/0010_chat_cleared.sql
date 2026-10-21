-- "Xóa hội thoại" (docs/15-chat.md §6.1): file cards up to this moment no longer show in the conversation.
CREATE TABLE ChatCleared (
  PeerId    TEXT PRIMARY KEY,
  ClearedAt INTEGER NOT NULL
);
