# ZenConnect

ZenConnect là hệ thống chụp màn hình và phản hồi theo phòng tất cả trong một (All-in-One), hợp nhất giao diện client Windows (WinForms) và server web review (ASP.NET Core Kestrel) vào **một project .NET 8 duy nhất**.

## Cấu trúc thư mục

```
seb/
├── src/                                  # Toàn bộ mã nguồn dự án .NET
│   └── ZenConnect/                       # Project duy nhất tích hợp Server & Client
│       ├── Client/                       # Giao diện WinForms, chụp màn hình, hook chuột/phím tắt
│       │   ├── MainForm.cs               # Cửa sổ chính ZenConnect
│       │   └── ZenConnectService.cs      # Xử lý chụp màn hình, phím tắt X, overlay chuột phải
│       ├── Server/                       # ASP.NET Core Kestrel Minimal APIs & Data Store
│       │   ├── WebServer.cs              # Host Kestrel nhúng, rate limiting, endpoints /api/*
│       │   └── ReviewStore.cs            # Lưu trữ dữ liệu metadata & ảnh chụp
│       ├── wwwroot/                      # Giao diện web review phòng (HTML/CSS/JS)
│       ├── Dockerfile                    # Docker build phục vụ deploy Render / Linux
│       ├── Program.cs                    # Entry point hợp nhất (Desktop mode / Server mode)
│       └── ZenConnect.csproj             # File project .NET 8 SDK Web duy nhất
├── scripts/                              # Scripts tự động hóa và kiểm tra
│   ├── run.ps1                           # Script khởi chạy và build/publish tự động
│   ├── run-hidden.vbs                    # Bootstrap chạy ẩn, không giữ cửa sổ terminal
│   └── verify.ps1                        # Script kiểm tra build, JavaScript và cú pháp
├── run.bat                               # Shortcut 1-click khởi chạy tại thư mục gốc
├── render.yaml                           # Cấu hình triển khai server lên Render Blueprint
├── ZenConnect.sln                        # Solution Visual Studio quản lý 1 project duy nhất
└── README.md                             # Tài liệu hướng dẫn sử dụng
```

## Chạy ứng dụng

Yêu cầu .NET 8 SDK.

### 1. Khởi chạy nhanh (1-Click)
Nhấp đúp file **`run.bat`** ở thư mục gốc. Ứng dụng sẽ:
- Tự động publish ra file chạy duy nhất `ZenConnect.exe` (Single-file).
- Khởi động Kestrel web server ngầm tại `http://localhost:5080`.
- Mở giao diện WinForms client.
- Tự động dừng server ngầm khi người dùng đóng cửa sổ tool.

### 2. Chạy từ dòng lệnh
```powershell
# Chạy trực tiếp Desktop App (kèm server local ngầm)
dotnet run --project .\src\ZenConnect\ZenConnect.csproj

# Hoặc chạy riêng chế độ Server headless (cho server/Docker)
dotnet run --project .\src\ZenConnect\ZenConnect.csproj -- --server
```

Dữ liệu local được lưu trữ ổn định tại `%LocalAppData%\ZenConnect\server-data`. Log khởi động nằm tại `%LocalAppData%\ZenConnect\logs\launcher.log`.

Server production mặc định là `https://zenconnect-noli.onrender.com`. Có thể chỉnh URL trực tiếp trong client hoặc đặt biến `ZENCONNECT_SERVER_URL`.

## Sử dụng

1. Mở tool. Tool sẽ tự kết nối và đăng ký phòng của máy; nếu kết nối tự động không thành công, kiểm tra Server URL rồi nhấn `KẾT NỐI` để thử lại.
2. Key được tạo trong lần chạy đầu và lưu tại `%LocalAppData%\ZenConnect\device-key.txt`.
3. Nhấn `MỞ PHÒNG WEB` hoặc mở URL server trên trình duyệt (`http://localhost:5080`) và nhập key đó.
4. Sau khi kết nối, nhấn `X` ở bất kỳ đâu để chụp nhanh; cũng có thể dùng `CHỤP TOÀN MÀN HÌNH` hoặc `CHỤP VÙNG` trên tool.
5. Nhấn chuột phải để bật hoặc tắt thanh phản hồi một dòng cạnh con trỏ. Thao tác chuột phải thông thường của Windows vẫn hoạt động.
6. Khi thanh phản hồi đang hiện, lăn chuột lên/xuống để xem lần lượt các tin nhắn; thao tác cuộn vẫn được chuyển tiếp đến ứng dụng đang dùng. Có thể dùng `Ctrl + Shift + Up/Down` thay thế.
7. Trong phòng web, dùng `Xóa` trên từng phản hồi để xóa riêng tin nhắn; dùng `Xóa ảnh` trên thanh công cụ để xóa ảnh đang xem cùng toàn bộ phản hồi liên quan. Cả hai thao tác đều yêu cầu xác nhận và không thể hoàn tác.

URL server gần nhất được lưu tại `%LocalAppData%\ZenConnect\server-url.txt`.

> [!NOTE]
> Room key là thông tin xác thực. Không commit `App_Data`, `store.json`, ảnh chụp hoặc các file trong `%LocalAppData%\ZenConnect` lên repository.

## Triển khai lên Render

### Dịch vụ mới qua Blueprint

1. Push repository lên GitHub hoặc GitLab.
2. Trong Render, chọn `New` → `Blueprint` và kết nối repository.
3. Render đọc `render.yaml`, build `src/ZenConnect/Dockerfile` và gắn persistent disk tại `/var/data`.
4. Sau khi deploy, nhập URL HTTPS của Render vào client và nhấn `KẾT NỐI`.

### Sửa dịch vụ đang dùng đường dẫn cũ `ScreenReviewServer`

Nếu log báo `invalid local: resolve : lstat /opt/render/project/src/ScreenReviewServer: no such file or directory`, Render đang dùng đường dẫn đã bị xóa khi hợp nhất project vào `src/ZenConnect`. Lỗi xảy ra trước bước build .NET.

Mở **dịch vụ hiện tại → Settings → Build & Deploy**, cập nhật đồng bộ:

| Trường | Giá trị |
| --- | --- |
| Branch | `main` |
| Root Directory | Để trống (gốc repository) |
| Dockerfile Path | `./src/ZenConnect/Dockerfile` |
| Docker Build Context Directory | `./src/ZenConnect` |
| Docker Command | Để trống (dùng entrypoint `dotnet ZenConnect.dll --server`) |
| Health Check Path | `/api/health` |

Lưu các thay đổi rồi chọn **Manual Deploy → Deploy latest commit**. Không nhập `/opt/render/project/src` vào các trường này: đó là đường dẫn nội bộ của Render, không phải đường dẫn trong repository. Không đặt Root Directory thành `src/ZenConnect` khi dùng các giá trị trong bảng, vì Render tính đường dẫn Dockerfile và build context tương đối với Root Directory.

File `render.yaml` hiện đã dùng đường dẫn mới, nhưng việc có file này trong repository không tự cập nhật dịch vụ được tạo thủ công. Nếu dịch vụ được quản lý qua Blueprint, đồng bộ Blueprint đang quản lý chính dịch vụ đó. Không tạo Blueprint mới để sửa dịch vụ hiện tại; tên mẫu `screen-review-server` trong YAML có thể khác tên dịch vụ đang dùng.

Tài liệu tham chiếu: [Root Directory và các đường dẫn tương đối trên Render](https://render.com/docs/monorepo-support), [quản lý dịch vụ bằng Blueprint](https://render.com/docs/infrastructure-as-code).

Sau khi triển khai thành công, kiểm tra `/api/health` trả về `{"status":"ok"}` và trang `/` tải được giao diện. Kiểm tra kết nối, tải ảnh và phản hồi bằng client là bước riêng để xác nhận hoạt động thực tế.

Mỗi client tự tạo một key ngẫu nhiên dạng `XXXX-XXXX-XXXX-XXXX-XXXX` và đăng ký phòng tương ứng. Server giữ tối đa 200 ảnh gần nhất cho mỗi phòng theo mặc định (`SCREEN_REVIEW_MAX_SCREENSHOTS_PER_ROOM`).

## Build & Publish

```powershell
# Publish Desktop App cho Windows (Single-file executable)
dotnet publish .\src\ZenConnect\ZenConnect.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true
```

Output file chạy duy nhất nằm tại: `src\ZenConnect\bin\Release\net8.0-windows\win-x64\publish\ZenConnect.exe`.

## Kiểm tra hệ thống

Chạy script kiểm tra toàn diện:

```powershell
.\scripts\verify.ps1
```

Script sẽ tự động nhận diện .NET SDK, build toàn bộ solution, restore/publish riêng server `net8.0` theo Dockerfile vào `artifacts/render-publish`, kiểm tra cú pháp JavaScript và xác thực cú pháp các script khởi chạy. Kiểm tra này không thay thế việc build container Linux hoặc deploy thực tế trên Render.
