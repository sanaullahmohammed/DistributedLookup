FROM mcr.microsoft.com/dotnet/runtime:10.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files (for better layer caching)
COPY ["src/Workers/ReverseDnsWorker/ReverseDnsWorker.csproj", "src/Workers/ReverseDnsWorker/"]
COPY ["src/Domain/Domain.csproj", "src/Domain/"]
COPY ["src/Contracts/Contracts.csproj", "src/Contracts/"]
COPY ["src/Infrastructure/Infrastructure.csproj", "src/Infrastructure/"]

# Restore dependencies
RUN dotnet restore "src/Workers/ReverseDnsWorker/ReverseDnsWorker.csproj"

# Copy source code
COPY . .

# Build
WORKDIR "/src/src/Workers/ReverseDnsWorker"
RUN dotnet build "ReverseDnsWorker.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "ReverseDnsWorker.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ReverseDnsWorker.dll"]
