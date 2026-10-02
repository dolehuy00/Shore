-- M5: groups (docs/07-groups.md). The tables exist since 0001; what was missing:
-- member side: whether we are in the group or still waiting for the Host to approve;
-- host side: who asked (or was invited), to show it and to remember declines.
ALTER TABLE Groups ADD COLUMN State TEXT NOT NULL DEFAULT 'member';
ALTER TABLE GroupJoinRequests ADD COLUMN DisplayName TEXT;
ALTER TABLE GroupJoinRequests ADD COLUMN HostName TEXT;
