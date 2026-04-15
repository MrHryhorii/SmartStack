# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies (cached by Docker for faster builds)
COPY ["ONNX_Runner.csproj", "./"]
RUN dotnet restore "ONNX_Runner.csproj"

# Copy the remaining source code
COPY . .

# Build the release version
RUN dotnet publish "ONNX_Runner.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 2: Runtime Environment
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# CRITICAL FOR LINUX: Install the native espeak-ng engine AND lame (for MP3 encoding).
RUN apt-get update && apt-get install -y \
    espeak-ng \
    libespeak-ng-dev \
    libmp3lame0 \
    libmp3lame-dev \
    && rm -rf /var/lib/apt/lists/*

# THE DEVOPS FIX: NAudio.Lame hardcodes the Windows DLL name.
# We create the expected directory and a symlink pointing to the actual Linux library.
RUN mkdir -p /app/runtimes/linux-x64/native/ && \
    ln -s /usr/lib/x86_64-linux-gnu/libmp3lame.so.0 /app/runtimes/linux-x64/native/libmp3lame.64.dll

# Copy the compiled application from the build stage
COPY --from=build /app/publish .

# Configure the server to listen on port 5000
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

# Define mount points (Volumes) for external models and voice caches
VOLUME ["/app/Model", "/app/Cloner", "/app/Voices"]

# Start the server
ENTRYPOINT ["dotnet", "ONNX_Runner.dll"]