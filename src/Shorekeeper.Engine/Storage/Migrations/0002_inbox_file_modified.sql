-- Remember the sender's modification time so a retried download can still restore it.
ALTER TABLE InboxFiles ADD COLUMN ModifiedAt INTEGER;
