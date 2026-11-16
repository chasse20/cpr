# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src
COPY src/Orchestrator/CPR.csproj src/Orchestrator/
COPY src/Worker/CPR.Worker.csproj src/Worker/
COPY src/Protocol/CPR.Protocol.csproj src/Protocol/
RUN dotnet restore src/Orchestrator/CPR.csproj && dotnet restore src/Worker/CPR.Worker.csproj
COPY src/Orchestrator/ src/Orchestrator/
COPY src/Worker/ src/Worker/
COPY src/Protocol/ src/Protocol/
RUN dotnet publish src/Orchestrator/CPR.csproj -c Release -o /published --no-restore -p:UseAppHost=false \
    && dotnet publish src/Worker/CPR.Worker.csproj -c Release -o /worker --no-restore -p:UseAppHost=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS final
ADD --chmod=0644 https://cli.github.com/packages/githubcli-archive-keyring.gpg /etc/apt/keyrings/githubcli-archive-keyring.gpg
RUN echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/githubcli-archive-keyring.gpg] https://cli.github.com/packages stable main" > /etc/apt/sources.list.d/github-cli.list \
    && apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client gh ca-certificates bash tar coreutils \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /home/app/.ssh /data/git \
    && chown -R "$APP_UID:$APP_UID" /home/app /data
WORKDIR /app
COPY --from=build /published ./
COPY --from=build /worker /opt/cpr-worker
ENV HOME=/home/app
ENV XDG_CACHE_HOME=/tmp/cache
ENV ASPNETCORE_URLS=http://0.0.0.0:5000
EXPOSE 5000
USER $APP_UID
ENTRYPOINT ["dotnet", "CPR.dll"]
