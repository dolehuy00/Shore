-- How far the Host's clock is ahead of ours (ms, negative when behind), measured on each answer of the Host
-- (docs/07-groups.md §4.1): removal times in its signed member list are in its time.
ALTER TABLE Groups ADD COLUMN HostOffsetMs INTEGER NOT NULL DEFAULT 0;
