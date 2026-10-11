// exec_bridge.go
//
// Local HTTP daemon for the BDS exec-bridge userscript.
// v0.8.0 - two-process model:
//
//	exec_bridge.exe            -> logger daemon (HTTP, no stdin)
//	exec_bridge.exe -qterm     -> interactive Q&A terminal
package main

import (
	"bufio"
	"bytes"
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
    version          = "0.8.0"
    defaultTimeoutMs = 30_000
    maxTimeoutMs     = 300_000
    connTTL          = 30 * time.Second

    ansiReset = "\033[0m"
    ansiCyan  = "\033[36m"
    ansiWhite = "\033[97m"
    ansiGray  = "\033[90m"
    ansiRed   = "\033[31m"
    ansiClear = "\033[2J\033[H"
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

// ---- exec dedup ----

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

// ---- question queue ----

type questionItem struct {
    URL        string
    ID         string
    Question   string
    Answer     string
    AnsweredAt time.Time
}

type questionQueue struct {
    mu        sync.Mutex
    pending   []questionItem
    answers   map[string]map[string]questionItem
    batchOpen bool
}

func newQuestionQueue() *questionQueue {
    return &questionQueue{answers: make(map[string]map[string]questionItem)}
}

func (qq *questionQueue) Enqueue(url, id, question string) {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    qq.batchOpen = true
    qq.pending = append(qq.pending, questionItem{URL: url, ID: id, Question: question})
    fmt.Printf("%s[queued %s] %s%s\n", ansiGray, id, truncate(question, 60), ansiReset)
}

func (qq *questionQueue) Next() (questionItem, bool) {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    if len(qq.pending) == 0 { return questionItem{}, false }
    return qq.pending[0], true
}

func (qq *questionQueue) SubmitAnswer(id, answer string) bool {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    for i, item := range qq.pending {
        if item.ID == id {
            item.Answer = answer
            item.AnsweredAt = time.Now().UTC()
            if qq.answers[item.URL] == nil { qq.answers[item.URL] = make(map[string]questionItem) }
            qq.answers[item.URL][item.ID] = item
            qq.pending = append(qq.pending[:i], qq.pending[i+1:]...)
            if len(qq.pending) == 0 {
                qq.batchOpen = false
                fmt.Printf("%s[all questions answered - ready for delivery]%s\n", ansiGray, ansiReset)
            }
            return true
        }
    }
    return false
}

func (qq *questionQueue) Ready(url string) (bool, []questionItem) {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    items := make([]questionItem, 0)
    for _, a := range qq.answers[url] { items = append(items, a) }
    return !qq.batchOpen && len(items) > 0, items
}

func (qq *questionQueue) Clear(url string) int {
    qq.mu.Lock()
    defer qq.mu.Unlock()
    n := len(qq.answers[url])
    delete(qq.answers, url)
    return n
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
    if c.holder == id { c.holder = ""; return true }
    return false
}
func (c *connState) heartbeat(id string) bool {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder == id { c.lastSeen = time.Now(); return true }
    return false
}
func (c *connState) current() string {
    c.mu.Lock(); defer c.mu.Unlock()
    if c.holder != "" && time.Since(c.lastSeen) > connTTL { return "" }
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
    Ok     bool   `json:"ok"`
    Holder string `json:"holder,omitempty"`
    Reason string `json:"reason,omitempty"`
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
type questionRequest struct {
    URL      string `json:"url"`
    ID       string `json:"id"`
    Question string `json:"question"`
}
type answerPostRequest struct {
    ID     string `json:"id"`
    Answer string `json:"answer"`
}
type answerRow struct {
    ID          string `json:"id"`
    Question    string `json:"question"`
    Answer      string `json:"answer"`
    SubmittedAt string `json:"submitted_at"`
}
type clearAnswersRequest struct {
    URL string   `json:"url"`
    IDs []string `json:"ids"`
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
    s = strings.ReplaceAll(s, "\n", " ")
    s = strings.TrimSpace(s)
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

func handleQuestion(w http.ResponseWriter, r *http.Request, qq *questionQueue) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req questionRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    if req.URL == "" || req.ID == "" || strings.TrimSpace(req.Question) == "" {
        http.Error(w, "url, id, question required", http.StatusBadRequest)
        return
    }
    qq.Enqueue(req.URL, req.ID, req.Question)
    log.Printf("question queued url=%q id=%q qlen=%d", truncate(req.URL, 40), req.ID, len(req.Question))
    writeJSON(w, map[string]any{"ok": true})
}

func handleQuestionPending(w http.ResponseWriter, r *http.Request, qq *questionQueue) {
    item, ok := qq.Next()
    if !ok {
        writeJSON(w, map[string]any{"pending": false})
        return
    }
    writeJSON(w, map[string]any{
        "pending":  true,
        "id":       item.ID,
        "question": item.Question,
    })
}

func handleQuestionAnswer(w http.ResponseWriter, r *http.Request, qq *questionQueue) {
    // No origin check -- the qterm client runs locally and sends no Origin.
    if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req answerPostRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    if req.ID == "" || strings.TrimSpace(req.Answer) == "" {
        http.Error(w, "id, answer required", http.StatusBadRequest)
        return
    }
    if !qq.SubmitAnswer(req.ID, req.Answer) {
        http.Error(w, "no pending question with that id", http.StatusNotFound)
        return
    }
    log.Printf("answer stored id=%q alen=%d", req.ID, len(req.Answer))
    writeJSON(w, map[string]any{"ok": true})
}

func handleListAnswers(w http.ResponseWriter, r *http.Request, qq *questionQueue) {
    url := r.URL.Query().Get("url")
    ready, items := qq.Ready(url)
    rows := make([]answerRow, 0, len(items))
    for _, it := range items {
        rows = append(rows, answerRow{
            ID:          it.ID,
            Question:    it.Question,
            Answer:      it.Answer,
            SubmittedAt: it.AnsweredAt.Format(time.RFC3339),
        })
    }
    writeJSON(w, map[string]any{"ready": ready, "answers": rows})
}

func handleClearAnswers(w http.ResponseWriter, r *http.Request, qq *questionQueue) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req clearAnswersRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    n := qq.Clear(req.URL)
    log.Printf("answers cleared url=%q count=%d", truncate(req.URL, 40), n)
    writeJSON(w, map[string]any{"ok": true, "cleared": n})
}

func handleLog(w http.ResponseWriter, r *http.Request) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req logRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    level := strings.ToLower(strings.TrimSpace(req.Level))
    switch level { case "info", "warn", "error": default: level = "info" }
    log.Printf("[client/%s] %s", level, truncate(req.Message, 120))
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
        log.Printf("exec SKIPPED (already executed) url=%q id=%q", truncate(req.URL, 40), req.ID)
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

    log.Printf("exec url=%q id=%q cmd=%q exit=%d dur=%dms",
        truncate(req.URL, 40), req.ID, truncate(req.Command, 60), resp.ExitCode, dur)
    writeJSON(w, resp)
}

// ---- qterm client ----

func wrapText(s string, width int) string {
    var out []string
    for _, raw := range strings.Split(s, "\n") {
        line := strings.TrimRight(raw, "\r")
        if line == "" {
            out = append(out, "")
            continue
        }
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
        if cur != "" {
            out = append(out, cur)
        }
    }
    return strings.Join(out, "\n")
}
func runQTerm(host string, port int) {
    base := fmt.Sprintf("http://%s:%d", host, port)

    fmt.Printf("%sconnecting to %s ...%s\n", ansiGray, base, ansiReset)
    var ready bool
    for i := 0; i < 40; i++ {
        resp, err := http.Get(base + "/ping")
        if err == nil { resp.Body.Close(); ready = true; break }
        time.Sleep(250 * time.Millisecond)
    }
    if !ready {
        fmt.Printf("%sdaemon not reachable at %s. start the daemon first.%s\n", ansiRed, base, ansiReset)
        fmt.Println("press Enter to exit.")
        bufio.NewReader(os.Stdin).ReadString('\n')
        return
    }

    reader := bufio.NewReader(os.Stdin)
    lastShown := ""

    redrawIdle := func() {
        fmt.Print(ansiClear)
        fmt.Printf("%s   BDS Q&A terminal%s\n", ansiGray, ansiReset)
        fmt.Printf("%s   daemon: %s%s\n", ansiGray, base, ansiReset)
        fmt.Printf("%s   waiting for questions...%s\n", ansiGray, ansiReset)
    }
    redrawIdle()

    for {
        resp, err := http.Get(base + "/question/pending")
        if err != nil {
            fmt.Printf("\r%s[bridge unreachable]%s   ", ansiRed, ansiReset)
            time.Sleep(1000 * time.Millisecond)
            continue
        }
        var pr struct {
            Pending  bool   `json:"pending"`
            ID       string `json:"id"`
            Question string `json:"question"`
        }
        err = json.NewDecoder(resp.Body).Decode(&pr)
        resp.Body.Close()
        if err != nil { time.Sleep(500 * time.Millisecond); continue }

        if !pr.Pending || pr.ID == lastShown {
            time.Sleep(500 * time.Millisecond)
            continue
        }

        fmt.Print(ansiClear)
        fmt.Printf("%s   BDS Q&A terminal%s\n\n", ansiGray, ansiReset)
        fmt.Printf("%s[DeepSeek asks %s]%s\n", ansiCyan, pr.ID, ansiReset)
        fmt.Println(wrapText(pr.Question, 76))
        fmt.Println()
        fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)
        fmt.Printf("%s                 YOUR ANSWER%s\n", ansiCyan, ansiReset)
        fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)
        fmt.Printf("%s Type or paste below. Blank line submits.%s\n", ansiGray, ansiReset)
        fmt.Printf("%s /cancel aborts, /skip skips.%s\n", ansiGray, ansiReset)
        fmt.Println()

        time.Sleep(80 * time.Millisecond)

        answer, action := readMultilineAnswer(reader)

        fmt.Println()
        fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)

        switch action {
        case "eof":
            return
        case "cancel":
            fmt.Printf("%s                 CANCELLED%s\n", ansiCyan, ansiReset)
            fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)
            time.Sleep(500 * time.Millisecond)
            lastShown = pr.ID
            redrawIdle()
            continue
        case "skip":
            answer = "(skipped)"
        case "send":
            if strings.TrimSpace(answer) == "" {
                answer = "(empty answer)"
            }
        }

        nLines := len(strings.Split(answer, "\n"))

        fmt.Printf("%s                 SUBMITTED%s\n", ansiCyan, ansiReset)
        fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)
        for _, l := range strings.Split(answer, "\n") {
            fmt.Println(l)
        }
        fmt.Printf("%s===============================================%s\n", ansiCyan, ansiReset)
        fmt.Printf("%s   sent %d line(s), %d char(s)%s\n", ansiGray, nLines, len(answer), ansiReset)

        body, _ := json.Marshal(answerPostRequest{ID: pr.ID, Answer: answer})
        req, _ := http.NewRequest("POST", base+"/question/answer", bytes.NewReader(body))
        req.Header.Set("Content-Type", "application/json")
        hr, e := http.DefaultClient.Do(req)
        if e != nil {
            fmt.Printf("%s   post failed: %s%s\n", ansiRed, e.Error(), ansiReset)
            fmt.Printf("%s   press Enter to continue.%s", ansiGray, ansiReset)
            reader.ReadString('\n')
        } else {
            hr.Body.Close()
            time.Sleep(700 * time.Millisecond)
        }
        lastShown = pr.ID
        redrawIdle()
    }
}

func readMultilineAnswer(reader *bufio.Reader) (string, string) {
    var lines []string
    for {
        line, err := reader.ReadString('\n')
        if err != nil {
            if len(lines) > 0 {
                return strings.Join(lines, "\n"), "send"
            }
            return "", "eof"
        }
        trimmed := strings.TrimRight(line, "\r\n")
        switch trimmed {
        case "/send":
            return strings.Join(lines, "\n"), "send"
        case "/skip":
            return "", "skip"
        case "/cancel":
            return "", "cancel"
        }
        if trimmed == "" {
            if len(lines) == 0 { continue }
            return strings.Join(lines, "\n"), "send"
        }
        lines = append(lines, trimmed)
    }
}
// ---- main ----

func main() {
    var (
        portFlag  = flag.Int("port", 7788, "TCP port")
        hostFlag  = flag.String("host", "127.0.0.1", "bind interface; keep 127.0.0.1")
        dataFlag  = flag.String("data", "", "data dir (default: <exe-dir>/data)")
        qtermFlag = flag.Bool("qterm", false, "run as interactive Q&A terminal client")
    )
    flag.Parse()

    if *qtermFlag {
        runQTerm(*hostFlag, *portFlag)
        return
    }

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

    qq := newQuestionQueue()
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
    mux.HandleFunc("/question", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleQuestion(w, r, qq)
    }))
    mux.HandleFunc("/question/pending", cors(func(w http.ResponseWriter, r *http.Request) {
        handleQuestionPending(w, r, qq)
    }))
    mux.HandleFunc("/question/answer", cors(func(w http.ResponseWriter, r *http.Request) {
        handleQuestionAnswer(w, r, qq)
    }))
    mux.HandleFunc("/answers", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method == http.MethodGet { handleListAnswers(w, r, qq); return }
        http.Error(w, "method not allowed", http.StatusMethodNotAllowed)
    }))
    mux.HandleFunc("/answers/clear", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleClearAnswers(w, r, qq)
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
    log.Printf("ready - logging only; Q&A in the separate qterm window")

    if err := srv.Serve(ln); err != nil && err != http.ErrServerClosed { log.Fatalf("serve: %v", err) }
}