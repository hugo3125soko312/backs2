package main

import (
	"encoding/json"
	"net/http"
	"os/exec"
	"runtime"
	"strings"
	"time"
)

const (
    ansiReset  = "\033[0m"
    ansiCyan   = "\033[36m"
    ansiGreen  = "\033[32m"
    ansiWhite  = "\033[97m"
    ansiGray   = "\033[90m"
    ansiRed    = "\033[31m"
    ansiYellow = "\033[33m"
    ansiClear  = "\033[2J\033[H"
)

func originAllowed(origin string) bool {
    if origin == "" { return true }
    if allowedOrigins[origin] { return true }
    if strings.HasPrefix(origin, "chrome-extension://") || strings.HasPrefix(origin, "moz-extension://") {
        return true
    }
    return false
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
        if r.Method == http.MethodOptions { w.WriteHeader(http.StatusNoContent); return }
        next(w, r)
    }
}

func truncate(s string, n int) string {
    s = strings.ReplaceAll(s, "\n", " ")
    s = strings.TrimSpace(s)
    if len(s) <= n { return s }
    return s[:n] + "..."
}

func writeJSON(w http.ResponseWriter, v any) {
    w.Header().Set("Content-Type", "application/json")
    _ = json.NewEncoder(w).Encode(v)
}

func nowUTC() time.Time { return time.Now().UTC() }

func wrapText(s string, width int) string {
    var out []string
    for _, raw := range strings.Split(s, "\n") {
        line := strings.TrimRight(raw, "\r")
        if line == "" { out = append(out, ""); continue }
        words := strings.Fields(line)
        cur := ""
        for _, w := range words {
            if cur == "" {
                cur = w
            } else if len(cur)+1+len(w) <= width {
                cur += " " + w
            } else {
                out = append(out, cur)
                cur = w
            }
        }
        if cur != "" { out = append(out, cur) }
    }
    return strings.Join(out, "\n")
}