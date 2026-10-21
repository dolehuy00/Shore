-- When the sender made the offer (its clock, never later than arrival), so a card delivered late keeps its place
-- among the messages. Older rows have none: they fall back to when they arrived.
ALTER TABLE InboxOffers ADD COLUMN SentAt INTEGER;
