// exec_bridge.go - daemon entry point.
//
// Usage:
//
//	exec_bridge.exe             run as HTTP daemon
//	exec_bridge.exe -qterm      run as interactive Q&A/approval terminal
package main

import (
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"path/filepath"
)

const (
    version          = "0.10.0"
    defaultTimeoutMs = 30_000
    maxTimeoutMs     = 300_000
    connTTL          = 30_000_000_000 // 30s in ns for time.Duration math
    approvalTTL      = 240_000_000_000 // 4 min
)

var allowedOrigins = map[string]bool{
    "https://chat.deepseek.com": true,
    "https://www.deepseek.com":  true,
    "https://deepseek.com":      true,
}

func main() {
    var (
        portFlag  = flag.Int("port", 7788, "TCP port")
        hostFlag  = flag.String("host", "127.0.0.1", "bind interface; keep 127.0.0.1")
        dataFlag  = flag.String("data", "", "data dir (default: <exe-dir>/data)")
        confFlag  = flag.String("config", "", "config path (default: <exe-dir>/exec_bridge.config.json)")
        qtermFlag = flag.Bool("qterm", false, "run as interactive Q&A/approval terminal")
    )
    flag.Parse()

    exeDir := ""
    if exe, err := os.Executable(); err == nil {
        exeDir = filepath.Dir(exe)
    }

    if *qtermFlag {
        runQTerm(*hostFlag, *portFlag)
        return
    }

    if *hostFlag != "127.0.0.1" && *hostFlag != "localhost" && *hostFlag != "::1" {
        log.Printf("WARNING: binding to %s is not loopback", *hostFlag)
    }

    dataDir := *dataFlag
    if dataDir == "" {
        if exeDir != "" {
            dataDir = filepath.Join(exeDir, "data")
        } else {
            dataDir = "data"
        }
    }
    if err := os.MkdirAll(dataDir, 0o755); err != nil {
        log.Printf("WARN: could not create data dir %s: %v", dataDir, err)
        dataDir = ""
    }

    configPath := *confFlag
    if configPath == "" {
        if exeDir != "" {
            configPath = filepath.Join(exeDir, "exec_bridge.config.json")
        } else {
            configPath = "exec_bridge.config.json"
        }
    }
    cfg := LoadConfig(configPath)
    log.Printf("config: %s (%d filter rule(s))", configPath, len(cfg.Rules))
    for _, r := range cfg.Rules {
        log.Printf("  rule: %s", r.Name)
    }

    st := NewStorage(dataDir)
    conn := &connState{}
    qq := newQuestionQueue()
    aq := newApprovalQueue()

    ln, err := net.Listen("tcp", fmt.Sprintf("%s:%d", *hostFlag, *portFlag))
    if err != nil { log.Fatalf("listen: %v", err) }
    actualPort := ln.Addr().(*net.TCPAddr).Port

    srv := BuildServer(st, conn, qq, aq, cfg)

    log.Printf("bds-exec-bridge %s on http://%s:%d", version, *hostFlag, actualPort)
    log.Printf("data dir: %s", dataDir)
    log.Printf("ready - approval gate active for matching commands")

    if err := srv.Serve(ln); err != nil && err.Error() != "http: Server closed" {
        log.Fatalf("serve: %v", err)
    }
}