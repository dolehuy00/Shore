-- Years of messages (docs/22-scale.md): the conversation list counts what is unread and finds the newest reaction to
-- ours in each conversation without reading every message or reaction.
CREATE INDEX IX_ChatMessages_Unread ON ChatMessages (PeerId) WHERE State = 'unread';
CREATE INDEX IX_ChatGroupMessages_Unread ON ChatGroupMessages (GroupId) WHERE State = 'unread';
CREATE INDEX IX_ChatReactions_At ON ChatReactions (PeerId, At);
CREATE INDEX IX_ChatGroupReactions_At ON ChatGroupReactions (GroupId, At);
