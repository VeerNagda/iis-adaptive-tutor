# Backend image. Build context is server/server (see docker-compose.yml).

# Stage 1: build and publish with the full SDK.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Copy the project file first so the package restore layer is cached.
COPY server.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

# Stage 2: run on the smaller ASP.NET runtime image.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# The database file and uploaded documents live in /data, which compose mounts
# as a volume so they survive container restarts.
ENV ASPNETCORE_URLS=http://+:5242 \
    ConnectionStrings__Sqlite="Data Source=/data/adaptive_tutor.db" \
    Uploads__Path=/data/uploads
RUN mkdir -p /data
EXPOSE 5242
ENTRYPOINT ["dotnet", "server.dll"]
