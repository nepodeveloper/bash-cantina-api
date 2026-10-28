FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json BashCantina.slnx ./
COPY src/Cantina.Domain/Cantina.Domain.csproj src/Cantina.Domain/
COPY src/Cantina.Application/Cantina.Application.csproj src/Cantina.Application/
COPY src/Cantina.Infrastructure/Cantina.Infrastructure.csproj src/Cantina.Infrastructure/
COPY src/Cantina.Api/Cantina.Api.csproj src/Cantina.Api/
RUN dotnet restore src/Cantina.Api/Cantina.Api.csproj
COPY src/ src/
RUN dotnet publish src/Cantina.Api/Cantina.Api.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
RUN mkdir -p /var/cantina/keys && chown "$APP_UID" /var/cantina/keys
COPY --from=build /app/publish .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Cantina.Api.dll"]
