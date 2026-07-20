# ZenConnect

ZenConnect là hệ thống chụp màn hình và phản hồi theo phòng, gồm client Windows,
module dùng chung và server web hoạt động độc lập.

## Cấu trúc

- `ZenConnectClient`: ứng dụng Windows một màn hình để kết nối, hiển thị key và chụp ảnh.
- `ZenConnectModule`: module độc lập quản lý key máy, upload ảnh và nhận tin nhắn.
- `ScreenReviewServer`: ASP.NET Core API và giao diện web quản lý phòng.
- `render.yaml`: cấu hình triển khai server lên Render.

## Chạy local

Yêu cầu .NET 8 SDK.

```powershell
dotnet run --project .\ScreenReviewServer\ScreenReviewServer.csproj
dotnet run --project .\ZenConnectClient\ZenConnectClient.csproj
```

Server mặc định chạy tại `http://localhost:5080`. Trong client:

1. Nhấn `KẾT NỐI` để đăng ký phòng của máy.
2. Key được tạo trong lần chạy đầu và lưu tại
   `%LocalAppData%\ZenConnect\device-key.txt`.
3. Mở `http://localhost:5080` trên trình duyệt và nhập key đó.
4. Nhấn `CHỤP MÀN HÌNH` hoặc phím `F8` để gửi ảnh lên phòng.

URL server gần nhất được lưu tại
`%LocalAppData%\ZenConnect\server-url.txt`. Có thể đặt biến môi trường
`ZENCONNECT_SERVER_URL` để cấu hình URL mặc định khi phát hành client.

## Deploy Render

1. Push repository lên GitHub hoặc GitLab.
2. Trong Render, chọn `New` → `Blueprint` và kết nối repository.
3. Render đọc `render.yaml`, build `ScreenReviewServer/Dockerfile` và gắn persistent disk tại `/var/data`.
4. Sau khi deploy, nhập URL HTTPS của Render vào client và nhấn `KẾT NỐI`.

Không cần cấu hình key phòng trên Render. Mỗi client tự tạo một key ngẫu nhiên
dạng `XXXX-XXXX-XXXX-XXXX-XXXX` và đăng ký phòng tương ứng.

## Build client

```powershell
dotnet publish .\ZenConnectClient\ZenConnectClient.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true
```

Output nằm trong `ZenConnectClient\bin\Release\net8.0-windows\win-x64\publish`.
