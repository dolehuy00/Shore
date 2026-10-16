-- "Tự nhận file" used to have one fixed limit (2 GB); contacts switched on before now follow the limit set in Settings (-1).
UPDATE Peers SET AutoAcceptMaxBytes = -1 WHERE AutoAcceptMaxBytes = 2147483648;
