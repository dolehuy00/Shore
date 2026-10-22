-- "Chỉ nhắn khi Host online" (docs/16-group-chat.md §3.7): set by the Host, copied to members with its signed list.
ALTER TABLE Groups ADD COLUMN HostOnlyChat INTEGER NOT NULL DEFAULT 0;

-- When this device last heard the Host's member list (ms, its own clock; 0 never), written about once a minute
-- (docs/07-groups.md §4.1).
ALTER TABLE Groups ADD COLUMN SyncedAt INTEGER NOT NULL DEFAULT 0;

-- Someone gone from our member list with no time of leaving in the Host's list (one older than it keeps, or a Host
-- without signed lists): what came from them after we last heard they were a member is deleted.
CREATE TABLE GroupDepartures (
  GroupId     TEXT NOT NULL REFERENCES Groups(GroupId) ON DELETE CASCADE,
  DeviceId    TEXT NOT NULL,
  ConfirmedAt INTEGER NOT NULL,
  PRIMARY KEY (GroupId, DeviceId)
);
