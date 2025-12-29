FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY ["src/Workers/PingWorker/PingWorker.csproj", "src/Workers/PingWorker/"]
COPY ["src/Domain/Domain.csproj", "src/Domain/"]
COPY ["src/Contracts/Contracts.csproj", "src/Contracts/"]

# Restore dependencies
RUN dotnet restore "src/Workers/PingWorker/PingWorker.csproj"

# Copy source code
COPY . .

# Build
WORKDIR "/src/src/Workers/PingWorker"
RUN dotnet build "PingWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "PingWorker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "PingWorker.dll"]
