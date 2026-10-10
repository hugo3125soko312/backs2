// exec_bridge.go
//
// Local HTTP daemon that executes shell commands on behalf of a browser
// userscript attached to the DeepSeek chat page.
//
// Wire protocol
// -------------
// GET  /ping   always allowed, no origin check.
//
//	Returns {"ok":true,"version":"0.3.0"}.
//
// POST /exec   requires an acceptable Origin header.
//
//	request:
//	  { "command": "...", "cwd": "...", "timeout_ms": 30000 }
//	response:
//	  { "stdout": "...", "stderr": "...", "exit_code": 0,
//	    "duration_ms": 123, "timed_out": false, "error": "" }
//
// POST /log    requires an acceptable Origin header.
//
//	request:
//	  { "level": "info|warn|error", "message": "..." }
//	response: 204 No Content.
//	Prints to daemon stdout as: [client/<level>] <message>
//
// Origin policy
// -------------
// A POST is accepted only when the Origin header is absent (local clients
// such as curl) or matches a known chat or extension origin. Browsers always
// send Origin on cross-origin requests and cannot forge it from JavaScript,
// so this blocks a malicious page from silently shelling out. A local
// malicious process can spoof the header -- if that is your threat model,
// this tool is the wrong shape.
//
// Bind address defaults to 127.0.0.1 and must stay loopback.
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
	"os/exec"
	"runtime"
	"strings"
	"time"
)

const (
    version          = "0.3.0"
    defaultTimeoutMs = 30_000
    maxTimeoutMs     = 300_000
)

var allowedOrigins = map[string]bool{
    "https://chat.deepseek.com": true,
    "https://www.deepseek.com":  true,
    "https://deepseek.com":      true,
}

func originAllowed(origin string) bool {
    if origin == "" {
        return true
    }
    if allowedOrigins[origin] {
        return true
    }
    if strings.HasPrefix(origin, "chrome-extension://") || strings.HasPrefix(origin, "moz-extension://") {
        return true
    }
    return false
}

type execRequest struct {
    Command   string `json:"command"`
    Cwd       string `json:"cwd,omitempty"`
    TimeoutMs int    `json:"timeout_ms,omitempty"`
}

type execResponse struct {
    Stdout     string `json:"stdout"`
    Stderr     string `json:"stderr"`
    ExitCode   int    `json:"exit_code"`
    DurationMs int64  `json:"duration_ms"`
    TimedOut   bool   `json:"timed_out"`
    Error      string `json:"error,omitempty"`
}

type logRequest struct {
    Level   string `json:"level"`
    Message string `json:"message"`
}

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
        if r.Method == http.MethodOptions {
            w.WriteHeader(http.StatusNoContent)
            return
        }
        next(w, r)
    }
}

func truncate(s string, n int) string {
    if len(s) <= n {
        return s
    }
    return s[:n] + "..."
}

func handleLog(w http.ResponseWriter, r *http.Request) {
    origin := r.Header.Get("Origin")
    if !originAllowed(origin) {
        http.Error(w, "forbidden origin", http.StatusForbidden)
        return
    }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil {
        http.Error(w, "read body: "+err.Error(), http.StatusBadRequest)
        return
    }
    var req logRequest
    if err := json.Unmarshal(body, &req); err != nil {
        http.Error(w, "decode json: "+err.Error(), http.StatusBadRequest)
        return
    }
    level := strings.ToLower(strings.TrimSpace(req.Level))
    switch level {
    case "info", "warn", "error":
    default:
        level = "info"
    }
    log.Printf("[client/%s] %s", level, req.Message)
    w.WriteHeader(http.StatusNoContent)
}

func handleExec(w http.ResponseWriter, r *http.Request) {
    origin := r.Header.Get("Origin")
    if !originAllowed(origin) {
        log.Printf("exec REJECTED origin=%q", origin)
        http.Error(w, "forbidden origin", http.StatusForbidden)
        return
    }

    body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20))
    if err != nil {
        http.Error(w, "read body: "+err.Error(), http.StatusBadRequest)
        return
    }

    var req execRequest
    if err := json.Unmarshal(body, &req); err != nil {
        http.Error(w, "decode json: "+err.Error(), http.StatusBadRequest)
        return
    }
    if strings.TrimSpace(req.Command) == "" {
        http.Error(w, "empty command", http.StatusBadRequest)
        return
    }

    timeout := time.Duration(req.TimeoutMs) * time.Millisecond
    if timeout <= 0 {
        timeout = defaultTimeoutMs * time.Millisecond
    }
    if timeout > maxTimeoutMs*time.Millisecond {
        timeout = maxTimeoutMs * time.Millisecond
    }

    ctx, cancel := context.WithTimeout(r.Context(), timeout)
    defer cancel()

    name, args := shellCommand(req.Command)
    cmd := exec.CommandContext(ctx, name, args...)
    if req.Cwd != "" {
        cmd.Dir = req.Cwd
    }

    var stdout, stderr strings.Builder
    cmd.Stdout = &stdout
    cmd.Stderr = &stderr

    start := time.Now()
    runErr := cmd.Run()
    dur := time.Since(start).Milliseconds()

    resp := execResponse{
        Stdout:     stdout.String(),
        Stderr:     stderr.String(),
        DurationMs: dur,
    }
    switch {
    case ctx.Err() == context.DeadlineExceeded:
        resp.TimedOut = true
        resp.ExitCode = -1
    case runErr != nil:
        if ee, ok := runErr.(*exec.ExitError); ok {
            resp.ExitCode = ee.ExitCode()
        } else {
            resp.ExitCode = -1
            resp.Error = runErr.Error()
        }
    }

    log.Printf("exec origin=%q cmd=%q exit=%d dur=%dms timedout=%v",
        origin, truncate(req.Command, 80), resp.ExitCode, dur, resp.TimedOut)

    w.Header().Set("Content-Type", "application/json")
    _ = json.NewEncoder(w).Encode(resp)
}

func main() {
    var (
        portFlag = flag.Int("port", 7788, "TCP port")
        hostFlag = flag.String("host", "127.0.0.1", "bind interface; keep 127.0.0.1")
    )
    flag.Parse()

    if *hostFlag != "127.0.0.1" && *hostFlag != "localhost" && *hostFlag != "::1" {
        log.Printf("WARNING: binding to %s is not loopback; command execution will be reachable from the network", *hostFlag)
    }

    ln, err := net.Listen("tcp", fmt.Sprintf("%s:%d", *hostFlag, *portFlag))
    if err != nil {
        log.Fatalf("listen on %s:%d: %v", *hostFlag, *portFlag, err)
    }
    actualPort := ln.Addr().(*net.TCPAddr).Port

    mux := http.NewServeMux()
    mux.HandleFunc("/ping", cors(func(w http.ResponseWriter, r *http.Request) {
        w.Header().Set("Content-Type", "application/json")
        _ = json.NewEncoder(w).Encode(map[string]any{"ok": true, "version": version})
    }))
    mux.HandleFunc("/log", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost {
            http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
            return
        }
        handleLog(w, r)
    }))
    mux.HandleFunc("/exec", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost {
            http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
            return
        }
        handleExec(w, r)
    }))

    srv := &http.Server{
        Handler:      mux,
        ReadTimeout:  10 * time.Second,
        WriteTimeout: maxTimeoutMs*time.Millisecond + 10*time.Second,
    }

    log.Printf("bds-exec-bridge %s listening on http://%s:%d", version, *hostFlag, actualPort)
    log.Printf("origin allowlist: deepseek.com + chrome-extension:// + moz-extension:// + no-origin")
    log.Printf("ready")
    log.Printf("awaiting client logs on /log and commands on /exec")

    if err := srv.Serve(ln); err != nil && err != http.ErrServerClosed {
        log.Fatalf("serve: %v", err)
    }
}