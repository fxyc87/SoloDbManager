using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using SoloDbManager;

namespace SoloDbManager;

/// <summary>
/// Builds and runs the ASP.NET Core web server. Kept separate from Program.cs
/// so the server can be started on a background thread, leaving the main
/// thread free for the WebView2 message loop (which requires STA).
/// </summary>
public static class WebServerHost
{
    /// <summary>
    /// Builds the WebApplication (configures all routes) and returns it
    /// without starting it. The caller starts/stops it via RunAsync/StopAsync.
    /// </summary>
    public static WebApplication Build(string hostname, string port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://{hostname}:{port}");
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            o.SerializerOptions.TypeInfoResolver = AppJsonContext.Default;
        });
        builder.Services.AddHttpContextAccessor();

        var app = builder.Build();

        var embeddedProvider = new ManifestEmbeddedFileProvider(
            typeof(WebServerHost).Assembly, "wwwroot");
        app.UseStaticFiles(new StaticFileOptions { FileProvider = embeddedProvider });

        RegisterRoutes(app, embeddedProvider);
        return app;
    }

    private static SqliteConnection? OpenFromQuery(HttpContext ctx, out string? error, out string? absPath)
    {
        error = null;
        absPath = null;
        var rawPath = ctx.Request.Query["db"].FirstOrDefault();
        if (string.IsNullOrEmpty(rawPath))
        {
            error = "db query param is required";
            return null;
        }
        try
        {
            absPath = SoloDbEngine.ResolveDbPath(rawPath);
            return SoloDbEngine.GetDatabase(absPath);
        }
        catch (Exception e)
        {
            error = e.Message;
            return null;
        }
    }

    private static Results<Ok<object>, JsonHttpResult<ErrorResponse>> Err(string msg, int status = 400)
    {
        var body = new ErrorResponse { Error = msg };
        return TypedResults.Json(body, AppJsonContext.Default.ErrorResponse, statusCode: status);
    }

    private static void RegisterRoutes(WebApplication app, ManifestEmbeddedFileProvider embeddedProvider)
    {
        // ===================== /api/connections =====================
        app.MapGet("/api/connections", () =>
        {
            var rows = ConnectionsStore.List();
            var conns = rows.Select(r => new Connection
            {
                Id = r.Id,
                Name = r.Name,
                Path = r.Path,
                IsSolodb = r.IsSolodb == 1,
                LastOpenedAt = r.LastOpenedAt,
                CreatedAt = r.CreatedAt,
            }).ToList();
            return Results.Ok(new ConnectionsResponse { Connections = conns });
        });

        app.MapPost("/api/connections", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.ConnectionRequest);
            if (body is null || string.IsNullOrEmpty(body.Path))
                return Err("path is required");

            try
            {
                var abs = SoloDbEngine.ResolveDbPath(body.Path);
                if (!File.Exists(abs)) Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
                var conn = SoloDbEngine.GetDatabase(abs);
                var (isSolodb, _) = SoloDbEngine.IsSolodbDatabase(conn);
                var name = string.IsNullOrEmpty(body.Name) ? Path.GetFileName(abs) : body.Name;
                var stored = ConnectionsStore.Upsert(abs, name, isSolodb);
                return Results.Ok(new ConnectionResponse
                {
                    Connection = new Connection
                    {
                        Id = stored.Id,
                        Name = stored.Name,
                        Path = stored.Path,
                        IsSolodb = stored.IsSolodb == 1,
                        LastOpenedAt = stored.LastOpenedAt,
                        CreatedAt = stored.CreatedAt,
                    }
                });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapDelete("/api/connections/{id}", (string id) =>
        {
            var all = ConnectionsStore.List();
            var c = all.FirstOrDefault(x => x.Id == id);
            if (c == null) return Err("Connection not found", 404);
            try { SoloDbEngine.CloseDatabase(c.Path); } catch { }
            ConnectionsStore.Remove(id);
            return Results.Ok(new OkResponse { Ok = true });
        });

        // ===================== databases dir helpers =====================
        // (defined locally so the files endpoints can use them)
        app.MapGet("/api/database/files", () =>
        {
            var dir = GetDatabasesDir();
            var files = new List<FileEntry>();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.EnumerateFiles(dir).Where(p =>
                        Regex.IsMatch(Path.GetFileName(p), @"\.(db|sqlite|sqlite3)$", RegexOptions.IgnoreCase)))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            files.Add(new FileEntry
                            {
                                Name = Path.GetFileName(f),
                                Path = f,
                                SizeBytes = fi.Length,
                                Modified = fi.LastWriteTimeUtc.ToString("o"),
                            });
                        } catch { }
                    }
                }
            } catch { }
            files = files.OrderByDescending(f => f.Modified).ToList();
            return Results.Ok(new FilesResponse { Files = files, Dir = dir });
        });

        app.MapPost("/api/database/files/upload", async (HttpContext ctx) =>
        {
            var dir = GetDatabasesDir();
            var form = await ctx.Request.ReadFormAsync();
            var uploaded = new List<FileEntry>();

            // Accept .db / .sqlite / .sqlite3 main files, AND their sidecar
            // WAL (-db-wal) / SHM (-db-shm) files. SQLite stores uncommitted
            // writes in the WAL file; if a user uploads only the .db without
            // its -wal companion, those writes are lost. We accept sidecars so
            // a user can drag all three files in and lose nothing.
            bool IsDbFile(string n) => Regex.IsMatch(n, @"\.(db|sqlite|sqlite3)$", RegexOptions.IgnoreCase);
            bool IsSidecar(string n) => Regex.IsMatch(n, @"\.(db|sqlite|sqlite3)-(wal|shm|journal)$", RegexOptions.IgnoreCase);

            // First pass: save everything (main files + sidecars).
            var savedMainFiles = new List<string>();
            foreach (var file in form.Files)
            {
                var fname = Path.GetFileName(file.FileName.Replace("\\", "/"));
                if (string.IsNullOrWhiteSpace(fname) || fname.StartsWith(".")) continue;
                if (fname.Contains("..")) continue;
                // Normalize sidecar extension if missing the .db prefix pattern
                if (!IsDbFile(fname) && !IsSidecar(fname))
                {
                    // Not a recognized db/sidecar name — append .db
                    fname += ".db";
                }
                var dest = Path.Combine(dir, fname);
                try { SoloDbEngine.CloseDatabase(dest); } catch { }
                await using (var input = file.OpenReadStream())
                await using (var output = File.Create(dest))
                {
                    await input.CopyToAsync(output);
                }
                if (IsDbFile(fname)) savedMainFiles.Add(dest);
            }

            // Second pass: for each saved .db main file, open it (which merges
            // any sidecar WAL into the main file via checkpoint) so the
            // committed data is fully in the .db and not stranded in a -wal
            // that might later be deleted. Then close to release locks.
            foreach (var dbPath in savedMainFiles)
            {
                try
                {
                    SoloDbEngine.CloseDatabase(dbPath);
                    // Open read-write, force a full checkpoint, then close.
                    var conn = SoloDbEngine.GetDatabase(dbPath);
                    try
                    {
                        using var c = conn.CreateCommand();
                        // TRUNCATE checkpoint writes all WAL frames into the db
                        // file and truncates the WAL to zero.
                        c.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                        c.ExecuteNonQuery();
                    }
                    finally { SoloDbEngine.CloseDatabase(dbPath); }
                }
                catch { /* checkpoint failure is non-fatal */ }
                var fi = new FileInfo(dbPath);
                uploaded.Add(new FileEntry
                {
                    Name = fi.Name,
                    Path = fi.FullName,
                    SizeBytes = fi.Length,
                    Modified = fi.LastWriteTimeUtc.ToString("o"),
                });
            }
            if (uploaded.Count == 0)
                return Err("No valid database files uploaded (must end with .db/.sqlite/.sqlite3).");
            return Results.Ok(new FilesResponse { Files = uploaded, Dir = dir });
        });

        app.MapDelete("/api/database/files/{name}", (string name) =>
        {
            var dir = GetDatabasesDir();
            var safe = SanitizeDbFileName(name);
            if (string.IsNullOrEmpty(safe))
                return Err("Invalid file name.", 400);
            var target = Path.Combine(dir, safe);
            var resolved = Path.GetFullPath(target);
            var expected = Path.GetFullPath(dir);
            if (!resolved.StartsWith(expected + Path.DirectorySeparatorChar))
                return Err("Invalid file path.", 400);
            if (!File.Exists(resolved))
                return Err("File not found.", 404);
            try { SoloDbEngine.CloseDatabase(resolved); } catch { }
            try
            {
                foreach (var c in ConnectionsStore.List())
                    if (c.Path == resolved)
                        ConnectionsStore.Remove(c.Id);
            } catch { }
            try { File.Delete(resolved); }
            catch (Exception e) { return Err("Could not delete file: " + e.Message, 500); }
            return Results.Ok(new OkResponse { Ok = true });
        });

        // ===================== /api/database/browse =====================
        // Browse the local filesystem to let users open databases by path
        // without uploading them. Returns folders + .db/.sqlite/.sqlite3 files.
        app.MapGet("/api/database/browse", (HttpContext ctx) =>
        {
            var rawDir = ctx.Request.Query["dir"].FirstOrDefault();
            string dir;
            if (string.IsNullOrEmpty(rawDir) || rawDir == "~")
            {
                dir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            else
            {
                dir = rawDir;
                if (dir.StartsWith("~"))
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), dir[1..]);
                dir = Path.GetFullPath(dir);
            }

            var resp = new BrowseResponse { Current = dir };
            try
            {
                var di = new DirectoryInfo(dir);
                resp.Parent = di.Parent?.FullName;
                var entries = new List<BrowseEntry>();
                foreach (var d in di.EnumerateDirectories())
                {
                    entries.Add(new BrowseEntry
                    {
                        Name = d.Name,
                        Path = d.FullName,
                        IsDir = true,
                    });
                }
                foreach (var f in di.EnumerateFiles())
                {
                    if (!Regex.IsMatch(f.Name, @"\.(db|sqlite|sqlite3)$", RegexOptions.IgnoreCase))
                        continue;
                    entries.Add(new BrowseEntry
                    {
                        Name = f.Name,
                        Path = f.FullName,
                        IsDir = false,
                        Size = f.Length,
                    });
                }
                resp.Entries = entries;
            }
            catch (UnauthorizedAccessException)
            {
                resp.Error = "Access denied to this folder.";
            }
            catch (DirectoryNotFoundException)
            {
                resp.Error = "Folder not found.";
            }
            catch (Exception e)
            {
                resp.Error = e.Message;
            }
            return Results.Ok(resp);
        });

        // ===================== /api/database/open =====================
        app.MapPost("/api/database/open", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.OpenRequest);
            if (body is null || string.IsNullOrEmpty(body.Path))
                return Err("path is required");
            try
            {
                var abs = SoloDbEngine.ResolveDbPath(body.Path);
                bool exists = File.Exists(abs);
                if (!exists && body.Create != true)
                    return Err($"Database file not found: {abs}", 404);
                if (!exists)
                {
                    try { Directory.CreateDirectory(Path.GetDirectoryName(abs)!); } catch { }
                }
                var conn = SoloDbEngine.GetDatabase(abs);
                var info = SoloDbEngine.GetDatabaseInfo(conn, abs);
                return Results.Ok(new InfoResponse { Info = info });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapGet("/api/database/info", (HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out var abs);
            if (conn == null) return Err(error!, 400);
            try { return Results.Ok(new InfoResponse { Info = SoloDbEngine.GetDatabaseInfo(conn, abs!) }); }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapGet("/api/database/tables", (HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out _);
            if (conn == null) return Err(error!, 400);
            try { return Results.Ok(new TablesResponse { Tables = SoloDbEngine.ListTables(conn) }); }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapPost("/api/database/tables", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.CreateTableRequest);
            if (body is null || string.IsNullOrEmpty(body.Db) || string.IsNullOrEmpty(body.Name))
                return Err("db and name are required");
            try
            {
                var abs = SoloDbEngine.ResolveDbPath(body.Db);
                var conn = SoloDbEngine.GetDatabase(abs);
                if (body.Kind == TableKind.SolodbCollection)
                    SoloDbEngine.CreateSolodbCollection(conn, body.Name);
                else
                {
                    if (body.Columns is null || body.Columns.Count == 0)
                        return Err("columns are required for a regular table");
                    SoloDbEngine.CreateRegularTable(conn, body.Name, body.Columns);
                }
                return Results.Ok(new OkResponse { Ok = true, Name = body.Name });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapDelete("/api/database/tables/{table}", (string table, HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out _);
            if (conn == null) return Err(error!, 400);
            try { SoloDbEngine.DropTable(conn, Uri.UnescapeDataString(table)); return Results.Ok(new OkResponse { Ok = true }); }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapGet("/api/database/tables/{table}/schema", (string table, HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out _);
            if (conn == null) return Err(error!, 400);
            try { return Results.Ok(new SchemaResponse { Schema = SoloDbEngine.GetTableSchema(conn, Uri.UnescapeDataString(table)) }); }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapGet("/api/database/tables/{table}/data", (string table, HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out _);
            if (conn == null) return Err(error!, 400);
            try
            {
                var sp = ctx.Request.Query;
                int page = int.TryParse(sp["page"], out var pg) ? pg : 1;
                int pageSize = int.TryParse(sp["pageSize"], out var ps) ? ps : 50;
                var sort = sp["sort"].FirstOrDefault();
                var order = sp["order"].FirstOrDefault() == "desc" ? "desc" : "asc";
                var search = sp["search"].FirstOrDefault();
                var data = SoloDbEngine.GetTableData(conn, Uri.UnescapeDataString(table), page, pageSize, sort, order, search);
                return Results.Ok(new DataResponse { Data = data });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapPost("/api/database/tables/{table}/data", async (string table, HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out _);
            if (conn == null) return Err(error!, 400);
            var body = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.InsertRequest);
            if (body is null) return Err("invalid body");
            try
            {
                var t = Uri.UnescapeDataString(table);
                var kind = SoloDbEngine.DetectTableKind(conn, t);
                if (kind == TableKind.SolodbCollection)
                {
                    var id = SoloDbEngine.InsertSolodbDocument(conn, t, body.Value ?? new JsonObject(), body.Metadata);
                    return Results.Ok(new OkResponse { Ok = true, Id = id });
                }
                var (rowid, _) = SoloDbEngine.InsertRegularRow(conn, t, body.Values ?? new());
                return Results.Ok(new OkResponse { Ok = true, Rowid = rowid });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapPatch("/api/database/tables/{table}/data", async (string table, HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out _);
            if (conn == null) return Err(error!, 400);
            var body = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.UpdateRequest);
            if (body is null || body.Identifier is null) return Err("identifier is required");
            try
            {
                var t = Uri.UnescapeDataString(table);
                var kind = SoloDbEngine.DetectTableKind(conn, t);
                if (kind == TableKind.SolodbCollection)
                {
                    if (!body.Identifier.TryGetValue("Id", out var id) || id is null)
                        return Err("identifier.Id is required");
                    var changes = SoloDbEngine.UpdateSolodbDocument(conn, t, id!, body.Value ?? new JsonObject(), body.Metadata);
                    return Results.Ok(new OkResponse { Ok = true, Changes = changes });
                }
                if (body.Values is null) return Err("values are required");
                var changes2 = SoloDbEngine.UpdateRegularRow(conn, t, body.Identifier, body.Values);
                return Results.Ok(new OkResponse { Ok = true, Changes = changes2 });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapDelete("/api/database/tables/{table}/data", async (string table, HttpContext ctx) =>
        {
            var conn = OpenFromQuery(ctx, out var error, out _);
            if (conn == null) return Err(error!, 400);
            var body = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.DeleteRequest);
            if (body is null || body.Identifier is null) return Err("identifier is required");
            try
            {
                var t = Uri.UnescapeDataString(table);
                var kind = SoloDbEngine.DetectTableKind(conn, t);
                if (kind == TableKind.SolodbCollection)
                {
                    if (!body.Identifier.TryGetValue("Id", out var id) || id is null)
                        return Err("identifier.Id is required");
                    var changes = SoloDbEngine.DeleteSolodbDocument(conn, t, id!);
                    return Results.Ok(new OkResponse { Ok = true, Changes = changes });
                }
                var changes2 = SoloDbEngine.DeleteRegularRow(conn, t, body.Identifier);
                return Results.Ok(new OkResponse { Ok = true, Changes = changes2 });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        app.MapPost("/api/database/query", async (HttpContext ctx) =>
        {
            var body = await ctx.Request.ReadFromJsonAsync(AppJsonContext.Default.QueryRequest);
            if (body is null || string.IsNullOrEmpty(body.Db) || string.IsNullOrEmpty(body.Sql))
                return Err("db and sql are required");
            try
            {
                var abs = SoloDbEngine.ResolveDbPath(body.Db);
                var conn = SoloDbEngine.GetDatabase(abs);
                var result = SoloDbEngine.ExecuteQuery(conn, body.Sql);
                return Results.Ok(new QueryResponse { Result = result });
            }
            catch (Exception e) { return Err(e.Message, 500); }
        });

        // Fallback to index.html for SPA routes (serve from embedded resource).
        app.MapFallback(async ctx =>
        {
            var file = embeddedProvider.GetFileInfo("index.html");
            if (file.Exists)
            {
                ctx.Response.ContentType = "text/html";
                await using var stream = file.CreateReadStream();
                await stream.CopyToAsync(ctx.Response.Body);
                return;
            }
            ctx.Response.StatusCode = 404;
        });
    }

    // ---- shared helpers for the files endpoints ----
    private static string GetDatabasesDir()
    {
        var exeDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(exeDir, "databases"),
            Path.Combine(Directory.GetCurrentDirectory(), "databases"),
        };
        var dir = candidates.FirstOrDefault(Directory.Exists) ?? candidates[0];
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    private static string SanitizeDbFileName(string raw)
    {
        var name = Path.GetFileName(raw.Replace("\\", "/"));
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".")) return "";
        if (name.Contains("..")) return "";
        if (!Regex.IsMatch(name, @"\.(db|sqlite|sqlite3)$", RegexOptions.IgnoreCase))
            name += ".db";
        return name;
    }
}
