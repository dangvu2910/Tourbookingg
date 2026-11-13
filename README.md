# Tourbooking

Ứng dụng đặt tour ASP.NET Core 10: MVC/API, SQL Server, MongoDB, RabbitMQ, Kong và YARP.

Stack `tourbooking-live` chạy bằng `compose.hybrid.yaml`: mỗi service một container trên một Docker network, SQL Server và MongoDB trong container. Stack SQLite cũ đã bỏ (13/11/2026); toàn bộ dữ liệu đã có trong SQL Server/MongoDB.

Không có load balancer/edge riêng: Kong là điểm vào duy nhất. Dự án chỉ chạy local, không triển khai cloud.

## Chạy

Yêu cầu Docker Desktop Linux containers, .NET SDK 10.

```powershell
.\scripts\Start-Hybrid.ps1 -AdminPorts
```

Kong là điểm vào **http://localhost:8080** (đổi bằng `HTTP_PORT`). Các service gọi nhau trực tiếp bằng tên Docker (`http://catalog:8080`...).

SQL: với `-AdminPorts`, SSMS kết nối `127.0.0.1,14333`, SQL Server Authentication (`sa`/`SQL_PASSWORD` hoặc tài khoản `<service>_app`), tick *Trust server certificate*; database `IdentityDB`, `CatalogDB`, `BookingDB`, `CustomerDB`. MongoDB ở `localhost:27018`, RabbitMQ management ở `localhost:15672`. Mật khẩu nằm trong `.env`, được gitignore. Không xóa database/volume để cập nhật ứng dụng.

## Kiểm tra

```powershell
dotnet build Tourbooking.sln
dotnet test tests/Tourbooking.Tests/Tourbooking.Tests.csproj
```

Test cần dịch vụ thật mặc định bỏ qua. Chỉ bật trên stack test riêng: chúng có thể tạo tài khoản, booking và tin nhắn. Xem cách chạy SQL/Mongo và ma trận chịu lỗi trong tài liệu kiểm chứng.

## Tài liệu

- [Vận hành, backup và triển khai](docs/operations.md)
- [Kiến trúc và chịu lỗi](docs/architecture.md)
- [Kiểm chứng và giới hạn](docs/verification.md)
- [Sao lưu và phục hồi](docs/backup-restore.md)
- [Hợp đồng API và MVC](contracts/api-spec.md)

Quy tắc phát triển: `AI-rules/AGENTS.md`, `AI-rules/api-guidelines.md`. Báo cáo cũ/trùng đã được gộp; bản gốc ở `backups/docs-before-consolidation-*`. Hình trong `docs/images` giữ nguyên; thư mục này không có file Markdown cần xóa.
