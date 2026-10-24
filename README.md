# WDM (Windows Download Manager)
*Next-Generation Open-Source Parallel Download Manager for Windows & Web*

WDM is a modern, high-speed open-source alternative to Internet Download Manager (IDM), built on **.NET 10** with native HTTP/3 support, dynamic multi-connection chunking, and **zero-stitch direct concurrent disk writes**.

---

## 🚀 Key Advantages Over IDM

1. **Zero-Stitch Instant Disk Completion**:
   - IDM downloads chunks as separate temp files (`.part0`, `.part1`, etc.) and slowly stitches them at 100%, causing disk bottlenecks.
   - WDM pre-allocates the target file and uses .NET `RandomAccess` with Windows overlapped I/O to allow multiple worker threads to stream directly into their assigned byte offsets in parallel. **Zero merge time when the download hits 100%!**
2. **Modern Protocol Support**:
   - Full support for HTTP/3 (QUIC multiplexing), HTTP/2, and HTTP/1.1 with socket pooling.
3. **Resumable State Checkpoints**:
   - Saves chunk download states (`.wdm-state`) to survive network dropouts or machine reboots.
4. **Built-in Security Verification**:
   - High-throughput streaming cryptographic hash calculation (SHA-256, SHA-512, SHA-1, MD5).
5. **Media Stream Bridge**:
   - Architecture in place to sniff and extract adaptive streaming media (YouTube, HLS `.m3u8`, DASH `.mpd`).

---

## 📁 Project Architecture

- **`src/WDM.Core`**: The high-performance core engine class library.
  - `Network/`: `HttpProbe`, `HttpClientFactory` (HTTP/3 & socket pooling).
  - `Storage/`: `DiskManager` (RandomAccess zero-stitch I/O), `CheckpointStore` (pause/resume).
  - `Engine/`: `DownloadEngine`, `BandwidthThrottler`.
  - `Security/`: `ChecksumVerifier` (SHA-256 / MD5).
  - `Media/`: `MediaExtractor` (YouTube / HLS / DASH sniffer bridge).
- **`src/WDM.Cli`**: Feature-complete terminal client powered by Spectre.Console.
- **`tests/WDM.Core.Tests`**: Automated test suite for multi-threading, chunking, and hashing.

---

## 🛠️ Usage & Commands

Run using the .NET CLI:

### 1. Download with Parallel Chunking
```bash
dotnet run --project src/WDM.Cli/WDM.Cli.csproj -- download https://proof.ovh.net/files/100Mb.dat -c 16
```
Options:
- `-c <num>`: Number of parallel connections (default: `16`, up to `64`).
- `-o <dir>`: Target download directory.
- `-f <name>`: Custom file name.
- `-l <limit>`: Speed cap (e.g., `-l 5MB` or `-l 500KB`).

### 2. Inspect Server Capabilities & Range Support (Probe)
```bash
dotnet run --project src/WDM.Cli/WDM.Cli.csproj -- probe https://proof.ovh.net/files/1Mb.dat
```

### 3. Compute & Verify Checksums
```bash
dotnet run --project src/WDM.Cli/WDM.Cli.csproj -- hash path/to/file.zip sha256
```

### 4. Media Stream Extraction (YouTube, HLS, DASH)
```bash
dotnet run --project src/WDM.Cli/WDM.Cli.csproj -- media https://www.youtube.com/watch?v=...
```

### 5. Multi-Connection Benchmark
Compare 1 vs 4 vs 8 vs 16 connections side-by-side:
```bash
dotnet run --project src/WDM.Cli/WDM.Cli.csproj -- benchmark https://proof.ovh.net/files/10Mb.dat
```

---

## 📋 Roadmap Status

- [x] **Step 1: Core Engine & Initial Components (.NET 10)**
  - [x] Dynamic HTTP Range & Header Probe
  - [x] High-performance parallel chunk downloader
  - [x] Zero-stitch direct disk write engine (`RandomAccess`)
  - [x] Pause / resume checkpoint storage
  - [x] Bandwidth throttler (token bucket)
  - [x] Cryptographic hash verifier (SHA-256, MD5)
  - [x] Media extraction bridge interface
  - [x] Rich CLI runner with Spectre.Console
  - [x] Automated test suite (6/6 passing)
- [ ] **Step 2: Browser Extension (Pending)**
  - Manifest V3 WebExtension (Chrome/Edge/Firefox)
  - Native Messaging Host IPC
  - Floating download pill on media players
- [ ] **Step 3: Desktop UI (Pending)**
  - Modern Windows 11 Fluent / Mica interface
  - System tray mini-widget & toast notifications
