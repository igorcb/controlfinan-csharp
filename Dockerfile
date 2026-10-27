# Development image: .NET 9 SDK and the Tailwind standalone CLI.
FROM mcr.microsoft.com/dotnet/sdk:9.0
RUN curl -fsSL --retry 6 --retry-all-errors --retry-delay 5 -o /usr/local/bin/tailwindcss \
       https://github.com/tailwindlabs/tailwindcss/releases/download/v4.3.3/tailwindcss-linux-x64 \
    && chmod +x /usr/local/bin/tailwindcss
ARG UID=1000
ARG GID=1000
RUN (getent group ${GID} || groupadd -g ${GID} dev) && (getent passwd ${UID} || useradd -m -u ${UID} -g ${GID} dev) \
    && mkdir -p /home/app/.nuget && chown -R ${UID}:${GID} /home/app
USER ${UID}:${GID}
ENV HOME=/home/app DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /app
EXPOSE 8000
CMD ["bin/dev"]
