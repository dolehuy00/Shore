Gửi file trong mạng LAN giữa máy tính Windows và điện thoại Android, không qua máy chủ. / Send files over the LAN between Windows PCs and Android phones, with no server.

### Windows — `Shorekeeper-<version>-x64.msi`

- Windows 10/11 x64, cần quyền admin để cài. / Needs admin rights to install.
- MSI chưa ký số nên SmartScreen báo "Unknown publisher": chọn **More info → Run anyway**. / The MSI is not code-signed, so SmartScreen warns: choose **More info → Run anyway**.

### Android — `Shorekeeper-<version>.apk`

- Android 10 trở lên. Mở file APK và cho phép cài từ nguồn này. / Android 10 or later. Open the APK and allow installing from this source.
- Chứng chỉ ký (SHA-256) / Signing certificate (SHA-256):
  `96:3A:5C:4A:C4:2A:5E:A0:0C:84:82:27:F1:AC:8C:EB:99:D7:95:03:48:34:D7:03:00:D5:F0:75:11:DE:40:26`
- Máy OPPO/Xiaomi/Vivo: cho phép Shorekeeper chạy nền trong Cài đặt → Pin. / On OPPO/Xiaomi/Vivo, allow background activity in Settings → Battery.

Kiểm tra file tải về bằng `SHA256SUMS.txt`. / Check the downloads against `SHA256SUMS.txt`.
