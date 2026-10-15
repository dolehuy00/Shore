# Spike A0 — Android

Code thử ở giai đoạn A0 của [docs/14-android.md](../../docs/14-android.md) (kết quả ghi ở §4). Không phải code sản phẩm, không nằm trong `Shorekeeper.sln`.

| Project | Làm gì |
|---|---|
| `A0Phone` | App Android không giao diện: mTLS server/client, multicast + probe, SQLite + Dapper. Kết quả ra màn hình và logcat (tag `A0`) |
| `A0Ui` | Avalonia.Android: danh sách, nhận file từ menu Chia sẻ, foreground service giữ server TLS khi tắt màn hình |
| `A0Pc` | Console trên PC: gọi server TLS của điện thoại như `PeerClient`, đo tốc độ (TCP thường và TLS, có/không giữ khóa) |
| `Shared` | Cert thiết bị (giống `CertificateFactory`), server thử `BlobServer` |

Cần: workload `android` của .NET, Android SDK API 36, điện thoại bật USB debugging, cùng Wi-Fi với PC.

```bash
dotnet build spikes/android-a0/A0Ui -c Release
```

```bash
adb install -r spikes/android-a0/A0Ui/bin/Release/net10.0-android/dev.shorekeeper.a0ui-Signed.apk
```

```bash
dotnet run --project spikes/android-a0/A0Pc -c Release -- <IP điện thoại>:47510
```

- `A0Phone` dùng cổng 47500, `A0Ui` cổng 47510 (TLS) và cổng kế tiếp (TCP thường). `A0Phone` gọi Shorekeeper trên PC qua `adb reverse tcp:47471 tcp:47471`; địa chỉ PC cho probe đang ghi cứng trong `MainActivity.cs`.
- Điều khiển `A0Ui` bằng adb: `adb shell am start -n dev.shorekeeper.a0ui/dev.shorekeeper.a0ui.MainActivity --es action server|fgs-start|fgs-stop`.
- Trong Git Bash, đặt `MSYS_NO_PATHCONV=1` trước các lệnh adb có đường dẫn trên điện thoại (`/sdcard/...`).
