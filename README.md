# Tourbooking

Ứng dụng đặt tour ASP.NET Core 10: MVC/API, SQL Server, MongoDB, RabbitMQ, Kong và YARP.

Stack hiện tại `tourbooking-live` dùng SQL Server Express `TourBookingDB` trên Windows và MongoDB trong Docker. `compose.yaml` giữ nguyên SQLite legacy; không chuyển dữ liệu hoặc share một file SQLite cho nhiều replica.

## Chạy

Yêu cầu Docker Desktop Linux containers, .NET SDK 10; SQL Express phải chạy khi `.env` đặt `SQL_DEPLOYMENT=sqlexpress`.

```powershell
.\scripts\Start-Hybrid.ps1 -AdminPorts
```

Mặc định dùng `compose.ha.yaml`: hai replica cho backend, Gateway và Kong. HAProxy là điểm vào **http://localhost:8080**. `-SingleInstance` chỉ dành cho cấu hình một instance, không dùng để chuyển ngược một stack HA đang chạy.

SSMS: `localhost\SQLEXPRESS`, Windows Authentication, `TourBookingDB`. Với `-AdminPorts`, MongoDB ở `localhost:27018`, RabbitMQ management ở `localhost:15672`. Mật khẩu nằm trong `.env`, được gitignore. Không xóa database/volume để cập nhật ứng dụng.

## Kiểm tra

```powershell
dotnet build Tourbooking.sln
dotnet test tests/RabbitMq.Tests/RabbitMq.Tests.csproj
```

Test cần dịch vụ thật mặc định bỏ qua. Chỉ bật trên stack test riêng: chúng có thể tạo tài khoản, booking và tin nhắn. Xem cách chạy SQL/Mongo và ma trận chịu lỗi trong tài liệu kiểm chứng.

## Tài liệu

- [Vận hành, backup và triển khai](docs/operations.md)
- [Kiến trúc và chịu lỗi](docs/architecture.md)
- [Kiểm chứng và giới hạn](docs/verification.md)
- [Stack/công cụ legacy](docs/legacy.md)
- [Hợp đồng API và MVC](contracts/api-spec.md)

Quy tắc phát triển: `AI-rules/AGENTS.md`, `AI-rules/api-guidelines.md`. Báo cáo cũ/trùng đã được gộp; bản gốc ở `backups/docs-before-consolidation-*`. Hình trong `docs/images` giữ nguyên; thư mục này không có file Markdown cần xóa.
