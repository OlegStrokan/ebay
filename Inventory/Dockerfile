FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Inventory/Api/Api.csproj Inventory/Api/
COPY Inventory/Application/Application.csproj Inventory/Application/
COPY Inventory/Infrastructure/Infrastructure.csproj Inventory/Infrastructure/
COPY Inventory/Protos/Protos.csproj Inventory/Protos/

RUN dotnet restore Inventory/Api/Api.csproj

COPY . .
RUN dotnet publish Inventory/Api/Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN addgroup --system --gid 1001 appgroup \
 && adduser --system --uid 1001 --ingroup appgroup appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

RUN chown -R appuser:appgroup /app
USER appuser

EXPOSE 8080

ENTRYPOINT ["dotnet", "Api.dll"]
