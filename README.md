# Tour Booking — Ứng dụng đặt tour phân tán

Ứng dụng cho phép khách hàng tìm tour, đặt tour, chuyển khoản và nhận thông báo. Quản trị viên quản lý tour, người dùng và xác nhận thanh toán.

Hệ thống được chia thành **9 dịch vụ ứng dụng**, mỗi dịch vụ chạy trong một container riêng. Docker Compose chạy cả ứng dụng, SQL Server và RabbitMQ: tổng cộng **11 container chính**.

## 1. Sơ đồ kiến trúc

```text
Trình duyệt -> Gateway :8080
                 |-> Identity    |-> Catalog   |-> Booking
                 |-> Payment     |-> Review    |-> Support
                 |-> Reporting   |-> Notification

Identity, Catalog, Booking, Payment, Review, Support, Reporting -> TourBookingDb
Notification -> TourBookingNotifications
Các service ghi sự kiện -> SQL Outbox -> RabbitMQ -> Notification
```

SQL Server chứa hai database. Mỗi service có health check riêng; Gateway kiểm tra cả tám backend. SQL Server và RabbitMQ là hạ tầng, không tính vào 9 service ứng dụng.

### Mỗi dịch vụ làm gì?

| Dịch vụ | Hiểu đơn giản | Ví dụ chức năng |
| --- | --- | --- |
| Gateway | Cửa vào của ứng dụng, chuyển request đến đúng dịch vụ | Chuyển trang đăng nhập đến Identity |
| Identity | Quản lý tài khoản | Đăng ký, đăng nhập, hồ sơ, vai trò |
| Catalog | Quản lý tour du lịch | Danh sách, tìm kiếm, chi tiết, thêm/sửa tour |
| Booking | Quản lý đặt tour | Đặt, hủy, đổi lịch, quản trị booking |
| Payment | Thanh toán | Trang chuyển khoản, ghi nhận yêu cầu xác nhận thanh toán |
| Review | Đánh giá | Gửi đánh giá, bình chọn |
| Support | Tư vấn | Liên hệ, hội thoại và phản hồi của quản trị viên |
| Reporting | Báo cáo | Dashboard, xuất Excel tour, booking và người dùng |
| Notification | Xử lý thông báo | Nhận sự kiện RabbitMQ, lưu và cung cấp thông báo |

**Phạm vi hiện tại:** 9 service chạy trong các tiến trình/container riêng; Payment, Review, Support và Reporting sở hữu các endpoint được tách từ hệ thống cũ. Controller và Razor được biên dịch riêng trong từng service. Các service nghiệp vụ vẫn dùng chung thư viện persistence EF và database `TourBookingDb`; Notification dùng database riêng. Booking vẫn tạo bản ghi thanh toán ban đầu và cập nhật trạng thái liên quan trong cùng giao dịch; Payment xử lý trang thanh toán và yêu cầu xác nhận. Đây chưa phải kiến trúc database riêng cho từng service.

## 2. Cổng nào dùng để làm gì?

| Thành phần | Địa chỉ / cổng | Cách sử dụng |
| --- | --- | --- |
| Website / Gateway | `http://localhost:8080` | Mở bằng trình duyệt |
| Identity | `identity:8080` | Gateway gọi trong mạng Docker |
| Catalog | `catalog:8080` | Gateway gọi trong mạng Docker |
| Booking | `booking:8080` | Gateway gọi trong mạng Docker |
| Notification | `notification:8080` | Gateway và Booking gọi trong mạng Docker |
| Payment | `payment:8080` | Gateway gọi trong mạng Docker |
| Review | `review:8080` | Gateway gọi trong mạng Docker |
| Support | `support:8080` | Gateway gọi trong mạng Docker |
| Reporting | `reporting:8080` | Gateway gọi trong mạng Docker |
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
localhost:8080/Home/Payment       -> Gateway -> Payment:8080
localhost:8080/Account/Reviews    -> Gateway -> Review:8080
localhost:8080/Home/Contact       -> Gateway -> Support:8080
localhost:8080/Admin/Dashboard    -> Gateway -> Reporting:8080
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
| `TourBookingDb` | Identity, Catalog, Booking, Payment, Review, Support, Reporting | Tài khoản, tour, booking, payment, review, tư vấn, outbox |
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
| 3 | `Shared/Routing/ServiceBoundary.cs` | Endpoint thuộc dịch vụ nào |
| 4 | `services/Booking/Controllers/HomeController.cs` và `services/Payment/Controllers/HomeController.cs` | Luồng đặt tour và gửi yêu cầu xác nhận thanh toán |
| 5 | `Shared/Persistence/Data/ApplicationDbContext.cs` | Lưu dữ liệu nghiệp vụ cùng sự kiện outbox |
| 6 | `Shared/Persistence/OutboxDispatcher.cs` | Worker lấy sự kiện và gửi RabbitMQ |
| 7 | `services/Notification/` | Consumer nhận sự kiện, lưu và đọc thông báo |
| 8 | `tests/RabbitMq.Tests/` | Cách kiểm chứng hoạt động của hệ thống |

Các project dịch vụ nằm trong `services/`; controller, model, ViewModel và giao diện nằm trong từng `services/<TênService>/`; script vận hành trong `scripts/`. Công nghệ chính là ASP.NET Core MVC 10, Entity Framework Core, SQL Server, YARP, RabbitMQ và Docker Compose.

Nếu đã cài .NET SDK 10, có thể kiểm tra build bằng `dotnet build Tourbooking.sln`. Không còn project MVC nguyên khối ở gốc. Chạy hệ thống bằng Docker Compose; có thể chạy từng project bằng `dotnet run --project services/<TênService>/<TênService>.csproj` với cấu hình kết nối tương ứng.

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

## 10. Quyền sở hữu mã nguồn

| Service | Model sở hữu | Mã và giao diện |
| --- | --- | --- |
| Identity | ApplicationUser | Tài khoản, hồ sơ, quản lý người dùng |
| Catalog | Tour | Trang chủ, tìm kiếm và quản lý tour |
| Booking | Booking | Đặt, hủy, đổi lịch và quản trị booking |
| Payment | Payment | Trang thanh toán và yêu cầu xác nhận |
| Review | TourReview, TourReviewVote | Đánh giá và bình chọn |
| Support | ContactInquiry, ContactInquiryReply | Tư vấn và phản hồi |
| Reporting | Không sở hữu entity ghi | Dashboard và báo cáo Excel |
| Notification | NotificationRecord trong Models/ | Consumer RabbitMQ và API thông báo |
| Gateway | Không có entity nghiệp vụ | Reverse proxy và readiness tổng hợp |

Mỗi service biên dịch controller, ViewModel và View của chính mình; không tham chiếu project web của service khác. Model nghiệp vụ nằm trong `services/<TênService>/Models/`, được biên dịch thành thư viện model riêng. Namespace entity cũ được giữ để tương thích migration EF hiện có.

`Shared/Messaging` chứa hợp đồng sự kiện và topology RabbitMQ; `Shared/Routing` chứa bản đồ URL; `Shared/Hosting` chứa thiết lập host và xử lý HTTP dùng chung; `Shared/Presentation` chứa layout và kiểu hiển thị dùng chung. Đây là thư viện, không phải các service bổ sung.

`Shared/Persistence` hiện giữ DbContext, migration, SQL outbox và worker. Entity vẫn có navigation xuyên nghiệp vụ và các service vẫn truy cập database chung. Vì vậy đây là bước tách mã và tiến trình; chưa phải các microservice độc lập dữ liệu. Không được xóa navigation hoặc di chuyển migration riêng lẻ mà chưa thay truy vấn chéo bằng API/projection sự kiện và chuẩn bị migration dữ liệu.
