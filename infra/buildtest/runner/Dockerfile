FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet10
FROM mcr.microsoft.com/playwright/dotnet:v1.44.0-jammy

USER root
COPY --from=dotnet10 /usr/share/dotnet /usr/share/dotnet

# Node is a HARD requirement, not a convenience: MarkdownRendererFailClosedTests executes
# the real wwwroot/js/markdown.js under Node with the third-party globals deliberately
# absent, and those tests fail rather than skip when Node cannot be launched. The
# playwright/dotnet base image ships a Node runtime for its own driver, but it is not
# guaranteed to be a `node` on PATH for arbitrary child processes, so the runner installs
# one explicitly. Without it the gate reports 8 failures that have nothing to do with the
# change under test.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl git tar gzip \
    && curl -fsSL https://deb.nodesource.com/setup_20.x -o /tmp/nodesource_setup.sh \
    && bash /tmp/nodesource_setup.sh \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -f /tmp/nodesource_setup.sh \
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

COPY entrypoint.ps1 RunnerResult.ps1 /runner/
WORKDIR /work

# Fail the IMAGE BUILD, not a later test run, if a hard runner dependency is missing.
# A missing `node` previously surfaced as 8 unrelated test failures inside the gate, which
# reads as "the change under test broke markdown rendering" rather than "the image is
# incomplete". Asserting it here makes the image the thing that fails when the image is wrong.
RUN node --version && dotnet --version && pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'

ENTRYPOINT ["pwsh", "-NoProfile", "-File", "/runner/entrypoint.ps1"]
