# SoloDB Manager

[English](#english) | [中文](#中文)

---

<a id="english"></a>
## English

A desktop GUI tool for managing **SoloDB** document databases and plain **SQLite** files.

- **Auto-detects SoloDB collections** (`Id` / `Value` JSONB / `Metadata` columns) and flattens JSON documents into a readable grid
- Browse / edit / delete documents (JSON editor for SoloDB, form editor for regular tables)
- Visualize table structure: columns, indexes, inferred document fields, SQLite pragmas
- Built-in SQL editor with `json_extract(Value, '$.field')` snippets
- **Bilingual** (Chinese/English), auto-detected from system locale
- **Local file browser**: open any .db file by path without uploading
- **Quick actions**: sort by column, first-N rows, clear filters
- Dark mode, responsive layout

### Download

Download the single-file Windows exe from the [Releases page](https://github.com/fxyc87/SoloDbManager/releases/latest):

`SoloDbManager.exe` (~70 MB, Windows 11 x64, self-contained).

### Usage

Double-click `SoloDbManager.exe`:
- A desktop window opens automatically (WebView2, bundled with Win11)
- The full SoloDB Manager UI appears inside the window
- Close the window to exit

#### Opening a database
- **Files tab**: lists files in the app's `databases/` folder; upload/delete supported
- **Browse tab**: navigate the local filesystem to open any .db file by path (no upload needed)
- **Recent tab**: previously opened databases
- **Path tab**: enter a path manually

> **WAL files**: When uploading, also include `.db-wal` / `.db-shm` sidecar files if present — the app merges them via checkpoint so no data is lost.

#### Language
Auto-detects system language (Chinese/English). Click the `中`/`EN` button in the top bar to switch manually; the choice is remembered.

### Build

Requirements: [.NET 10 SDK](https://dot.net)

```bash
dotnet publish solodb-csharp/SoloDbManager.csproj -c Release -r win-x64 -o dist-win
```

Produces `dist-win/SoloDbManager.exe` (single file, ~70 MB, includes .NET runtime + WebView2 interop + SQLite + frontend).

### What's in the single file

| Component | Notes |
|-----------|-------|
| .NET 10 runtime | self-contained, no .NET install needed on target |
| WebView2 interop + Loader.dll | embedded |
| SQLite `e_sqlite3.dll` | embedded as resource, extracted to `%TEMP%` at runtime |
| Frontend `index.html` | embedded as resource (with i18n) |

### Project structure

```
solodb-csharp/
  SoloDbManager.csproj     net10.0-windows + WinForms + WebView2 + single-file publish
  Program.cs               [STAThread] entry; server on background thread, WebView2 on UI thread
  WebServerHost.cs         ASP.NET Core routes (connections/files/upload/browse/query)
  SoloDbEngine.cs          SoloDB detection, JSON flattening, CRUD, SQL execution
  WebViewShell.cs          WinForms + WebView2 desktop window shell
  NativeLibLoader.cs       Embedded SQLite native lib extraction & loading
  AppLauncher.cs           Non-Windows fallback: open browser
  ConnectionsStore.cs      Saved connections store
  Models.cs                DTOs (JsonNode for dynamic values, AOT-safe)
  JsonContext.cs           System.Text.Json source generator
  TableKindConverter.cs    Enum → hyphenated string converter
  Extensions.cs            SqliteDataReader string-column-name extensions
  wwwroot/index.html       Single-file vanilla JS SPA frontend (with i18n)
  databases/               Sample databases
```

### Architecture

**Startup flow (Windows):**
1. `[STAThread] Main` — main thread stays STA (required by WebView2)
2. Background MTA thread initializes SQLite + builds ASP.NET Core + starts Kestrel
3. Main thread waits for server ready (max 15s)
4. Main thread opens WebView2 window loading `http://localhost:3000/`
5. Close window → stop server → exit

**STA threading**: WebView2 requires its initializing thread to be STA. ASP.NET Core sets threads to MTA. So an explicit `[STAThread] Main` is used — the main thread never touches ASP.NET Core / SQLite init; all of that runs on a background MTA thread.

**Single-file principle**:
- `PublishSingleFile` + `SelfContained` + `IncludeNativeLibrariesForSelfExtract` bundle all dependencies into one exe
- `EnableCompressionInSingleFile` compresses
- SQLite `e_sqlite3.dll` embedded as `EmbeddedResource`, extracted to `%TEMP%\solodb-manager-native\` at runtime
- Frontend `wwwroot/index.html` embedded as `EmbeddedResource`, served via `ManifestEmbeddedFileProvider`

### About SoloDB

SoloDB is an embedded document database for .NET that stores objects as JSONB inside SQLite. Each collection is a table with three columns: `Id` (INTEGER PK), `Value` (JSONB), `Metadata` (JSONB). This tool auto-detects that structure and flattens the JSON documents into a readable grid.

Docs: https://www.solodb.org/docs.html

---

<a id="中文"></a>
## 中文

一个管理 **SoloDB** 文档数据库和普通 **SQLite** 文件的桌面 GUI 工具。

- **自动识别 SoloDB 集合**（`Id` / `Value` JSONB / `Metadata` 三列表），把 JSON 文档铺平成可读表格
- 浏览 / 编辑 / 删除文档（SoloDB 用 JSON 编辑器，普通表用表单）
- 可视化表结构、索引、推断出的文档字段、SQLite pragma
- 内置 SQL 编辑器，支持 `json_extract(Value, '$.field')` 等 SQLite JSON 函数
- **中英文双语**，根据系统语言自动切换
- **本地文件浏览**：直接打开任意路径的数据库，无需上传
- **快捷操作**：按列排序、只看前 N 行、一键清除筛选
- 深色模式、响应式布局

### 下载

从 [Releases 页面](https://github.com/fxyc87/SoloDbManager/releases/latest) 下载单文件 Windows exe：

`SoloDbManager.exe`（约 70 MB，Windows 11 x64，自包含）。

### 使用

双击 `SoloDbManager.exe`：
- 自动弹出桌面窗口（WebView2，Win11 自带运行时）
- 窗口内即完整 SoloDB Manager 界面
- 关闭窗口即退出程序

#### 打开数据库
- **Files 标签页**：列出 `databases/` 目录下的文件，可上传/删除
- **Browse 标签页**：浏览本地文件系统，直接打开任意路径 .db 文件（无需上传）
- **Recent 标签页**：最近打开过的数据库
- **Path 标签页**：手动输入路径

> **WAL 文件**：上传时如果有 `.db-wal` / `.db-shm` 旁车文件请一并上传——工具会通过 checkpoint 合并数据，避免丢失。

#### 多语言
自动检测系统语言（中文/英文）。点右上角 `中`/`EN` 按钮手动切换，记忆选择。

### 构建

环境要求：[.NET 10 SDK](https://dot.net)

```bash
dotnet publish solodb-csharp/SoloDbManager.csproj -c Release -r win-x64 -o dist-win
```

产出 `dist-win/SoloDbManager.exe`（单文件，约 70 MB，含 .NET 运行时 + WebView2 互操作库 + SQLite + 前端资源）。

### 单文件包含什么

| 组件 | 说明 |
|------|------|
| .NET 10 运行时 | self-contained，Win11 无需装 .NET |
| WebView2 互操作库 + Loader.dll | 嵌入，运行时加载 |
| SQLite `e_sqlite3.dll` | 嵌入为资源，运行时释放到 `%TEMP%` |
| 前端 `index.html` | 嵌入为资源（含中英文双语） |

### 项目结构

```
solodb-csharp/
  SoloDbManager.csproj     net10.0-windows + WinForms + WebView2 + 单文件发布
  Program.cs               [STAThread] 入口，主线程跑 WebView2，后台线程跑服务器
  WebServerHost.cs         ASP.NET Core 路由（连接/文件/上传/删除/浏览/查询等）
  SoloDbEngine.cs          SoloDB 检测、JSON 铺平、CRUD、SQL 执行
  WebViewShell.cs          WinForms + WebView2 桌面窗口壳
  NativeLibLoader.cs       嵌入式 SQLite 原生库释放与加载
  AppLauncher.cs           非 Windows 回退：开浏览器
  ConnectionsStore.cs      保存的连接列表
  Models.cs                DTO（JsonNode 存动态值，AOT 安全）
  JsonContext.cs           System.Text.Json 源生成器
  TableKindConverter.cs    枚举→连字符字符串转换器
  Extensions.cs            SqliteDataReader 字符串列名扩展
  wwwroot/index.html       单文件 vanilla JS SPA 前端（含 i18n）
  databases/               示例数据库
```

### 技术架构

**启动流程（Windows）：**
1. `[STAThread] Main` — 主线程保持 STA（WebView2 要求）
2. 后台 MTA 线程初始化 SQLite + 构建 ASP.NET Core + 启动 Kestrel 监听
3. 主线程等服务器就绪（最多 15 秒）
4. 主线程打开 WebView2 窗口加载 `http://localhost:3000/`
5. 关窗 → 停服务器 → 退出

**STA 线程模式**：WebView2 要求初始化它的线程是 STA（单线程单元）。ASP.NET Core 会把线程设成 MTA。因此必须用显式 `[STAThread] Main`，主线程绝对不碰 ASP.NET Core / SQLite 初始化——全部在后台 MTA 线程做。

**单文件原理**：
- `PublishSingleFile` + `SelfContained` + `IncludeNativeLibrariesForSelfExtract` 把所有依赖打进一个 exe
- `EnableCompressionInSingleFile` 压缩
- SQLite `e_sqlite3.dll` 作为 `EmbeddedResource`，运行时释放到 `%TEMP%\solodb-manager-native\`
- 前端 `wwwroot/index.html` 作为 `EmbeddedResource`，通过 `ManifestEmbeddedFileProvider` 服务

### 关于 SoloDB

SoloDB 是 .NET 嵌入式文档数据库，把对象以 JSONB 存进 SQLite。每个集合是一张表，固定三列：`Id`（INTEGER PK）、`Value`（JSONB）、`Metadata`（JSONB）。本工具自动识别这种结构并铺平 JSON 文档为可读表格。

文档：https://www.solodb.org/docs.html
