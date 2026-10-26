FROM mcr.microsoft.com/dotnet/sdk:10.0 AS source
WORKDIR /src
COPY . .
RUN dotnet restore Tourbooking.sln
FROM source AS build
ARG SERVICE
RUN dotnet publish services/${SERVICE}/${SERVICE}.csproj -c Release -o /publish --no-restore -m:1

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
COPY wwwroot ./wwwroot
ARG SERVICE
ENV SERVICE_DLL=${SERVICE}.dll
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$SERVICE_DLL\""]
