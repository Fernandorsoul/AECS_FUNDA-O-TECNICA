FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS base
WORKDIR /app
COPY . .
RUN dotnet restore AECS.slnx
RUN dotnet build AECS.slnx --configuration Release --no-restore

FROM base AS test
# Repository-aware integration tests need a commit for their disposable worktrees.
# Create a synthetic baseline only in the test stage; never copy host Git metadata.
RUN git init --initial-branch=container-validation \
    && git add -A \
    && git -c user.name="AECS Container Tests" -c user.email="aecs-tests@example.invalid" commit -m "Container validation baseline"
CMD ["dotnet", "test", "AECS.slnx", "--configuration", "Release", "--no-build", "--nologo", "--filter", "Category!=RealWorldE2E&Category!=PostgreSql&Category!=DockerSandbox"]

FROM base AS cli
ENTRYPOINT ["dotnet", "run", "--project", "src/AECS.Cli/AECS.Cli.csproj", "--configuration", "Release", "--"]
