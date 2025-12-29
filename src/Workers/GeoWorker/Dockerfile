FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files
COPY ["src/Workers/GeoWorker/GeoWorker.csproj", "src/Workers/GeoWorker/"]
COPY ["src/Domain/Domain.csproj", "src/Domain/"]
COPY ["src/Contracts/Contracts.csproj", "src/Contracts/"]

# Restore dependencies
RUN dotnet restore "src/Workers/GeoWorker/GeoWorker.csproj"

# Copy source code
COPY . .

# Build
WORKDIR "/src/src/Workers/GeoWorker"
RUN dotnet build "GeoWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "GeoWorker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "GeoWorker.dll"]
