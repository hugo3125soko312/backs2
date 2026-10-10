// exec_bridge.go
//
// Local HTTP daemon for the BDS exec-bridge userscript.
package main

import (
	"context"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"time"
)

const (
    version          = "0.5.0"
    defaultTimeoutMs = 30_000
    maxTimeoutMs     = 300_000
    connTTL          = 30 * time.Second
)

var allowedOrigins = map[string]bool{
    "https://chat.deepseek.com": true,
    "https://www.deepseek.com":  true,
    "https://deepseek.com":      true,
}

func originAllowed(origin string) bool {
    if origin == "" { return true }
    if allowedOrigins[origin] { return true }
    if strings.HasPrefix(origin, "chrome-extension://") || strings.HasPrefix(origin, "moz-extension://") {
        return true
    }
    return false
}

// ---- dedup DB ----

type execDB struct {
    mu   sync.Mutex
    path string
    data map[string]map[string]bool
}

func newExecDB(path string) (*execDB, error) {
    d := &execDB{path: path, data: make(map[string]map[string]bool)}
    if err := d.load(); err != nil { return nil, err }
    return d, nil
}
func (d *execDB) load() error {
    b, err := os.ReadFile(d.path)
    if err != nil {
        if os.IsNotExist(err) { return nil }
        return err
    }
    return json.Unmarshal(b, &d.data)
}
func (d *execDB) saveLocked() error {
    b, err := json.MarshalIndent(d.data, "", "  ")
    if err != nil { return err }
    tmp := d.path + ".tmp"
    if err := os.WriteFile(tmp, b, 0o644); err != nil { return err }
    return os.Rename(tmp, d.path)
}
func (d *execDB) Claim(url, id string) (bool, error) {
    if url == "" || id == "" { return true, nil }
    d.mu.Lock(); defer d.mu.Unlock()
    if d.data[url] == nil { d.data[url] = make(map[string]bool) }
    if d.data[url][id] { return false, nil }
    d.data[url][id] = true
    if err := d.saveLocked(); err != nil { return false, err }
    return true, nil
}
func (d *execDB) List(url string) []string {
    if url == "" { return nil }
    d.mu.Lock(); defer d.mu.Unlock()
    ids := d.data[url]
    out := make([]string, 0, len(ids))
    for id := range ids { out = append(out, id) }
    return out
}

// ---- connection arbiter ----

type connState struct {
    mu       sync.Mutex
    holder   string
    lastSeen time.Time
}

func (c *connState) acquire(id string) (bool, string) {
    if id == "" { return false, "" }
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder == "" || c.holder == id || time.Since(c.lastSeen) > connTTL {
        prev := c.holder
        c.holder = id
        c.lastSeen = time.Now()
        return true, prev
    }
    return false, c.holder
}
func (c *connState) release(id string) bool {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder == id {
        c.holder = ""
        return true
    }
    return false
}
func (c *connState) heartbeat(id string) bool {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder == id {
        c.lastSeen = time.Now()
        return true
    }
    return false
}
func (c *connState) current() string {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder != "" && time.Since(c.lastSeen) > connTTL {
        return ""
    }
    return c.holder
}

// ---- request/response types ----

type initRequest struct { URL string `json:"url"` }
type initResponse struct {
    IDs       []string `json:"ids"`
    Connected string   `json:"connected"`
}

type connRequest struct { ClientID string `json:"client_id"` }
type connResponse struct {
    Ok      bool   `json:"ok"`
    Holder  string `json:"holder,omitempty"`
    Reason  string `json:"reason,omitempty"`
}

type execRequest struct {
    Command   string `json:"command"`
    Cwd       string `json:"cwd,omitempty"`
    TimeoutMs int    `json:"timeout_ms,omitempty"`
    URL       string `json:"url,omitempty"`
    ID        string `json:"id,omitempty"`
}
type execResponse struct {
    Stdout          string `json:"stdout"`
    Stderr          string `json:"stderr"`
    ExitCode        int    `json:"exit_code"`
    DurationMs      int64  `json:"duration_ms"`
    TimedOut        bool   `json:"timed_out"`
    Error           string `json:"error,omitempty"`
    AlreadyExecuted bool   `json:"already_executed,omitempty"`
}

type logRequest struct {
    Level   string `json:"level"`
    Message string `json:"message"`
}

// ---- helpers ----

func shellCommand(cmd string) (string, []string) {
    if runtime.GOOS == "windows" {
        if _, err := exec.LookPath("pwsh.exe"); err == nil {
            return "pwsh.exe", []string{"-NoProfile", "-NonInteractive", "-Command", cmd}
        }
        return "powershell.exe", []string{"-NoProfile", "-NonInteractive", "-Command", cmd}
    }
    return "/bin/sh", []string{"-c", cmd}
}

func cors(next http.HandlerFunc) http.HandlerFunc {
    return func(w http.ResponseWriter, r *http.Request) {
        w.Header().Set("Access-Control-Allow-Origin", "*")
        w.Header().Set("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        w.Header().Set("Access-Control-Allow-Headers", "Content-Type")
        if r.Method == http.MethodOptions { w.WriteHeader(http.StatusNoContent); return }
        next(w, r)
    }
}

func truncate(s string, n int) string {
    if len(s) <= n { return s }
    return s[:n] + "..."
}

func writeJSON(w http.ResponseWriter, v any) {
    w.Header().Set("Content-Type", "application/json")
    _ = json.NewEncoder(w).Encode(v)
}

// ---- handlers ----

func handleInit(w http.ResponseWriter, r *http.Request, db *execDB, conn *connState) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req initRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    ids := db.List(req.URL)
    if ids == nil { ids = []string{} }
    log.Printf("init url=%q known_ids=%d connected=%q", truncate(req.URL, 80), len(ids), conn.current())
    writeJSON(w, initResponse{IDs: ids, Connected: conn.current()})
}

func handleConnect(w http.ResponseWriter, r *http.Request, conn *connState) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 4*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req connRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    ok, holder := conn.acquire(req.ClientID)
    if !ok {
        log.Printf("connect DENIED client=%q current_holder=%q", req.ClientID, holder)
        w.WriteHeader(http.StatusConflict)
        writeJSON(w, connResponse{Ok: false, Holder: holder, Reason: "another tab is connected"})
        return
    }
    log.Printf("connect OK client=%q (prev=%q)", req.ClientID, holder)
    writeJSON(w, connResponse{Ok: true, Holder: req.ClientID})
}

func handleDisconnect(w http.ResponseWriter, r *http.Request, conn *connState) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 4*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req connRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    ok := conn.release(req.ClientID)
    log.Printf("disconnect client=%q released=%v", req.ClientID, ok)
    writeJSON(w, connResponse{Ok: ok})
}

func handleHeartbeat(w http.ResponseWriter, r *http.Request, conn *connState) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 4*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req connRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    ok := conn.heartbeat(req.ClientID)
    if !ok {
        w.WriteHeader(http.StatusConflict)
        writeJSON(w, connResponse{Ok: false, Reason: "not the holder"})
        return
    }
    w.WriteHeader(http.StatusNoContent)
}

func handleConnected(w http.ResponseWriter, r *http.Request, conn *connState) {
    writeJSON(w, map[string]string{"holder": conn.current()})
}

func handleLog(w http.ResponseWriter, r *http.Request) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req logRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    level := strings.ToLower(strings.TrimSpace(req.Level))
    switch level { case "info", "warn", "error": default: level = "info" }
    log.Printf("[client/%s] %s", level, req.Message)
    w.WriteHeader(http.StatusNoContent)
}

func handleExec(w http.ResponseWriter, r *http.Request, db *execDB) {
    origin := r.Header.Get("Origin")
    if !originAllowed(origin) {
        log.Printf("exec REJECTED origin=%q", origin)
        http.Error(w, "forbidden origin", http.StatusForbidden)
        return
    }
    body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req execRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    if strings.TrimSpace(req.Command) == "" { http.Error(w, "empty command", http.StatusBadRequest); return }

    claimed, err := db.Claim(req.URL, req.ID)
    if err != nil { log.Printf("exec db.claim error: %v", err) }
    if !claimed {
        log.Printf("exec SKIPPED (already executed) url=%q id=%q", truncate(req.URL, 60), req.ID)
        writeJSON(w, execResponse{AlreadyExecuted: true, ExitCode: 0})
        return
    }

    timeout := time.Duration(req.TimeoutMs) * time.Millisecond
    if timeout <= 0 { timeout = defaultTimeoutMs * time.Millisecond }
    if timeout > maxTimeoutMs*time.Millisecond { timeout = maxTimeoutMs * time.Millisecond }

    ctx, cancel := context.WithTimeout(r.Context(), timeout)
    defer cancel()

    name, args := shellCommand(req.Command)
    cmd := exec.CommandContext(ctx, name, args...)
    if req.Cwd != "" { cmd.Dir = req.Cwd }

    var stdout, stderr strings.Builder
    cmd.Stdout = &stdout
    cmd.Stderr = &stderr

    start := time.Now()
    runErr := cmd.Run()
    dur := time.Since(start).Milliseconds()

    resp := execResponse{Stdout: stdout.String(), Stderr: stderr.String(), DurationMs: dur}
    switch {
    case ctx.Err() == context.DeadlineExceeded:
        resp.TimedOut = true
        resp.ExitCode = -1
    case runErr != nil:
        if ee, ok := runErr.(*exec.ExitError); ok { resp.ExitCode = ee.ExitCode() } else { resp.ExitCode = -1; resp.Error = runErr.Error() }
    }

    log.Printf("exec origin=%q url=%q id=%q cmd=%q exit=%d dur=%dms",
        origin, truncate(req.URL, 60), req.ID, truncate(req.Command, 60), resp.ExitCode, dur)
    writeJSON(w, resp)
}

// ---- main ----

func main() {
    var (
        portFlag = flag.Int("port", 7788, "TCP port")
        hostFlag = flag.String("host", "127.0.0.1", "bind interface; keep 127.0.0.1")
        dataFlag = flag.String("data", "", "data dir (default: <exe-dir>/data)")
    )
    flag.Parse()

    if *hostFlag != "127.0.0.1" && *hostFlag != "localhost" && *hostFlag != "::1" {
        log.Printf("WARNING: binding to %s is not loopback", *hostFlag)
    }

    dataDir := *dataFlag
    if dataDir == "" {
        if exe, err := os.Executable(); err == nil {
            dataDir = filepath.Join(filepath.Dir(exe), "data")
        } else {
            dataDir = "data"
        }
    }
    if err := os.MkdirAll(dataDir, 0o755); err != nil {
        log.Printf("WARN: could not create data dir %s: %v", dataDir, err)
        dataDir = ""
    }
    var db *execDB
    if dataDir != "" {
        var err error
        db, err = newExecDB(filepath.Join(dataDir, "executed.json"))
        if err != nil { log.Printf("WARN: could not open dedup db: %v", err); db = nil }
    }
    if db == nil { db = &execDB{data: make(map[string]map[string]bool)} }

    conn := &connState{}

    ln, err := net.Listen("tcp", fmt.Sprintf("%s:%d", *hostFlag, *portFlag))
    if err != nil { log.Fatalf("listen: %v", err) }
    actualPort := ln.Addr().(*net.TCPAddr).Port

    mux := http.NewServeMux()
    mux.HandleFunc("/ping", cors(func(w http.ResponseWriter, r *http.Request) {
        writeJSON(w, map[string]any{"ok": true, "version": version})
    }))
    mux.HandleFunc("/init", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleInit(w, r, db, conn)
    }))
    mux.HandleFunc("/connect", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleConnect(w, r, conn)
    }))
    mux.HandleFunc("/disconnect", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleDisconnect(w, r, conn)
    }))
    mux.HandleFunc("/heartbeat", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleHeartbeat(w, r, conn)
    }))
    mux.HandleFunc("/connected", cors(func(w http.ResponseWriter, r *http.Request) {
        handleConnected(w, r, conn)
    }))
    mux.HandleFunc("/log", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleLog(w, r)
    }))
    mux.HandleFunc("/exec", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleExec(w, r, db)
    }))

    srv := &http.Server{
        Handler:      mux,
        ReadTimeout:  10 * time.Second,
        WriteTimeout: maxTimeoutMs*time.Millisecond + 10*time.Second,
    }

    log.Printf("bds-exec-bridge %s on http://%s:%d", version, *hostFlag, actualPort)
    log.Printf("data dir: %s", dataDir)
    log.Printf("ready")

    if err := srv.Serve(ln); err != nil && err != http.ErrServerClosed { log.Fatalf("serve: %v", err) }
}