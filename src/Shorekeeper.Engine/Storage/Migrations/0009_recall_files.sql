-- Files taken back from a conversation ("Thu hồi", docs/15-chat.md §6.3): the card becomes a note on both sides.
ALTER TABLE Offers ADD COLUMN Recalled INTEGER NOT NULL DEFAULT 0;
ALTER TABLE InboxOffers ADD COLUMN Recalled INTEGER NOT NULL DEFAULT 0;
-- Pictures that downloaded by themselves to show in Tin nhắn: a recall deletes them; files the person downloaded stay.
ALTER TABLE InboxOffers ADD COLUMN ChatPicture INTEGER NOT NULL DEFAULT 0;
-- Recalls still to tell the peer about: messages (0) or offers (1).
ALTER TABLE ChatRecalls ADD COLUMN IsOffer INTEGER NOT NULL DEFAULT 0;
