# Tour Booking — Ứng dụng đặt tour phân tán

Ứng dụng cho phép khách hàng tìm tour, đặt tour, chuyển khoản và nhận thông báo. Quản trị viên quản lý tour, người dùng và xác nhận thanh toán.

Hệ thống được chia thành **5 dịch vụ**, mỗi dịch vụ chạy trong một container riêng. Docker Compose chạy cả ứng dụng, SQL Server và RabbitMQ: tổng cộng **7 container chính**.

## 1. Sơ đồ kiến trúc

Có thể hình dung hệ thống đơn giản như sau. Sơ đồ được viết bằng chữ ngay trong README:

                                 Người dùng
                                     |
                                     v
                             Trình duyệt Web
                                     |
                              HTTP :8080
                                     |
                                     v
                              API Gateway
                            (gateway:8080)
                                     |
          +------------------+-------+----------+---------------+
          |                  |                  |               |
          v                  v                  v               v
      Identity            Catalog           Booking        Notification
    Người dùng           Tour du lịch     Đặt / Thanh toán   Thông báo
       :8080               :8080             :8080             :8080
          |                  |                  |               |
          +------------------+-------+----------+---------------+
                                     |
                               SQL / TCP :1433
                                     |
                                     v
                                 SQL Server
                              (sqlserver:1433)
                                     |
                      +--------------+----------------+
                      |                               |
                      v                               v
                TourBookingDb             TourBookingNotifications
             Identity, Catalog, Booking          Notification


Đường nối từ các dịch vụ xuống SQL Server thể hiện kết nối tới **cùng một máy chủ SQL**. Bảng bên dưới giải thích dịch vụ nào dùng database nào.

Booking còn gửi sự kiện qua RabbitMQ để Notification xử lý thông báo:

```text
      Booking              RabbitMQ              Notification
         |                     |                        |
         +-- Gửi sự kiện ------>+--- Giao sự kiện ------>+
              AMQP :5672             AMQP :5672          |
                                                        v
                                             Lưu thông báo vào SQL
```

**Cách đọc:** đường từ trên xuống là request truy cập website; luồng qua RabbitMQ là việc gửi và nhận sự kiện sau khi đặt tour. Gateway cũng chuyển request đọc thông báo đến Notification qua HTTP.

### Mỗi dịch vụ làm gì?

| Dịch vụ | Hiểu đơn giản | Ví dụ chức năng |
| --- | --- | --- |
| Gateway | Cửa vào của ứng dụng, chuyển request đến đúng dịch vụ | Chuyển trang đăng nhập đến Identity |
| Identity | Quản lý tài khoản | Đăng ký, đăng nhập, hồ sơ, vai trò |
| Catalog | Quản lý tour du lịch | Danh sách, tìm kiếm, chi tiết, thêm/sửa tour |
| Booking | Quản lý đặt tour và thanh toán | Đặt, hủy, đổi lịch, xác nhận thanh toán, đánh giá, tư vấn |
| Notification | Xử lý thông báo | Nhận sự kiện RabbitMQ, lưu và cung cấp thông báo |

**Phạm vi hiện tại:** thanh toán nằm trong Booking. Identity, Catalog và Booking dùng chung database và thư viện MVC/EF; Notification có database riêng. Đây là hệ thống nhiều dịch vụ chạy thành các tiến trình riêng, còn dùng chung một phần mã và dữ liệu. Môi trường demo chạy trên một máy.

## 2. Cổng nào dùng để làm gì?

| Thành phần | Địa chỉ / cổng | Cách sử dụng |
| --- | --- | --- |
| Website / Gateway | `http://localhost:8080` | Mở bằng trình duyệt |
| Identity | `identity:8080` | Gateway gọi trong mạng Docker |
| Catalog | `catalog:8080` | Gateway gọi trong mạng Docker |
| Booking | `booking:8080` | Gateway gọi trong mạng Docker |
| Notification | `notification:8080` | Gateway và Booking gọi trong mạng Docker |
| RabbitMQ | `rabbitmq:5672` | Ứng dụng gửi/nhận message bằng AMQP |
| Quản trị RabbitMQ | `http://localhost:15672` | Mở bằng trình duyệt |
| SQL Server | `sqlserver:1433` | Các dịch vụ kết nối database trong Docker |

`localhost` là máy bạn đang sử dụng; `identity`, `booking`, `sqlserver`… là tên các dịch vụ trong mạng Docker.

Các dịch vụ đều dùng cổng `8080` nhưng nằm trong container riêng nên không trùng cổng. Chỉ Gateway mở cổng website ra máy. RabbitMQ mở thêm `5672` cho AMQP client và `15672` cho trang quản trị; SQL không mở cổng ra máy. Các cổng được mở hiện chỉ truy cập qua `127.0.0.1`.

Cấu hình demo đang dùng **HTTP**, chưa có HTTPS hay bộ cân bằng tải riêng.

### Ví dụ đường đi của request

```text
localhost:8080/Account/Login      -> Gateway -> Identity:8080
localhost:8080/Tours              -> Gateway -> Catalog:8080
localhost:8080/Home/Booking       -> Gateway -> Booking:8080
localhost:8080/api/notifications  -> Gateway -> Notification:8080
```

Riêng trang `/Notifications` do Booking dựng giao diện và gọi API của Notification qua `http://notification:8080`. Các tên dịch vụ trong ví dụ chỉ đích đến nội bộ, không phải địa chỉ để mở trực tiếp bằng trình duyệt trên máy.

## 3. Chạy dự án bằng Docker

### Chuẩn bị

- Mở Docker Desktop hoặc Docker Engine, dùng Linux containers.
- Máy có đủ RAM cho SQL Server và các dịch vụ; nên dành khoảng 4 GB trở lên cho Docker.
- Mở PowerShell tại thư mục dự án, nơi có file `compose.yaml`.

Chạy bằng Docker không cần cài riêng SQL Server hoặc .NET SDK trên máy.

### Khởi động lần đầu

```powershell
# Tạo file cấu hình và mật khẩu
./scripts/Initialize-Environment.ps1

# Chỉ chạy nếu máy chưa có Docker Compose
./scripts/Setup-Compose.ps1

# Build và chạy toàn bộ hệ thống
./scripts/Compose.ps1 up -d --build

# Xem trạng thái các container
./scripts/Compose.ps1 ps
```

Chờ các dịch vụ chuyển sang `healthy`, rồi mở **http://localhost:8080**. Lần đầu cần tải image và build nên có thể mất vài phút.

Script khởi tạo giữ nguyên `.env` nếu file đã tồn tại. Khi database Docker trống, hệ thống tự tạo bảng và thêm ba tour mẫu. Identity thực hiện migration, tức là cập nhật cấu trúc bảng theo mã nguồn.

Trên Linux/macOS, tạo `.env` từ `.env.example`, điền mật khẩu rồi chạy `docker compose up -d --build`.

### Tài khoản

| Tài khoản | Lấy ở đâu? |
| --- | --- |
| Khách hàng | Tự đăng ký trên website |
| Admin website | `ADMIN_EMAIL` và `ADMIN_PASSWORD` trong `.env` |
| RabbitMQ | Username `tourbooking`, mật khẩu `RABBITMQ_PASSWORD` trong `.env` |

Không commit hoặc chia sẻ `.env` vì file chứa mật khẩu.

## 4. Thử một lần đặt tour

1. Đăng ký và đăng nhập tài khoản khách hàng.
2. Chọn tour, số khách và ngày khởi hành trong tương lai.
3. Đặt tour và xem hướng dẫn chuyển khoản.
4. Sau khi chuyển khoản, bấm gửi yêu cầu xác nhận thanh toán.
5. Đăng nhập Admin bằng cửa sổ riêng để kiểm tra và xác nhận booking.
6. Quay lại tài khoản khách để xem booking và mục **Thông báo**.

Trạng thái sau khi khách báo đã chuyển khoản là `Processing`. Khi Admin xác nhận, booking thành `Confirmed`, thanh toán thành `Paid`. Hệ thống hiện xác nhận chuyển khoản thủ công, chưa kết nối cổng thu tiền tự động.

## 5. RabbitMQ hoạt động thế nào?

RabbitMQ là nơi trung gian giữ và chuyển message. Booking gửi sự kiện, còn Notification nhận sự kiện để tạo thông báo. Khách hàng không cần thao tác trực tiếp với RabbitMQ khi đặt tour.

Ví dụ một yêu cầu đặt tour:

```text
Khách đặt tour
      |
      v
Booking lưu đơn và sự kiện vào SQL
      |
      v
Worker gửi sự kiện đến RabbitMQ
      |
      v
Notification nhận sự kiện và lưu thông báo
      |
      v
Khách xem thông báo trên website
```

Một số tên thường gặp:

| Từ | Ý nghĩa trong dự án |
| --- | --- |
| Message / sự kiện | Dữ liệu cho biết đã có thay đổi, ví dụ một booking vừa được tạo |
| Producer | Bên gửi message: worker của Booking |
| Consumer | Bên nhận và xử lý message: Notification |
| Queue | Hàng đợi giữ message chờ xử lý |
| ACK | Consumer báo đã xử lý xong message |
| Outbox | Bảng SQL giữ sự kiện để worker có thể gửi sau |

Nếu RabbitMQ dừng, Booking vẫn lưu đơn và sự kiện vào SQL. Khi broker hoạt động lại, worker tiếp tục gửi sự kiện. Notification dùng EventId để tránh lưu thông báo trùng khi nhận lại cùng một sự kiện.

### Xem message trên RabbitMQ

1. Mở **http://localhost:15672** và đăng nhập.
2. Vào **Queues and Streams**.
3. Chọn queue `tourbooking.notifications`.

| Chỉ số | Ý nghĩa |
| --- | --- |
| Ready | Message đang chờ được giao |
| Unacked | Message đã giao nhưng chưa được xác nhận xử lý |
| Consumers | Số consumer đang nhận message |

Queue bằng `0` có thể là do message đã được xử lý ngay. Để thấy message chờ, thử dừng Notification:

```powershell
./scripts/Compose.ps1 stop notification
```

Tạo một booking trên website rồi xem lại queue: `Ready` sẽ tăng. Sau đó bật lại:

```powershell
./scripts/Compose.ps1 start notification
./scripts/Compose.ps1 logs -f booking notification
```

Message được xử lý, số lượng trong queue giảm và thông báo xuất hiện trên tài khoản khách hàng. Nhấn `Ctrl+C` để thoát xem log; các dịch vụ vẫn chạy.

### Các queue trong dự án

| Tên | Vai trò |
| --- | --- |
| `tourbooking.events` | Exchange nhận và phân phối sự kiện, không phải queue |
| `tourbooking.notifications` | Queue chính cho Notification |
| `tourbooking.notifications.retry` | Chờ 5 giây trước khi giao lại message lỗi |
| `tourbooking.notifications.dead` | Giữ message không hợp lệ hoặc lỗi sau tối đa 5 lần thử lại |

## 6. SQL và dữ liệu được lưu ở đâu?

Một container SQL Server chứa hai database:

| Database | Dịch vụ sử dụng | Dữ liệu chính |
| --- | --- | --- |
| `TourBookingDb` | Identity, Catalog, Booking | Tài khoản, tour, booking, payment, review, tư vấn, outbox |
| `TourBookingNotifications` | Notification | Thông báo và EventId |

Docker dùng **volume** để giữ dữ liệu khi container bị dừng hoặc tạo lại:

| Volume | Nội dung |
| --- | --- |
| `sql-data` | Dữ liệu SQL Server |
| `sql-backups` | File backup SQL trong container |
| `rabbit-data` | Dữ liệu RabbitMQ và message được lưu bền vững |
| `auth-keys` | Khóa dùng chung để các dịch vụ đọc cookie đăng nhập |
| `tour-images` | Hình ảnh tour |

Database Docker tách biệt với LocalDB cũ trên máy. Chạy Docker không tự chuyển dữ liệu LocalDB sang; muốn dùng dữ liệu cũ cần chuyển dữ liệu riêng. Đẩy image lên Docker Hub cũng không mang dữ liệu trong volume theo.

**Dùng `down` để dừng hệ thống và giữ dữ liệu. `down -v` sẽ xóa các named volume của hệ thống.**

## 7. Các lệnh cần nhớ

```powershell
# Xem container có chạy không
./scripts/Compose.ps1 ps

# Xem log
./scripts/Compose.ps1 logs -f gateway booking notification

# Chạy lại hệ thống
./scripts/Compose.ps1 up -d

# Cập nhật sau khi sửa mã nguồn
./scripts/Compose.ps1 up -d --build

# Dừng hệ thống và giữ dữ liệu
./scripts/Compose.ps1 down
```

Kiểm tra sức khỏe tại **http://localhost:8080/health/ready**. Mã `200` nghĩa là các dịch vụ phía sau đã sẵn sàng; `503` nghĩa là có dịch vụ chưa sẵn sàng. Các tuyến còn khỏe vẫn có thể phục vụ. `/health/live` kiểm tra Gateway còn hoạt động.

### Chạy kiểm thử

Sau khi hệ thống đã chạy và healthy:

```powershell
./scripts/Compose.ps1 --profile test run --build --no-deps --rm tests
```

Bộ test kiểm tra định tuyến, đăng nhập giữa các dịch vụ, quyền truy cập, đặt tour/thanh toán, chống booking trùng, rollback outbox và xử lý sự kiện trùng. Test tạo tài khoản và booking thử trong database Docker.

Kiểm tra đặt tour khi RabbitMQ mất kết nối:

```powershell
./scripts/Test-Resilience.ps1
```

Script này tạm dừng RabbitMQ, tạo booking thử rồi bật lại broker để kiểm tra sự kiện được giao sau khi phục hồi.

### Sao lưu và khôi phục

```powershell
# Tạo bản sao lưu
./scripts/Backup.ps1

# Khôi phục từ thư mục đã chọn; ghi đè dữ liệu hiện tại
./scripts/Restore.ps1 -BackupDirectory ./backups/THU_MUC_BACKUP -Apply
```

Backup gồm hai SQL database, cấu hình RabbitMQ, khóa đăng nhập và ảnh tour, kèm checksum để kiểm tra file. Cấu hình RabbitMQ được xuất không chứa message đang chờ trong queue. Hướng dẫn phát lại sự kiện khi mất dữ liệu broker nằm trong [báo cáo thiết kế](docs/bao-cao-he-thong-phan-tan.md). Giữ `.env` riêng và bảo vệ thư mục backup.

### Khi gặp lỗi

| Vấn đề | Kiểm tra trước |
| --- | --- |
| Website không mở | Docker có chạy không? Container Gateway đã healthy chưa? |
| Container còn starting | Chờ SQL khởi tạo; nếu lâu, xem log `sqlserver` và `identity` |
| Báo thiếu mật khẩu | Kiểm tra `.env` và chạy script khởi tạo |
| Không có thông báo | Kiểm tra RabbitMQ, Notification và queue chính |
| Không thấy dữ liệu cũ | Kiểm tra bạn đang dùng database Docker hay LocalDB |

## 8. Học mã nguồn từ đâu?

Nên đọc theo thứ tự sau:

| Thứ tự | File / thư mục | Tìm hiểu điều gì? |
| --- | --- | --- |
| 1 | `compose.yaml` | Có những container nào, kết nối và lưu dữ liệu ra sao |
| 2 | `services/Gateway/Program.cs` | URL được chuyển đến dịch vụ nào |
| 3 | `Distributed/ServiceBoundary.cs` | Endpoint thuộc dịch vụ nào |
| 4 | `Controllers/HomeController.cs` | Luồng đặt tour và gửi yêu cầu xác nhận thanh toán |
| 5 | `Data/ApplicationDbContext.cs` | Lưu dữ liệu nghiệp vụ cùng sự kiện outbox |
| 6 | `Distributed/OutboxDispatcher.cs` | Worker lấy sự kiện và gửi RabbitMQ |
| 7 | `services/Notification/` | Consumer nhận sự kiện, lưu và đọc thông báo |
| 8 | `tests/RabbitMq.Tests/` | Cách kiểm chứng hoạt động của hệ thống |

Các project dịch vụ nằm trong `services/`; giao diện trong `Views/`; script vận hành trong `scripts/`. Công nghệ chính là ASP.NET Core MVC 10, Entity Framework Core, SQL Server, YARP, RabbitMQ và Docker Compose.

Nếu đã cài .NET SDK 10, có thể kiểm tra build bằng `dotnet build Tourbooking.sln`. Project gốc vẫn chạy MVC với LocalDB bằng `dotnet run --project Tourbooking.csproj`, nhưng muốn học và demo kiến trúc phân tán thì dùng Docker Compose.

## 9. API và tài liệu tham khảo

Các API được truy cập qua `http://localhost:8080`. API riêng tư sử dụng cookie sau khi đăng nhập website; thao tác ghi thực hiện bằng form MVC có chống giả mạo request.

| API đọc | Quyền |
| --- | --- |
| `/api/tours`, `/api/tours/{id}` | Công khai |
| `/api/identity/me` | Đăng nhập |
| `/api/bookings`, `/api/bookings/{id}` | Booking của người đang đăng nhập |
| `/api/notifications` | Thông báo của người đang đăng nhập |
| `/api/bookings/outbox`, `/api/notifications/admin` | Admin |

Bản hiện tại có cơ chế retry, health checks và restart tiến trình, nhưng chưa triển khai cụm SQL/RabbitMQ nhiều node hay tự động failover giữa các máy.

- [Báo cáo hệ thống phân tán theo 8 chương](docs/bao-cao-he-thong-phan-tan.md)
- [Kịch bản thuyết trình và demo](docs/kich-ban-demo.md)
- [Kết quả kiểm chứng thực tế](docs/ket-qua-kiem-thu.md)
