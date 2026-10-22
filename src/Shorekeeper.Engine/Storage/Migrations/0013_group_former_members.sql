-- Who left a group or was removed, and when (the Host's clock), for the last 31 days (docs/16-group-chat.md §3.6).
-- Kept by the Host and copied to members with the member list: a group message passed on by a member is taken only
-- from a current member, or from a former one if sent before they left.
CREATE TABLE GroupFormerMembers (
  GroupId  TEXT NOT NULL REFERENCES Groups(GroupId) ON DELETE CASCADE,
  DeviceId TEXT NOT NULL,
  LeftAt   INTEGER NOT NULL,
  PRIMARY KEY (GroupId, DeviceId)
);
