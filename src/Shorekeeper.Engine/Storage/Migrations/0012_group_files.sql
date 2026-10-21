-- Files in group conversations (docs/16-group-chat.md §6.3): "Thu hồi" of a group's file card is told to its members
-- like a recalled message. (InboxOffers.GroupId was there from the start, but only written from now on.)
ALTER TABLE ChatGroupRecalls ADD COLUMN IsOffer INTEGER NOT NULL DEFAULT 0;
