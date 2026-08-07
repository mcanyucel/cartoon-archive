# Build stage: full SDK, produces publish output
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build 

WORKDIR /src

# Copy csproj first and restore. If only source changes, `restore` is cached
COPY CartoonArchive.csproj ./ 
RUN dotnet restore 

# Copy the rest and publish 
COPY . .
RUN dotnet publish -c Release -o /out --no-restore

# Runtime stage: smaller aspnet image, just runs the dll
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime 

WORKDIR /app 
COPY --from=build --chown=$APP_UID:$APP_UID /out ./

# Pre-create data dir with correct ownership.
# Named volumes copy the image's ownership on first mount only.
RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data

# Base image ships a non-root 'app' user
USER $APP_UID 

# Override base 8080 to conventional asp.net port 
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

# Everything persistent lives here
# Compose must mount a volume here 
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "CartoonArchive.dll"]
