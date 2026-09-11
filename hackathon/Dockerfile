FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NuGet.Config ./
COPY kb-app/KbApp.csproj kb-app/
RUN dotnet restore kb-app/KbApp.csproj --configfile NuGet.Config
COPY kb-app/ kb-app/
RUN dotnet publish kb-app/KbApp.csproj --no-restore -c Release -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out ./
COPY knowledge-base/ /app/seed/knowledge-base/
RUN mkdir -p /app/state && chown -R app:app /app/state
ENV ASPNETCORE_HTTP_PORTS=8080 \
    SCOPE_READ_ONLY=true \
    SCOPE_STATE_PATH=/app/state \
    SCOPE_SEED_PATH=/app/seed/knowledge-base
USER app
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "KbApp.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "KbApp.dll"]
