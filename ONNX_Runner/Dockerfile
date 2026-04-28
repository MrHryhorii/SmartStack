# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies (cached by Docker for faster builds)
COPY ["ONNX_Runner.csproj", "./"]
RUN dotnet restore "ONNX_Runner.csproj" -r linux-x64 -p:CpuOnly=true

# Copy the remaining source code
COPY . .

# Build the release version
RUN dotnet publish "ONNX_Runner.csproj" -c Release -r linux-x64 -p:CpuOnly=true -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime Environment
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# CRITICAL FOR LINUX: Install the native espeak-ng engine AND lame (for MP3 encoding).
RUN apt-get update && apt-get install -y \
    espeak-ng \
    libmp3lame0 \
    && rm -rf /var/lib/apt/lists/*

# Copy the compiled application from the build stage
COPY --from=build /app/publish .

# Configure the server to listen on port 5045
ENV ASPNETCORE_URLS=http://+:5045
EXPOSE 5045

# Define mount points (Volumes) for external models and voice caches
VOLUME ["/app/Model", "/app/Cloner", "/app/Voices"]

# Start the server
ENTRYPOINT ["dotnet", "TsubakiTTS.dll"]