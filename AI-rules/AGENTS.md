# Quy tắc phát triển backend: Docker, dữ liệu và giao tiếp service

Tài liệu dành cho coding agent và thành viên dự án đặt tour. Có thể dùng làm AGENTS.md ở gốc repository hoặc ghép các mục dưới đây vào AGENTS.md đang có. Không ghi đè các quy định dự án hiện có nếu chưa đối chiếu.

> **Mục tiêu**: Đảm bảo sự nhất quán tuyệt đối về cách đặt tên, kiến trúc lớp, quản lý thời gian và phong cách viết mã sạch (Clean Code) 
## 1. Phạm vi và quyết định kiến trúc

- Áp dụng Clean Code, Clean Architecture và nguyên tắc sở hữu dữ liệu của microservices. Đây là ba khái niệm bổ trợ nhau; Clean Code không tự yêu cầu phải tách DB.
- Mỗi service là một đơn vị triển khai độc lập, có Dockerfile và cấu hình riêng.
- Có thể chạy tất cả container trên một VPS. Không bắt buộc một VPS cho mỗi service hoặc một VPS riêng cho DB.
- Dùng chung SQL Server instance được phép. Mặc định Identity, Catalog và Booking có database riêng trong instance đó.
- Nếu repository đang dùng chung TravelDB, giữ tương thích và phân quyền theo schema trước khi đề xuất migration tách DB; không tự tách hoặc di chuyển dữ liệu đang tồn tại.
- Chung máy chủ DB không đồng nghĩa các service được đọc chung mọi bảng.
- Không tạo một service CRUD tổng quát để mọi service đi qua đó truy cập tất cả dữ liệu. Mỗi service nghiệp vụ cung cấp API cho dữ liệu mình sở hữu.

## 2. Quyền sở hữu dữ liệu

| Service | Dữ liệu sở hữu | DB mặc định |
| --- | --- | --- |
| Identity | Tài khoản, thông tin xác thực, vai trò; đánh giá tour (module Review) | IdentityDB trên SQL Server |
| Catalog | Tour, danh mục, lịch khởi hành, giá, số chỗ; báo cáo chỉ đọc (module Reporting) | CatalogDB trên SQL Server |
| Booking | Đơn đặt tour, thanh toán | BookingDB trên SQL Server |
| Customer/Chat | Liên hệ, hội thoại, tin nhắn | MongoDB riêng hoặc SQLite riêng theo cấu hình dự án |

Cấu trúc thư mục bắt buộc cho mỗi backend: xem mục "Cấu trúc thư mục service" trong `docs/architecture.md`.
| Notification | Thông báo, trạng thái gửi | MongoDB riêng hoặc SQLite riêng theo cấu hình dự án |

Chỉ service sở hữu dữ liệu được tạo bảng, migration, đọc và ghi trực tiếp dữ liệu đó. Những tên DB và bảng trong tài liệu là quy ước đề xuất, không phải bằng chứng về code hiện có.

Nếu dùng chung TravelDB, chia schema identity, catalog, booking. Dùng tài khoản riêng và chỉ cấp quyền trên schema tương ứng. Không cấp db_owner cho runtime. Migration dùng credential riêng có quyền DDL trong phạm vi được quản lý.

## 3. Truy vấn bảng: được phép và bị cấm

| Thao tác | Quy định |
| --- | --- |
| Booking dùng repository đọc bảng Bookings của Booking | Được phép |
| Booking JOIN Bookings và BookingItems cùng thuộc Booking | Được phép |
| Catalog dùng ORM hoặc SQL tham số hóa đọc Tours của Catalog | Được phép |
| Booking SELECT trực tiếp Tours của Catalog | Cấm |
| Booking JOIN bảng Orders của mình với bảng Users của Identity | Cấm trong luồng runtime |
| Booking tạo DbContext hoặc connection tới CatalogDB | Cấm |
| Booking dùng view, stored procedure, linked server hoặc synonym để đọc bảng Catalog | Cấm; đây vẫn là truy cập xuyên ranh giới |
| Booking đọc bản sao TourSummary trong DB của Booking do sự kiện cập nhật | Được phép; dữ liệu có thể trễ |
| Frontend kết nối thẳng SQL Server hoặc MongoDB | Cấm |

Ví dụ hợp lệ, chạy trong repository của Booking:

```sql
SELECT Id, TourId, UserId, Status
FROM dbo.Bookings
WHERE UserId = @UserId;
```

Ví dụ bị cấm, chạy từ Booking:

```sql
SELECT b.Id, t.Name
FROM BookingDB.dbo.Bookings AS b
JOIN CatalogDB.dbo.Tours AS t ON t.Id = b.TourId;
```

Các ví dụ SQL chỉ minh họa ranh giới sở hữu. JOIN nội bộ một service là bình thường. SQL trực tiếp không xấu nếu được đóng gói trong Infrastructure và dùng tham số; ORM không làm một truy vấn xuyên service trở nên hợp lệ.

## 4. Lấy dữ liệu của service khác

- Dùng HTTP REST hoặc gRPC khi cần kết quả ngay. Chọn công nghệ đang có trong repository, không thêm cả hai nếu không cần.
- Dùng RabbitMQ cho sự kiện như BookingCreated, PaymentSucceeded, MessageSent.
- RabbitMQ là message broker, không phải DB dùng chung, không thay thế foreign key và không tự bảo đảm nhất quán tức thì.
- Service gọi service khác qua client interface trong Application; triển khai HTTP/gRPC ở Infrastructure.
- Gọi nội bộ bằng tên service Docker, ví dụ http://catalog-service:8080. Cổng là cổng ứng dụng lắng nghe trong container, không nhất thiết là cổng publish trên host.
- Không bắt buộc mọi request nội bộ phải vòng qua Kong. Kong xử lý truy cập từ client theo cấu hình dự án; nội bộ vẫn cần xác thực và phân quyền phù hợp.
- Trả DTO qua API, không trả trực tiếp ORM entity, DbContext hoặc IQueryable.
- Không chia sẻ ORM entity, repository, DbContext hoặc migration giữa các service. Chỉ chia sẻ contract thật sự cần thiết và quản lý tương thích phiên bản.
- Gộp truy vấn bằng batch API khi cần lấy nhiều ID; tránh một request riêng cho từng dòng dữ liệu.

Ví dụ Booking muốn hiển thị tên tour: gọi Catalog API rồi ghép DTO, hoặc đọc TourSummary do Booking lưu từ sự kiện. Không JOIN vào bảng Tours của Catalog.

## 5. ID tham chiếu và dữ liệu lịch sử

- Bookings có thể lưu UserId và TourId do service khác cấp. Đây là tham chiếu logic, không tạo foreign key xuyên service.
- Foreign key giữa các bảng cùng thuộc một service vẫn được khuyến khích khi phù hợp.
- Booking lưu snapshot tên tour, ngày khởi hành và giá đã chốt để lịch sử đơn không đổi khi Catalog sửa dữ liệu.
- Không sao chép mật khẩu, token hoặc toàn bộ hồ sơ người dùng vào Booking.
- Khi cần kiểm tra tham chiếu, gọi API chủ sở hữu. Kiểm tra tồn tại không tự bảo đảm bản ghi sẽ không thay đổi ngay sau đó; quy trình nghiệp vụ phải xử lý hủy/ngừng hoạt động theo contract.

## 6. Luồng đặt tour và tính nhất quán

1. Booking xác thực người gọi và kiểm tra quyền. Không tin UserId do client gửi nếu chưa kiểm tra.
2. Booking gọi Catalog để giữ chỗ bằng một request có idempotency key.
3. Catalog kiểm tra và giảm/giữ số chỗ bằng transaction hoặc cập nhật có điều kiện nguyên tử trong DB của Catalog, trả reservationId, giá đã chốt và thời hạn giữ chỗ.
4. Booking lưu đơn ở trạng thái Pending và reservationId trong transaction cục bộ của Booking.
5. Khi thanh toán hoàn tất, luồng nghiệp vụ xác nhận giữ chỗ và chuyển trạng thái đơn theo contract đã định nghĩa.
6. Nếu lưu đơn/thanh toán thất bại, yêu cầu hủy giữ chỗ. Catalog phải có cơ chế hết hạn giữ chỗ để xử lý cả trường hợp Booking bị crash trước khi gửi hủy.

- Không đọc số chỗ rồi tự trừ trong Booking. Catalog chịu trách nhiệm chống bán vượt số chỗ.
- Không giữ transaction DB mở trong lúc chờ HTTP, RabbitMQ hoặc cổng thanh toán.
- Không mặc định dùng transaction phân tán/2PC. Dùng trạng thái rõ ràng và thao tác bù theo Saga khi nghiệp vụ trải qua nhiều service.
- Timeout không đồng nghĩa thao tác phía bên kia chưa thực hiện. Tra cứu lại trạng thái bằng request ID; không retry mù thao tác thanh toán hoặc giữ chỗ.
- Đây là luồng thiết kế đề xuất; agent phải đối chiếu payment provider và code hiện có trước khi triển khai.

## 7. RabbitMQ và xử lý sự kiện tin cậy

- Khi thay đổi DB phải phát sự kiện tin cậy, ghi bản ghi nghiệp vụ và Outbox trong cùng transaction cục bộ.
- Worker gửi Outbox đến RabbitMQ, xác nhận broker theo cơ chế phù hợp rồi đánh dấu đã gửi. Chấp nhận khả năng gửi trùng.
- Consumer xử lý idempotent bằng EventId/khóa nghiệp vụ và unique constraint; dùng Inbox hoặc cơ chế tương đương khi cần.
- Với tác động trong DB, ghi dấu đã xử lý và cập nhật nghiệp vụ trong cùng transaction. Chỉ ACK sau khi commit thành công.
- Với tác động ngoài DB như gửi email/thu tiền, cần idempotency key hoặc cơ chế phục hồi riêng; Inbox đơn thuần không bảo đảm chỉ tác động một lần.
- Dùng retry hữu hạn, backoff và dead-letter queue cho lỗi kéo dài. Không tạo vòng lặp retry vô hạn.
- Event có EventId, EventType, Version, OccurredAt, CorrelationId và payload tối thiểu cần thiết.
- Không hứa exactly-once cho toàn bộ hệ thống. Read model cập nhật qua sự kiện có eventual consistency.

## 8. Cấu trúc Clean Architecture

| Tầng | Trách nhiệm | Không đặt tại đây |
| --- | --- | --- |
| API | HTTP endpoint, ánh xạ request/response, middleware | SQL, nghiệp vụ phức tạp |
| Application | Use case, điều phối, interface repository/client | DbContext, HTTP client cụ thể |
| Domain | Entity, value object, quy tắc nghiệp vụ | Docker, HTTP, ORM, RabbitMQ |
| Infrastructure | Repository, ORM, HTTP client, message broker | Quy tắc nghiệp vụ thuộc Domain |

- Domain không phụ thuộc các tầng bên ngoài. Application phụ thuộc Domain. Infrastructure triển khai interface của tầng trong. API cấu hình dependency injection ở composition root.
- Không để Domain/Application tham chiếu project Infrastructure. API có thể tham chiếu Infrastructure để đăng ký DI.
- Dùng repository hoặc query abstraction có mục đích rõ ràng; không bắt buộc tạo generic repository chỉ bọc lại toàn bộ ORM.
- Dùng dependency injection, tên biến rõ nghĩa, hàm tập trung một trách nhiệm. Không catch exception rồi trả thành công hoặc trả dữ liệu giả.
- Các ví dụ trước dùng C# chỉ để minh họa. Giữ ngôn ngữ, framework và phiên bản sẵn có của repository.

## 9. Build image và chạy container

Phân biệt: Dockerfile mô tả cách build image; container là tiến trình chạy từ image; Compose mô tả các container, network, volume và cấu hình liên quan. SQL Server dùng image có sẵn, không cần tự viết một DB engine.

### 9.1 Quy tắc Dockerfile

- Mỗi service có Dockerfile; build context chỉ chứa các file cần cho service và dependency hợp lệ.
- Dùng multi-stage build khi phù hợp: build/test trong stage SDK/toolchain, runtime dùng image nhỏ hơn.
- Pin phiên bản hoặc digest phù hợp với runtime thực tế. Không tự nâng major version và không dùng latest trong cấu hình triển khai đã chốt.
- Chạy non-root khi image hỗ trợ. Không COPY .env, credential, DB file hoặc thư mục build thừa vào image.
- Có .dockerignore để loại .git, .env, secret, dữ liệu local và cache không cần thiết; không loại lockfile cần cho build.
- Không đặt password/token trong Dockerfile, build argument, source hoặc log. Dùng secret injection của môi trường; .env local phải bị gitignore.
- Ứng dụng phải lắng nghe trên interface cho phép container khác truy cập, thường là 0.0.0.0, với cổng được cấu hình rõ ràng.

### 9.2 Quy tắc Compose và kết nối DB

- Compose gồm các service nghiệp vụ, SQL Server, RabbitMQ, MongoDB nếu có, và Kong theo kiến trúc thực tế.
- SQL Server có named volume gắn đúng thư mục dữ liệu của image. MongoDB và RabbitMQ cần persistence khi yêu cầu lưu dữ liệu qua lần tạo lại container.
- Trong cùng Docker network, dùng DNS service name: SQL Server là sqlserver, RabbitMQ là rabbitmq. Không hardcode IP container.
- localhost trong Booking container là chính Booking container, không phải SQL Server hoặc máy host.
- Booking dùng connection string đến BookingDB với booking_app; Catalog dùng CatalogDB với catalog_app. Không cấp connection string DB của service khác cho runtime.
- Nếu chung TravelDB, mỗi runtime vẫn có user và schema riêng; permission của DB phải chặn truy cập ngoài sở hữu, không chỉ dựa vào quy ước code.
- Không publish cổng DB ra Internet. Nếu cần quản trị local, bind cổng vào 127.0.0.1 qua override dành cho development. Khi DB ở máy khác, dùng mạng riêng/VPN và cấu hình TLS phù hợp.
- Docker network chỉ cung cấp khả năng kết nối, không thay thế xác thực hoặc quyền truy cập DB.
- Dùng healthcheck thực sự kiểm tra readiness; depends_on đơn giản chỉ sắp xếp khởi động. Dùng condition: service_healthy khi cần chờ dependency có healthcheck.
- Ứng dụng vẫn phải xử lý mất kết nối và reconnect; readiness lúc khởi động không bảo đảm dependency luôn hoạt động.
- Tạo DB/user/schema bằng provisioning hoặc job có kiểm soát. Migration chạy riêng cho từng service, tránh nhiều replica cùng chạy migration không phối hợp.
- Không giả định SQL Server image tự chạy script trong thư mục init theo quy ước của image DB khác; phải triển khai bước provisioning phù hợp với image đã chọn.
- SQLite là file DB cục bộ: mount volume riêng cho service sở hữu. Không chia sẻ một file SQLite qua mạng cho nhiều service hoặc nhiều replica; đánh giá đổi DB trước khi scale ngang.

Ví dụ tên cấu hình, cần ánh xạ sang framework thực tế; không phải file Compose hoàn chỉnh:

```text
Booking:
  DB_HOST=sqlserver
  DB_PORT=1433
  DB_NAME=BookingDB
  DB_USER=booking_app
  DB_PASSWORD=<secret injected at runtime>
  CATALOG_BASE_URL=http://catalog-service:8080
  RABBITMQ_HOST=rabbitmq

Catalog:
  DB_HOST=sqlserver
  DB_NAME=CatalogDB
  DB_USER=catalog_app
  DB_PASSWORD=<different secret injected at runtime>
```

### 9.3 Trình tự triển khai

1. Đọc repository, Dockerfile, compose, biến môi trường mẫu, migration và test hiện có.
2. Xác định bảng và credential thuộc từng service; xử lý truy cập xuyên service trước khi tuyên bố kiến trúc đã độc lập.
3. Build image từng service; chuẩn bị volume và cấu hình runtime.
4. Khởi động DB/broker, chờ readiness, chạy provisioning và migration theo quy trình dự án.
5. Khởi động service rồi gateway; kiểm tra luồng API và sự kiện.
6. Kiểm tra dữ liệu còn sau khi tạo lại container mà không xóa volume; thao tác này chỉ thực hiện trên môi trường test.

Các lệnh mẫu (thêm `-f compose.hybrid.yaml`), chạy tại thư mục gốc repo sau khi đã cấu hình môi trường:

```bash
# Kiểm tra cấu hình mà không in toàn bộ giá trị đã resolve ra log
docker compose config --quiet

# Build image ứng dụng được khai báo bằng build trong Compose
docker compose build

# Khởi động/cập nhật container; không tự chạy bước provisioning chưa được định nghĩa
docker compose up -d

# Kiểm tra trạng thái và log, không chia sẻ log chứa dữ liệu nhạy cảm
docker compose ps
docker compose logs --tail=100 booking-service catalog-service

# Dừng và xóa container/network do Compose quản lý; named volume được giữ
docker compose down
```

Không chạy docker compose down -v hoặc xóa volume đang chứa dữ liệu thật nếu không có yêu cầu rõ ràng và bản backup có thể khôi phục. Volume không thay thế backup.

## 10. HTTP, lỗi và truy cập an toàn

- HTTP client có timeout, cancellation và ánh xạ lỗi rõ ràng. Retry hữu hạn chỉ cho thao tác an toàn hoặc có cơ chế idempotency đúng.
- Không tin request nội bộ mặc nhiên hợp lệ. Kiểm tra identity, quyền và phạm vi truy cập theo contract.
- Không dùng JWT chưa kiểm tra chữ ký, issuer, audience và thời hạn. Không ghi token hoặc connection string vào log.
- Dùng CorrelationId xuyên HTTP và message để theo dõi luồng. Log trạng thái nghiệp vụ và lỗi có cấu trúc.
- Không đánh đồng dependency timeout với không tìm thấy dữ liệu; trả lỗi phù hợp để client biết thử lại hay sửa request.
- Dùng SQL tham số hóa; không nối chuỗi input thành câu lệnh SQL.

## 11. Tiêu chí hoàn thành thay đổi

- [ ] Nêu rõ service và dữ liệu bị ảnh hưởng.
- [ ] Runtime không có credential hoặc query truy cập DB/schema của service khác.
- [ ] DB permission thực sự từ chối truy cập trái quyền; kiểm tra bằng user runtime trên DB test khi đổi phân quyền.
- [ ] Domain/Application không phụ thuộc SDK của DB, HTTP client cụ thể hoặc broker.
- [ ] API và event contract có validation, xác thực, version/tương thích phù hợp.
- [ ] Có kiểm tra có ý nghĩa cho nghiệp vụ thay đổi; khi sửa đặt chỗ phải kiểm tra cạnh tranh chỗ, timeout và request trùng.
- [ ] Khi sửa consumer, kiểm tra sự kiện trùng và phục hồi sau lỗi.
- [ ] Build và kiểm tra cấu hình Compose khi thay đổi Docker; chỉ chạy integration test khi có môi trường phù hợp.
- [ ] Không có secret trong source/image/log; dữ liệu persistent có volume và kế hoạch backup.
- [ ] Báo rõ đã chạy kiểm tra nào, kiểm tra nào chưa chạy và lý do. Không tuyên bố đã deploy hoặc test nếu chỉ sửa tài liệu.

## 12. Chỉ dẫn cho coding agent

Trước khi sửa code, đọc AGENTS.md theo phạm vi thư mục, xác định stack, DB ownership và cách test hiện có. Thực hiện thay đổi nhỏ, giữ tương thích API và không thêm framework/pattern nếu chưa có nhu cầu cụ thể. Nếu phát hiện truy cập bảng xuyên service, nêu vị trí và thay bằng client API hoặc read model phù hợp trong phạm vi nhiệm vụ. Không tự thực hiện migration phá hủy dữ liệu, chuyển DB hoặc deploy production chỉ vì tài liệu này đưa ra kiến trúc đề xuất.

## 13. Tài liệu tham chiếu

- Docker Compose networking: https://docs.docker.com/compose/how-tos/networking/
- Compose service configuration và health dependencies: https://docs.docker.com/reference/compose-file/services/

Các quy định sở hữu dữ liệu là quyết định kiến trúc cho dự án này. Các ví dụ là hướng dẫn, chưa được build hoặc chạy với repository thực tế vì chưa có source dự án trong yêu cầu.
# Project instructions

Read and follow `AI-rules/AGENTS.md` and `AI-rules/api-guidelines.md` before changing this repository. Preserve existing databases and the legacy MVC routes. The SQL Server/MongoDB stack uses `compose.hybrid.yaml`; The old SQLite stack (`compose.yaml`) was removed on 2026-11-13 after every row was verified in SQL Server/MongoDB (SQLite archives deleted); back up with `scripts/Backup.ps1` before risky data work.

Locally each service runs as one container on one Compose network with Kong as the only entry. There is no cloud deployment target (AWS plans were dropped on 2026-11-13); do not introduce Docker Swarm or cloud infrastructure unless asked.
