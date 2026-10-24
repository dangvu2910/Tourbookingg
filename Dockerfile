FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY Tourbooking.sln ./
COPY services/Booking/Booking.csproj services/Booking/
COPY services/Booking/Models/Booking.Models.csproj services/Booking/Models/
COPY services/Catalog/Catalog.csproj services/Catalog/
COPY services/Catalog/Models/Catalog.Models.csproj services/Catalog/Models/
COPY services/Gateway/Gateway.csproj services/Gateway/
COPY services/Identity/Identity.csproj services/Identity/
COPY services/Identity/Models/Identity.Models.csproj services/Identity/Models/
COPY services/Notification/Notification.csproj services/Notification/
COPY services/Payment/Models/Payment.Models.csproj services/Payment/Models/
COPY services/Payment/Payment.csproj services/Payment/
COPY services/Reporting/Reporting.csproj services/Reporting/
COPY services/Review/Models/Review.Models.csproj services/Review/Models/
COPY services/Review/Review.csproj services/Review/
COPY services/Support/Models/Support.Models.csproj services/Support/Models/
COPY services/Support/Support.csproj services/Support/
COPY Shared/Hosting/Tourbooking.Hosting.csproj Shared/Hosting/
COPY Shared/Messaging/Tourbooking.Messaging.csproj Shared/Messaging/
COPY Shared/Persistence/Tourbooking.Persistence.csproj Shared/Persistence/
COPY Shared/Presentation/Tourbooking.Presentation.csproj Shared/Presentation/
COPY Shared/Routing/Tourbooking.Routing.csproj Shared/Routing/
COPY tests/RabbitMq.Tests/RabbitMq.Tests.csproj tests/RabbitMq.Tests/
RUN dotnet restore Tourbooking.sln
COPY . .
FROM source AS build
ARG SERVICE
RUN dotnet publish services/${SERVICE}/${SERVICE}.csproj -c Release -o /publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
COPY wwwroot ./wwwroot
ARG SERVICE
ENV SERVICE_DLL=${SERVICE}.dll
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
