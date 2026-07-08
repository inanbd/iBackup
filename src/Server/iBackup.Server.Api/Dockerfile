# Build and run the iBackup API. Build context is the repository root:
#   docker build -f src/Server/iBackup.Server.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY src/Shared/iBackup.Shared/iBackup.Shared.csproj src/Shared/iBackup.Shared/
COPY src/Server/iBackup.Server.Domain/iBackup.Server.Domain.csproj src/Server/iBackup.Server.Domain/
COPY src/Server/iBackup.Server.Application/iBackup.Server.Application.csproj src/Server/iBackup.Server.Application/
COPY src/Server/iBackup.Server.Infrastructure/iBackup.Server.Infrastructure.csproj src/Server/iBackup.Server.Infrastructure/
COPY src/Server/iBackup.Server.Api/iBackup.Server.Api.csproj src/Server/iBackup.Server.Api/
RUN dotnet restore src/Server/iBackup.Server.Api/iBackup.Server.Api.csproj

COPY src/ src/
RUN dotnet publish src/Server/iBackup.Server.Api/iBackup.Server.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
COPY database/ ./database/
EXPOSE 8080
ENTRYPOINT ["dotnet", "iBackup.Server.Api.dll"]
