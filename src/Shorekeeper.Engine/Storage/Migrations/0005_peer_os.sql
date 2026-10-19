-- The OS a contact last announced ("windows", "android"), so it keeps its phone or computer icon while offline.
ALTER TABLE Peers ADD COLUMN Os TEXT;
