# The rony command-line tool (Rony.Net.Cli) as an image.
#   docker build -t rony .
#   docker run --rm -p 127.0.0.1:4000:4000 -v "$PWD:/config" rony            # runs /config/mock.json
# The tool targets net8.0 and rolls forward to the .NET 10 runtime of the image (.NET 8 leaves support in November 2026).
# Inside the container the configuration file must listen on "address": "0.0.0.0".

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# global.json and the Directory.Build.props files are needed for the build
COPY global.json Directory.Build.props README.md ./
COPY src/Directory.Build.props src/
COPY src/Rony/ src/Rony/
COPY src/Rony.Net.Cli/ src/Rony.Net.Cli/
RUN dotnet publish src/Rony.Net.Cli -c Release -f net8.0 -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
COPY --from=build /app /app
# the non-root user of the image
USER $APP_UID
WORKDIR /config
ENTRYPOINT ["dotnet", "/app/Rony.Net.Cli.dll"]
CMD ["run", "/config/mock.json"]
