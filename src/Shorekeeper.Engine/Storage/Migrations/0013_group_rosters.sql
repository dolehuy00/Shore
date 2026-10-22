-- The member list as the Host signed it (docs/07-groups.md §4.1), one per group: the Host's own, or the newest a
-- member got from the Host or from another member. Kept as it came (JSON), so it can be passed on and checked again.
CREATE TABLE GroupRosters (
  GroupId TEXT PRIMARY KEY REFERENCES Groups(GroupId) ON DELETE CASCADE,
  Version INTEGER NOT NULL,
  Roster  TEXT NOT NULL
);
