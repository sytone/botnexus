FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet10
FROM mcr.microsoft.com/playwright/dotnet:v1.44.0-jammy

USER root
COPY --from=dotnet10 /usr/share/dotnet /usr/share/dotnet

RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git tar gzip \
    && curl -fsSL "https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb" -o /tmp/packages-microsoft-prod.deb \
    && dpkg -i /tmp/packages-microsoft-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends powershell \
    && curl -fsSL "https://aka.ms/downloadazcopy-v10-linux" -o /tmp/azcopy.tar.gz \
    && tar -xzf /tmp/azcopy.tar.gz -C /tmp \
    && install -m 0755 /tmp/azcopy_linux_amd64_*/azcopy /usr/local/bin/azcopy \
    && rm -rf /tmp/azcopy* /tmp/packages-microsoft-prod.deb /var/lib/apt/lists/* \
    && mkdir -p /work /cache/nuget \
    && chmod 0777 /work /cache/nuget

COPY entrypoint.ps1 /runner/entrypoint.ps1
WORKDIR /work
ENTRYPOINT ["pwsh", "-NoProfile", "-File", "/runner/entrypoint.ps1"]
