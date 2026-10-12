package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"io"
	"log"
	"net/http"
	"os/exec"
	"strings"
	"time"
)

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
    DeniedByFilter  string `json:"denied_by_filter,omitempty"`
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
type approvalAnswerRequest struct {
    Token    string `json:"token"`
    Approved bool   `json:"approved"`
}
type logRequest struct {
    Level   string `json:"level"`
    Message string `json:"message"`
}

func newToken() string {
    b := make([]byte, 8)
    rand.Read(b)
    return hex.EncodeToString(b)
}

// ---- server ----

func BuildServer(st *storage, conn *connState, qq *questionQueue, aq *approvalQueue, cfg *BridgeConfig) *http.Server {
    mux := http.NewServeMux()

    mux.HandleFunc("/ping", cors(func(w http.ResponseWriter, r *http.Request) {
        writeJSON(w, map[string]any{"ok": true, "version": version})
    }))
    mux.HandleFunc("/init", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleInit(w, r, st, conn)
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
        writeJSON(w, map[string]string{"holder": conn.current()})
    }))
    mux.HandleFunc("/question", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleQuestion(w, r, qq)
    }))
    mux.HandleFunc("/question/pending", cors(func(w http.ResponseWriter, r *http.Request) {
        handleQuestionPending(w, r, qq)
    }))
    mux.HandleFunc("/question/answer", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
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
    mux.HandleFunc("/approval/pending", cors(func(w http.ResponseWriter, r *http.Request) {
        handleApprovalPending(w, r, aq)
    }))
    mux.HandleFunc("/approval/answer", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleApprovalAnswer(w, r, aq)
    }))
    mux.HandleFunc("/log", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleLog(w, r)
    }))
    mux.HandleFunc("/exec", cors(func(w http.ResponseWriter, r *http.Request) {
        if r.Method != http.MethodPost { http.Error(w, "method not allowed", http.StatusMethodNotAllowed); return }
        handleExec(w, r, st, aq, cfg)
    }))

    return &http.Server{
        Handler:      mux,
        ReadTimeout:  10 * time.Second,
        WriteTimeout: time.Duration(maxTimeoutMs)*time.Millisecond + time.Duration(approvalTTL) + 10*time.Second,
    }
}

// ---- handlers ----

func handleInit(w http.ResponseWriter, r *http.Request, st *storage, conn *connState) {
    if !originAllowed(r.Header.Get("Origin")) { http.Error(w, "forbidden origin", http.StatusForbidden); return }
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req initRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    ids := st.Exec.List(req.URL)
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
    if !conn.heartbeat(req.ClientID) {
        w.WriteHeader(http.StatusConflict)
        writeJSON(w, connResponse{Ok: false, Reason: "not the holder"})
        return
    }
    w.WriteHeader(http.StatusNoContent)
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
    if !ok { writeJSON(w, map[string]any{"pending": false}); return }
    writeJSON(w, map[string]any{"pending": true, "id": item.ID, "question": item.Question})
}

func handleQuestionAnswer(w http.ResponseWriter, r *http.Request, qq *questionQueue) {
    body, err := io.ReadAll(io.LimitReader(r.Body, 64*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req answerPostRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    if req.ID == "" || strings.TrimSpace(req.Answer) == "" {
        http.Error(w, "id, answer required", http.StatusBadRequest); return
    }
    if !qq.SubmitAnswer(req.ID, req.Answer) {
        http.Error(w, "no pending question with that id", http.StatusNotFound); return
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

func handleApprovalPending(w http.ResponseWriter, r *http.Request, aq *approvalQueue) {
    req := aq.Next()
    if req == nil { writeJSON(w, map[string]any{"pending": false}); return }
    writeJSON(w, map[string]any{
        "pending": true,
        "token":   req.Token,
        "kind":    req.Kind,
        "id":      req.ID,
        "rule":    req.Rule,
        "reason":  req.Reason,
        "preview": req.Preview,
    })
}

func handleApprovalAnswer(w http.ResponseWriter, r *http.Request, aq *approvalQueue) {
    body, err := io.ReadAll(io.LimitReader(r.Body, 4*1024))
    if err != nil { http.Error(w, "read body", http.StatusBadRequest); return }
    var req approvalAnswerRequest
    if err := json.Unmarshal(body, &req); err != nil { http.Error(w, "decode json", http.StatusBadRequest); return }
    ok := aq.Resolve(req.Token, req.Approved)
    log.Printf("approval answer token=%q approved=%v ok=%v", req.Token, req.Approved, ok)
    writeJSON(w, map[string]any{"ok": ok})
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

func handleExec(w http.ResponseWriter, r *http.Request, st *storage, aq *approvalQueue, cfg *BridgeConfig) {
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

    claimed, err := st.Exec.Claim(req.URL, req.ID)
    if err != nil { log.Printf("exec db.claim error: %v", err) }
    if !claimed {
        log.Printf("exec SKIPPED (already executed) url=%q id=%q", truncate(req.URL, 40), req.ID)
        writeJSON(w, execResponse{AlreadyExecuted: true, ExitCode: 0})
        return
    }

    // Filter check. If the command matches a rule, ask the human via qterm.
    if rule := cfg.RequiresApproval(req.Command); rule != nil {
        token := newToken()
        ar := &approvalRequest{
            Token:   token,
            URL:     req.URL,
            ID:      req.ID,
            Kind:    "exec",
            Rule:    rule.Name,
            Reason:  rule.Reason,
            Preview: req.Command,
            Result:  make(chan bool, 1),
            Created: time.Now(),
        }
        aq.Submit(ar)
        log.Printf("approval pending rule=%q id=%q", rule.Name, req.ID)

        select {
        case approved := <-ar.Result:
            log.Printf("approval resolved token=%q rule=%q approved=%v", token, rule.Name, approved)
            if !approved {
                writeJSON(w, execResponse{
                    ExitCode:       -1,
                    DeniedByFilter: rule.Name,
                    Error:          "denied by user (" + rule.Reason + ")",
                })
                return
            }
        case <-r.Context().Done():
            aq.Resolve(token, false)
            log.Printf("approval cancelled (client gone) token=%q", token)
            return
        case <-time.After(time.Duration(approvalTTL)):
            aq.Resolve(token, false)
            log.Printf("approval timeout token=%q", token)
            writeJSON(w, execResponse{
                ExitCode:       -1,
                DeniedByFilter: rule.Name,
                Error:          "approval timeout",
            })
            return
        }
    }

    timeout := time.Duration(req.TimeoutMs) * time.Millisecond
    if timeout <= 0 { timeout = time.Duration(defaultTimeoutMs) * time.Millisecond }
    if timeout > time.Duration(maxTimeoutMs)*time.Millisecond { timeout = time.Duration(maxTimeoutMs) * time.Millisecond }

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

// silence unused-import warnings for bytes (used in qterm.go, imported here as belt-and-braces)
var _ = bytes.NewReader