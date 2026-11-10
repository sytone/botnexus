FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet10
FROM mcr.microsoft.com/playwright/dotnet:v1.44.0-jammy AS build
USER root
COPY --from=dotnet10 /usr/share/dotnet /usr/share/dotnet
ENV NuGetAudit=false
WORKDIR /src
ARG SOURCE_COMMIT
ARG SOURCE_CONTEXT_DIGEST
RUN test "$(printf '%s' "$SOURCE_COMMIT" | grep -E '^[0-9a-f]{40}$')" = "$SOURCE_COMMIT"
COPY source/ .
# Bind the exported source BEFORE Git metadata, restore or publish generates files.
RUN actual=$(find . -type f -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1) \
 && test "$actual" = "$SOURCE_CONTEXT_DIGEST" \
 && printf '%s\n' "$actual" > /app-source-tree.sha256
RUN mkdir /approved-source && cp -a /src/. /approved-source/
RUN git init -b main \
 && git config user.name 'BotNexus Evidence' \
 && git config user.email 'evidence@botnexus.invalid' \
 && git add --force . \
 && git commit -m 'chore: materialize evidence source' \
 && git update-ref refs/remotes/origin/main HEAD \
 && cp -a /src/.git /approved-source/.git
RUN dotnet restore src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj -p:NuGetAudit=false \
 && dotnet publish src/gateway/BotNexus.Gateway.Api/BotNexus.Gateway.Api.csproj -c Release -o /app/publish --no-restore -p:NuGetAudit=false \
 && extension=BotNexus.Extensions.Channels.SignalR \
 && mkdir -p "/app/extensions/$extension" \
 && dotnet publish "src/extensions/$extension/$extension.csproj" -c Release -o "/app/extensions/$extension" -p:NuGetAudit=false \
 && cp "src/extensions/$extension/botnexus-extension.json" "/app/extensions/$extension/botnexus-extension.json"
# The gateway/extension graph does not depend on Playwright. Restore the driver explicitly
# in the image using the central version before consuming its bundled Node/package files.
RUN dotnet restore skills/botnexus-ui-evidence/container/PlaywrightDriver.csproj --packages /root/.nuget/packages -p:NuGetAudit=false
RUN mkdir -p /app/playwright/node_modules/playwright-core /app/playwright/node_modules/playwright \
 && cp -a /root/.nuget/packages/microsoft.playwright/1.44.0/.playwright/package/. /app/playwright/node_modules/playwright-core/ \
 && cp /root/.nuget/packages/microsoft.playwright/1.44.0/.playwright/node/linux-x64/node /app/playwright/node \
 && printf '{"name":"playwright","version":"1.44.0","type":"commonjs","main":"index.js"}\n' > /app/playwright/node_modules/playwright/package.json \
 && printf "module.exports = require('playwright-core');\n" > /app/playwright/node_modules/playwright/index.js
RUN printf '%s\n' "$SOURCE_COMMIT" > /app/source-commit && cp /app-source-tree.sha256 /app/source-tree

FROM mcr.microsoft.com/playwright/dotnet:v1.44.0-jammy
USER root
COPY --from=dotnet10 /usr/share/dotnet /usr/share/dotnet
WORKDIR /app
COPY --from=build /app/publish /app/publish
COPY --from=build /app/extensions /app/extensions
COPY --from=build /app/playwright /app/playwright
COPY --from=build /approved-source /app/source
COPY --from=build /app/source-commit /app/source-commit
COPY --from=build /app/source-tree /app/source-tree
COPY --from=build /app/playwright/node /usr/local/bin/node
COPY --from=build /approved-source/skills/botnexus-ui-evidence/container/entrypoint.mjs /app/entrypoint.mjs
COPY --from=build /approved-source/skills/botnexus-ui-evidence/container/capture.mjs /app/capture.mjs
COPY --from=build /approved-source/skills/botnexus-ui-evidence/container/capture-menu.mjs /app/capture-menu.mjs
COPY --from=build /approved-source/skills/botnexus-ui-evidence/container/capture-sidebar.mjs /app/capture-sidebar.mjs
RUN command -v timeout && chmod 0755 /usr/local/bin/node
ENV NODE_PATH=/app/playwright/node_modules
ENTRYPOINT ["timeout","--signal=TERM","--kill-after=10s","1490s","node","/app/entrypoint.mjs"]
