# Hasheous Task Runner

A distributed task runner service built in .NET 8.0 that registers with a central task server and executes queued tasks. It supports AI-powered task processing and can be deployed as a self-contained executable on any platform.

## Features

- **Distributed Architecture**: Registers with Hasheous and processes tasks asynchronously
- **AI Task Support**: Executes AI description generation and tagging tasks via Ollama integration
- **Capability-Based System**: Declares capabilities (Internet, Disk Space, AI) to the server
- **Cross-Platform**: Runs on Windows, Linux, and macOS (x64 and ARM64)
- **Self-Contained Deployment**: Distributes as single-file executable with runtime included
- **Configuration Flexibility**: Loads settings from defaults, config files, environment variables, and CLI arguments
- **Windows Service Support**: Install and manage as a Windows service for automatic startup
- **Automatic Updates**: Periodically checks for new releases on GitHub and auto-updates when available
- **Security Hardening**: API key protection, SHA256 checksum verification for updates, secure credential handling
- **Resilient Error Handling**: Comprehensive error handling with component isolation and automatic recovery
- **Smart Retry Logic**: Exponential backoff with jitter for graceful failure recovery

## Installation

### Prerequisites

- **For AI Workloads**: Ollama (see https://ollama.com/)

### Download

Pre-built executables are available for:
- **Linux**: x64, ARM64
- **Windows**: x64, ARM64
- **macOS**: x64 (Intel), ARM64 (Apple Silicon)

See [Building](#building) for instructions on building from source.

## Configuration

The task runner is configured through a hierarchical system (later values override earlier ones):

1. **Default values** - Built-in defaults
2. **Configuration file** (`~/.hasheous-taskrunner/config.json`)
3. **Environment variables**
4. **Command-line arguments**

### Configuration Options

| Option | Default | Required | Description |
|--------|---------|----------|-------------|
| `HostAddress` | `https://hasheous.org/` | No | URL of the central task server |
| `APIKey` | (empty) | **Yes** | Authentication key for the server - can be retrieved from https://hasheous.org/index.html?page=account |
| `ClientName` | System hostname | No | Name to register with the server |
| `ollama_url` | (empty) | No | URL of Ollama service for AI tasks |
| `EnableAutoUpdate` | `true` | No | Enable automatic update checking and installation from GitHub releases |

**Security Note**: Sensitive configuration values (`APIKey`, `ollama_url`) are automatically redacted in help text and logs. When loading from environment variables, the task runner will warn you that these values are visible to other processes and recommend using the configuration file instead.

## Usage

### Basic Usage

```bash
./hasheous-taskrunner --APIKey your-api-key-here
```

### With All Options

```bash
./hasheous-taskrunner \
  --APIKey your-api-key-here \
  --HostAddress https://taskserver.example.com \
  --ClientName my-worker-1 \
  --ollama_url http://localhost:11434
```

### Using Environment Variables

```bash
export APIKey="your-api-key-here"
export HostAddress="https://taskserver.example.com"
export ClientName="my-worker-1"
export ollama_url="http://localhost:11434"

./hasheous-taskrunner
```

### Using Configuration File

Create `~/.hasheous-taskrunner/config.json`:

```json
{
  "APIKey": "your-api-key-here",
  "HostAddress": "https://taskserver.example.com",
  "ClientName": "my-worker-1",
  "ollama_url": "http://localhost:11434"
}
```

Then run:

```bash
./hasheous-taskrunner
```

### Help

```bash
./hasheous-taskrunner --help
```

## Windows Service Installation (Windows Only)

On Windows systems, the task runner can be installed as a Windows service for automatic startup and management.

### Installing as a Service

Run the following command **as Administrator**:

```bash
hasheous-taskrunner install
```

This will:
- Create a Windows service named "HasheousTaskRunner"
- Set it to automatically start on system boot
- Use the current executable path

### Removing the Service

Run the following command **as Administrator**:

```bash
hasheous-taskrunner remove
```

This will uninstall the Windows service.

### Managing the Service

Once installed, you can manage the service using:

```bash
# Start the service
net start HasheousTaskRunner

# Stop the service
net stop HasheousTaskRunner

# View service status
sc query HasheousTaskRunner
```

Or through the Windows Services management console (`services.msc`).

## Automatic Updates

The task runner includes an automatic update mechanism that:

1. **Checks at Startup**: Immediately checks for updates when the application starts
2. **Background Checks**: Checks daily (every 24 hours) during operation
3. **Smart Updates**: Only downloads updates when a newer stable release is available
4. **Platform-Aware**: Automatically selects the correct executable for your platform and architecture
5. **Secure Downloads**: Verifies SHA256 checksums when available to ensure integrity
6. **Safe Updates**: Creates a backup of the current executable before updating
7. **Atomic Operations**: Uses atomic file operations to prevent corruption
8. **Auto-Restart**: Automatically restarts the application with the new version
9. **Rollback Support**: Automatically rolls back to backup if update fails

### Security

All GitHub releases include:
- **SHA256 Checksums** (`.sha256` files) - Automatically verified during updates
- **Build Provenance Attestations** - Cryptographically signed build records
- **Software Bill of Materials (SBOM)** - SPDX-format dependency transparency

The updater will:
- ✅ Verify checksums before applying updates (when available)
- ✅ Abort updates if checksums don't match
- ⚠️ Warn if checksum files are missing
- ✅ Create backups before modifying files
- ✅ Automatically rollback on failure

### Disabling Auto-Updates

To disable automatic updates, use any of these methods:

**Command-line argument:**
```bash
./hasheous-taskrunner --EnableAutoUpdate false
```

**Environment variable:**
```bash
export EnableAutoUpdate=false
./hasheous-taskrunner
```

**Configuration file** (`~/.hasheous-taskrunner/config.json`):
```json
{
  "EnableAutoUpdate": "false"
}
```

By default, auto-updates are **enabled**.

## Reliability and Error Handling

The task runner includes comprehensive error handling and resilience features:

### Component Isolation
- Each component (registration, heartbeat, updates, tasks) has dedicated error handling
- Component failures are isolated and logged without crashing the service
- Service continues operating even when individual components fail

### Smart Retry Logic
- **Exponential Backoff**: Retry delays increase exponentially (1s → 2s → 4s → 8s → 16s → 32s → max 60s)
- **Jitter**: Random 0-1000ms added to prevent thundering herd
- **Max Retries**: Limited to 10 attempts before giving up
- **Graceful Degradation**: Service remains operational during transient failures

### Network Resilience
- **HTTP Timeouts**: All HTTP requests timeout after 30 seconds (WebClient) or 120 seconds (Updater)
- **Automatic Reconnection**: Re-registration and heartbeat recovery
- **Connection Monitoring**: Tracks connectivity and adjusts behavior

### Error Logging
All errors are logged with standardized prefixes:
- `[ERROR]` - Component failures and unexpected errors
- `[WARNING]` - Non-critical issues and security warnings
- `[INFO]` - Normal operational messages
- `[FATAL]` - Unrecoverable errors requiring shutdown

### Global Safety Net
- **Unhandled Exception Handler**: Catches any exceptions that escape component handlers
- **Emergency Cleanup**: Attempts to unregister from server even during fatal errors
- **Detailed Diagnostics**: Full stack traces and exception details logged

## Operation

Once started, the task runner:

1. **Checks for Updates** immediately on startup and daily during operation
2. **Registers** with the central server and declares its capabilities
3. **Sends Heartbeats** every 30 seconds to maintain connection
4. **Polls for Tasks** at regular intervals
5. **Executes Tasks** as they arrive (currently supports AI description/tagging)
6. **Reports Results** back to the server
7. **Auto-reregisters** periodically to ensure server knows it's alive

### Graceful Shutdown

Press `Ctrl+C` to gracefully shut down. The runner will:
- Stop accepting new tasks
- Clean up any running processes
- Unregister from the server

## Capabilities

The task runner declares the following capabilities to the server:

| Capability | Status | Description |
|-----------|--------|-------------|
| **Internet** | Always active | Network connectivity for HTTP requests |
| **Disk Space** | Dynamic | Available free disk space monitoring |
| **AI** | Conditional | AI task processing (requires Ollama URL configured) |

## Supported Tasks

### AI Description and Tagging

Generates AI-powered descriptions and tags for content using Ollama.

**Parameters:**
- `input`: Content to describe and tag
- Additional parameters as required by the configured Ollama model

**Response:**
- `description`: Generated description
- `tags`: Generated tags (comma-separated or list)

## Docker Deployment

The project includes a Docker configuration for containerized deployment:

```bash
cd hasheous-taskrunner/Docker
docker-compose up
```

See [Docker/Dockerfile](hasheous-taskrunner/Docker/Dockerfile) for details.

## Building

### From Source

Build the solution:

```bash
dotnet build
```

### Publishing Self-Contained Executables

For a specific platform (example: Linux x64):

```bash
dotnet publish -c Release -r linux-x64 --self-contained
```

Output location: `bin/Release/net8.0/<runtime-id>/publish/hasheous-taskrunner`

Supported runtime identifiers:
- `linux-x64`, `linux-arm64`
- `win-x64`, `win-arm64`
- `osx-x64`, `osx-arm64`

Framework-dependent build (smaller, requires .NET 8.0):

```bash
dotnet publish -c Release --no-self-contained
```

See [BUILD.md](hasheous-taskrunner/BUILD.md) for detailed build information.

## Project Structure

```
hasheous-taskrunner/
├── Program.cs                  # Application entry point
├── Classes/
│   ├── Config.cs              # Configuration management
│   ├── WebClient.cs           # HTTP client for server communication
│   ├── Capabilities/          # Capability implementations
│   │   ├── ICapability.cs     # Capability interface
│   │   ├── DiskSpaceCapability.cs
│   │   ├── InternetCapability.cs
│   │   └── OllamaCapability.cs
│   ├── Communication/         # Server communication
│   │   ├── Registration.cs    # Worker registration
│   │   ├── Heartbeat.cs       # Heartbeat signals
│   │   ├── Tasks.cs           # Task fetching and execution
│   │   └── Common.cs          # Shared communication utilities
│   └── Tasks/                 # Task implementations
│       ├── ITask.cs           # Task interface
│       └── AITask.cs          # AI task implementation
└── Docker/                    # Container configuration
```

## Troubleshooting

### Task runner fails to start

Check that you've provided the required API key:
```bash
./hasheous-taskrunner --help
```

### Cannot connect to server

- Verify `HostAddress` is correct and reachable
- Check firewall rules allow outbound HTTPS connections
- Ensure `APIKey` is valid

### AI tasks not executing

- Verify `ollama_url` is configured and correct
- Ensure Ollama service is running and accessible
- Check that Ollama has the required model loaded

### On macOS

If the executable shows as untrusted:
```bash
xattr -d com.apple.quarantine ./hasheous-taskrunner
```

## Dependencies

- **Newtonsoft.Json** (v13.0.4) - JSON serialization/deserialization
- .NET 8.0 runtime (included in self-contained builds)

## License

See LICENSE file in the repository root.

## Contributing

For issues or contributions, please use the project's GitHub repository.
