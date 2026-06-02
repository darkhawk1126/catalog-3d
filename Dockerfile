# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution and project files first for layer-cached restore
COPY Catalog3d.slnx ./
COPY src/Catalog3d.Domain/Catalog3d.Domain.csproj src/Catalog3d.Domain/
COPY src/Catalog3d.Application/Catalog3d.Application.csproj src/Catalog3d.Application/
COPY src/Catalog3d.Infrastructure/Catalog3d.Infrastructure.csproj src/Catalog3d.Infrastructure/
COPY src/Catalog3d.Web/Catalog3d.Web.csproj src/Catalog3d.Web/
COPY tests/Catalog3d.Tests/Catalog3d.Tests.csproj tests/Catalog3d.Tests/

RUN dotnet restore src/Catalog3d.Web/Catalog3d.Web.csproj

# Copy the full source tree and publish
COPY . .
RUN dotnet publish src/Catalog3d.Web/Catalog3d.Web.csproj \
    --no-restore \
    -c Release \
    -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup --system --gid 1001 catalog3d \
 && adduser  --system --uid 1001 --ingroup catalog3d catalog3d

COPY --from=build /app/publish .

# Blob storage mount point; bind-mounted in dev, PVC in prod
RUN mkdir -p /data && chown catalog3d:catalog3d /data
VOLUME ["/data"]

USER catalog3d

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Catalog3d.Web.dll"]
