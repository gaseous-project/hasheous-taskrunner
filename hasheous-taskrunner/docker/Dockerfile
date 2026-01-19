FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build-env
ARG TARGETARCH
ARG BUILDPLATFORM
WORKDIR /App

RUN echo "Target: $TARGETARCH"
RUN echo "Build: $BUILDPLATFORM"

# Copy everything
COPY . ./

# Build Hasheous Task Runner
# Restore as distinct layers
RUN dotnet restore "hasheous-taskrunner/hasheous-taskrunner.csproj" -a $TARGETARCH
# Build and publish a release (self-contained with single-file)
RUN dotnet publish "hasheous-taskrunner/hasheous-taskrunner.csproj" --self-contained true -c Release -o out -a $TARGETARCH

# Build runtime image - use minimal base since we have a self-contained executable
FROM mcr.microsoft.com/dotnet/runtime-deps:8.0
ENV INDOCKER=1

RUN apt update && apt upgrade -y && apt install -y iputils-ping && \
    # Create a non-root user for running the application
    useradd -m -u 1000 appuser

WORKDIR /App

# Copy the published application
COPY --from=build-env /App/out .

# Set proper permissions for the application
RUN chown -R appuser:appuser /App

# Create a home directory for the appuser with proper permissions
RUN mkdir -p /home/appuser/.hasheous-taskrunner && \
    chown -R appuser:appuser /home/appuser

VOLUME [ "/home/appuser/.hasheous-taskrunner" ]

# Switch to non-root user
USER appuser

ENTRYPOINT ["./hasheous-taskrunner"]
