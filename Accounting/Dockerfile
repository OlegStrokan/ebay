# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Accounting.sln ./
COPY Accounting/Api/Api.csproj                       Accounting/Api/
COPY Accounting/Application/Application.csproj       Accounting/Application/
COPY Accounting/Domain/Domain.csproj                 Accounting/Domain/
COPY Accounting/Infrastructure/Infrastructure.csproj Accounting/Infrastructure/
COPY Accounting/Protos/Protos.csproj                 Accounting/Protos/

RUN dotnet restore Accounting/Api/Api.csproj

COPY . .
RUN dotnet publish Accounting/Api/Api.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN addgroup --system --gid 1001 appgroup \
 && adduser  --system --uid 1001 --ingroup appgroup appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

RUN chown -R appuser:appgroup /app
USER appuser

EXPOSE 8080

ENTRYPOINT ["dotnet", "Api.dll"]
